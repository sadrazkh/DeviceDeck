// Deleting photos in the mock (round 4, photodelete; a transfer job since v0.4.1): photos.deleteSupport,
// photos.deleteItems and photos.deleteJob, with the same rules as the host: every item matched first, nothing deleted
// unless all match, batches of five, a result per item. The job runs in stages on the mock's clock: copying (when
// asked), reading the iPhone's camera roll (N of M objects), deleting (N of the items), checking on the iPhone.
// ?photodelete=iCloudPhotos|readOnly|locked|notCamera|converted (the iPhone can't delete, for that reason),
// recentlyDeleted (deletes go to Recently Deleted), fails (the third photo of a delete is refused).
// ?deletespeed=slow: a slow camera roll (reading takes about 15 s, a batch 4 s), for watching the stages; crawl (v0.6):
// slower still, for tests that must catch a delete while it runs even on a slow runner.

import type { DeviceCapability, DeviceSummary, TransferJob } from '../types';
import type { PhotoDeleteBlock, PhotoDeleteDestination, PhotoDeleteJobStatus, PhotoDeleteReach, PhotoDeleteResult, PhotoDeleteSupport } from '../photoDeleteTypes';
import type { MethodName, MethodParams, MethodResult } from '../methods';
import { assetsInAlbum, type MockLibrary } from './photos';
import type { JobItem, NewJob, StagedRun } from './transfers';

export const MOCK_PHOTODELETE_SCENARIOS = ['ok', 'iCloudPhotos', 'readOnly', 'locked', 'notCamera', 'converted', 'recentlyDeleted', 'fails'] as const;
export type MockPhotoDeleteScenario = (typeof MOCK_PHOTODELETE_SCENARIOS)[number];

export const MOCK_DELETE_SPEEDS = ['normal', 'slow', 'crawl'] as const;

/** v0.4.2, ?reach=some: every fourth iPhone item (4th, 8th, …, newest first) is one Windows doesn't list. */
export const MOCK_REACH_SCENARIOS = ['all', 'some'] as const;
export type MockReachScenario = (typeof MOCK_REACH_SCENARIOS)[number];
export type MockDeleteSpeed = (typeof MOCK_DELETE_SPEEDS)[number];

const BATCH = 5;
/** Objects Windows lists in the mock iPhone's camera roll (the owner's had 3,258). */
const ROLL_OBJECTS = 3258;

/** Simulated seconds per stage. */
const SPEED: Record<MockDeleteSpeed, { objectsPerSecond: number; batchSeconds: number; checkSeconds: number }> = {
  normal: { objectsPerSecond: 1600, batchSeconds: 0.35, checkSeconds: 1 },
  slow: { objectsPerSecond: 220, batchSeconds: 4, checkSeconds: 2 },
  crawl: { objectsPerSecond: 50, batchSeconds: 30, checkSeconds: 5 },
};

const STOPPED = 'Not tried: the delete was stopped.';
const COPY_INCOMPLETE = 'The copy to the PC didn’t finish, so nothing was deleted.';

const DETAIL: Partial<Record<PhotoDeleteBlock, string>> = {
  iCloudPhotos: '0 of 24 sampled files say they can be deleted (0 don’t say); 41 camera-roll folders. The library has iCloud Photos assets.',
  readOnly: '0 of 24 sampled files say they can be deleted (0 don’t say); 41 camera-roll folders.',
  locked: 'Windows lists the iPhone’s Internal Storage as empty.',
  notCamera: 'Windows lists 2 portable devices (0 from Apple); none has this iPhone’s serial number.',
  converted: 'None of 24 sampled names is a file on the phone, but the same names with another extension are: the phone converts photos for the PC.',
};

type Handler<M extends MethodName> = (params: MethodParams<M>) => MethodResult<M>;

export interface PhotoDeleteContext {
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  device(deviceId: string): DeviceSummary | undefined;
  /** Replaces the phone's summary and announces it (device.changed). */
  setDevice(device: DeviceSummary): void;
  library(deviceId: string): MockLibrary;
  addJob(spec: NewJob): TransferJob;
  job(jobId: string): TransferJob | undefined;
  cancelJob(jobId: string): void;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

interface StartOptions {
  copyFirstTo?: string | null;
  source: 'photos' | 'cleanup';
  skipped?: number;
  /** Called once when the job ends, with what went (Clean-up takes them out of its report). */
  onFinished?: (gone: Set<string>, freedBytes: number) => void;
}

export class MockPhotoDelete {
  scenario: MockPhotoDeleteScenario = 'ok';
  speed: MockDeleteSpeed = 'normal';
  reach: MockReachScenario = 'all';
  /** Where deletes went on each phone this session (what a real iPhone shows after its first delete). */
  private readonly observed = new Map<string, PhotoDeleteDestination>();
  private readonly runs = new Map<string, DeleteRun>();
  /** v0.4.2: each iPhone's read ahead of its camera roll's view: objects read, and whether it still runs. */
  private readonly ahead = new Map<string, { read: number; running: boolean }>();

  constructor(private readonly ctx: PhotoDeleteContext) {}

  /** Starts reading an iPhone's camera roll ahead (when it can delete and isn't read or being read). */
  prefetch(deviceId: string): void {
    const device = this.ctx.device(deviceId);
    if (!device || device.platform !== 'ios' || device.state !== 'ready' || !device.capabilities.includes('photosDelete')) return;
    const current = this.ahead.get(deviceId);
    if (current?.running || current?.read === ROLL_OBJECTS) return;
    this.ahead.set(deviceId, { read: current?.read ?? 0, running: true });
  }

  /** Simulated time passes for the reads ahead. */
  tick(ms: number): void {
    for (const state of this.ahead.values()) {
      if (!state.running) continue;
      state.read = Math.min(ROLL_OBJECTS, state.read + Math.round((ms / 1000) * SPEED[this.speed].objectsPerSecond));
      if (state.read === ROLL_OBJECTS) state.running = false;
    }
  }

  /** A delete stops the read ahead and carries on from what it read. */
  takeReadAhead(deviceId: string): number {
    const state = this.ahead.get(deviceId);
    if (!state) return 0;
    state.running = false;
    return state.read;
  }

  /** A delete read the whole view: what it read stands, as the host's listing cache does. */
  readAll(deviceId: string): void {
    this.ahead.set(deviceId, { read: ROLL_OBJECTS, running: false });
  }

  /** The phone went away or locked: what was read ahead is gone. */
  forget(deviceId: string): void {
    this.ahead.delete(deviceId);
  }

  /** The items Windows doesn't list (?reach=some), fixed when the library is first read so deletes don't shift them. */
  private readonly unlisted = new Map<string, Set<string>>();

  private notListed(deviceId: string): Set<string> {
    let set = this.unlisted.get(deviceId);
    if (!set) {
      const library = this.ctx.library(deviceId);
      // By place in the All album (what Photos shows, newest first): the 4th, 8th, … item, unless it is in iCloud only.
      set = new Set(this.reach === 'some' ? assetsInAlbum(library, null).filter((a, i) => i % 4 === 3 && !a.inCloudOnly).map((a) => a.id) : []);
      this.unlisted.set(deviceId, set);
    }
    return set;
  }

  /** Whether Windows lists this item (a delete of one it doesn't is refused, as on the host). */
  listed(deviceId: string, id: string): boolean {
    return this.ctx.device(deviceId)?.platform !== 'ios' || !this.notListed(deviceId).has(id);
  }

  /** photos.deleteReach: from a complete read ahead only; starting one when none runs. */
  reachOf(deviceId: string): PhotoDeleteReach {
    const device = this.ctx.requireReady(deviceId, 'photosRead');
    const none = (state: PhotoDeleteReach['state'], done = 0, total = 0): PhotoDeleteReach => ({ deviceId, state, done, total, notListed: [], checkedAt: null });
    if (!device.capabilities.includes('photosDelete')) return none('unsupported');
    if (device.platform !== 'ios') return none('ready');
    const state = this.ahead.get(deviceId);
    if (state?.read === ROLL_OBJECTS) {
      const library = this.ctx.library(deviceId);
      return { deviceId, state: 'ready', done: ROLL_OBJECTS, total: ROLL_OBJECTS, notListed: [...this.notListed(deviceId)].filter((id) => library.byId.has(id)), checkedAt: new Date().toISOString() };
    }
    if (!state?.running) this.prefetch(deviceId);
    return none('checking', this.ahead.get(deviceId)?.read ?? 0, ROLL_OBJECTS);
  }

  /** The reason the iPhone can't delete in this scenario, or null when it can. */
  get blocked(): PhotoDeleteBlock | null {
    return this.scenario === 'ok' || this.scenario === 'recentlyDeleted' || this.scenario === 'fails' ? null : this.scenario;
  }

  /** Applies the scenario to a phone as it is added: an iPhone that can't delete has no photosDelete. */
  adjust(device: DeviceSummary): DeviceSummary {
    if (device.platform !== 'ios' || this.blocked === null) return device;
    return { ...device, capabilities: device.capabilities.filter((c) => c !== 'photosDelete') };
  }

  support(device: DeviceSummary): PhotoDeleteSupport {
    const now = new Date().toISOString();
    const can = device.capabilities.includes('photosDelete');
    if (device.platform !== 'ios') {
      return can
        ? { canDelete: true, reason: 'none', detail: null, goesTo: 'permanent', observed: false, checkedAt: now, iCloudPhotos: null }
        : { canDelete: false, reason: 'unsupported', detail: `${device.name} can’t delete photos from the PC.`, goesTo: 'unknown', observed: false, checkedAt: now, iCloudPhotos: null };
    }
    const seen = this.observed.get(device.id);
    const goesTo = seen ?? 'permanent';
    if (can) {
      return { canDelete: true, reason: 'none', detail: '24 of 24 sampled files say they can be deleted (0 don’t say); 41 camera-roll folders (202510_a…), named differently from the phone’s, straight under the storage.', goesTo, observed: !!seen, checkedAt: now, iCloudPhotos: false };
    }
    const reason = this.blocked ?? (device.mode === 'limited' ? 'unsupported' : 'notCamera');
    return { canDelete: false, reason, detail: DETAIL[reason] ?? null, goesTo, observed: !!seen, checkedAt: now, iCloudPhotos: reason === 'iCloudPhotos' ? true : null };
  }

  /**
   * What a delete of these items will end with, before anything goes: a mismatch anywhere and nothing is deleted;
   * else batches of five, stopping after the batch where the phone refused one (?photodelete=fails, the third).
   */
  plan(deviceId: string, items: readonly { id: string; size: number }[]): { results: PhotoDeleteResult[]; goesTo: PhotoDeleteDestination; matched: boolean } {
    const device = this.ctx.device(deviceId)!;
    const library = this.ctx.library(deviceId);
    const ios = device.platform === 'ios';
    const goesTo: PhotoDeleteDestination = ios && this.scenario === 'recentlyDeleted' ? 'recentlyDeleted' : 'permanent';
    const results: (PhotoDeleteResult | null)[] = items.map((item) => {
      const asset = library.byId.get(item.id);
      if (!asset) return result(item, item.id.split('/').pop() ?? item.id, 'mismatch', 'path_not_found', 'It’s no longer on the phone.');
      if (!this.listed(deviceId, item.id)) {
        return result(item, asset.fileName, 'mismatch', 'path_not_found', `Windows doesn’t list ${asset.fileName} anywhere in the iPhone’s camera roll, so this PC can’t delete it. Delete it in the Photos app on the iPhone.`);
      }
      if (asset.size !== item.size) return result(item, asset.fileName, 'mismatch', 'path_not_found', 'It changed since it was selected.');
      if (asset.inCloudOnly) return result(item, asset.fileName, 'mismatch', 'path_not_found', 'Its original is in iCloud only, not on the iPhone.');
      return null;
    });
    if (results.some((r) => r !== null)) {
      return {
        results: results.map((r, i) => r ?? result(items[i]!, library.byId.get(items[i]!.id)?.fileName ?? '', 'notTried', null, 'Nothing was deleted, because another selected item didn’t match.')),
        goesTo,
        matched: false,
      };
    }
    let stopped = false;
    for (let start = 0; start < items.length && !stopped; start += BATCH) {
      for (let i = start; i < Math.min(start + BATCH, items.length); i++) {
        const item = items[i]!;
        const asset = library.byId.get(item.id)!;
        if (this.scenario === 'fails' && ios && i === 2) {
          results[i] = result(item, asset.fileName, 'failed', 'permission_denied', 'The iPhone refused (access denied).');
          stopped = true;
          continue;
        }
        // A Live Photo's video goes with it, named like the still (IMG_3213.HEIC → IMG_3213.MOV).
        const video = ios && asset.kind === 'livePhoto' ? asset.fileName.replace(/\.[^.]+$/, '.MOV') : null;
        results[i] = {
          ...result(item, asset.fileName, 'deleted', null, null),
          size: item.size + (video ? Math.round(item.size * 0.6) : 0),
          also: video ? [video] : [],
          wentTo: goesTo,
        };
      }
    }
    return {
      results: results.map((r, i) => r ?? result(items[i]!, library.byId.get(items[i]!.id)?.fileName ?? '', 'notTried', null, 'Not tried: the delete stopped at an earlier item.')),
      goesTo,
      matched: true,
    };
  }

  /** Takes these assets off the phone (one batch of a delete). */
  remove(deviceId: string, ids: readonly string[], goesTo: PhotoDeleteDestination): void {
    const library = this.ctx.library(deviceId);
    const gone = new Set(ids);
    library.assets = library.assets.filter((a) => !gone.has(a.id));
    library.hidden = library.hidden.filter((a) => !gone.has(a.id));
    for (const id of gone) library.byId.delete(id);
    library.albums = library.albums.map((album) => ({ ...album, count: assetsInAlbum(library, album.id).length }));
    if (this.ctx.device(deviceId)?.platform === 'ios' && gone.size > 0) this.observed.set(deviceId, goesTo);
  }

  /** Checks a confirmed delete and queues it as a photosDelete job (Photos and Clean-up). */
  start(deviceId: string, items: readonly { id: string; size: number }[], options: StartOptions): string {
    const device = this.ctx.requireReady(deviceId, 'photosRead');
    if (!device.capabilities.includes('photosDelete')) {
      throw this.ctx.fault('not_supported', `${device.name} doesn’t let the PC delete its photos right now (${this.support(device).reason}).`);
    }
    const library = this.ctx.library(deviceId);
    const names = items.map((i) => library.byId.get(i.id)?.fileName ?? i.id.split('/').pop() ?? i.id);
    let copyJobIds: string[] = [];
    if (options.copyFirstTo) {
      const copy = this.ctx.addJob({
        deviceId,
        kind: 'photosExport',
        title: `Copying ${items.length} photos to ${options.copyFirstTo} before deleting them`,
        items: items.map((i, n): JobItem => ({ name: names[n]!, size: i.size })),
      });
      copyJobIds = [copy.id];
    }
    const run = new DeleteRun(this, this.ctx, deviceId, device.platform === 'ios', items, names, copyJobIds, options, SPEED[this.speed]);
    const job = this.ctx.addJob({
      deviceId,
      kind: 'photosDelete',
      title: items.length === 1 ? `Deleting 1 item from ${device.name}` : `Deleting ${items.length} photos and videos from ${device.name}`,
      items: items.map((i, n) => ({ name: names[n]!, size: i.size })),
      staged: run,
    });
    run.jobId = job.id;
    this.runs.set(job.id, run);
    return job.id;
  }

  status(jobId: string): PhotoDeleteJobStatus {
    const run = this.runs.get(jobId);
    const job = this.ctx.job(jobId);
    if (!run || !job) throw this.ctx.badParams('That transfer isn’t a photo delete.');
    return run.status(job);
  }

  readonly handlers: {
    'photos.deleteSupport': Handler<'photos.deleteSupport'>;
    'photos.deleteItems': Handler<'photos.deleteItems'>;
    'photos.deleteJob': Handler<'photos.deleteJob'>;
    'photos.deletePrepare': Handler<'photos.deletePrepare'>;
    'photos.deleteReach': Handler<'photos.deleteReach'>;
  } = {
    'photos.deleteSupport': ({ deviceId, recheck }) => {
      const device = this.ctx.requireReady(deviceId, 'photosRead');
      // "Check again" after unlocking the phone: the mock's locked iPhone is unlocked by then.
      if (recheck && device.platform === 'ios' && this.scenario === 'locked') {
        this.scenario = 'ok';
        const unlocked = { ...device, capabilities: [...device.capabilities.filter((c) => c !== 'photosDelete'), 'photosDelete' as const] };
        this.ctx.setDevice(unlocked);
        return this.support(unlocked);
      }
      return this.support(device);
    },

    'photos.deleteItems': ({ deviceId, items, expectedBytes, copyFirstTo }) => {
      if (!Array.isArray(items) || items.length === 0) throw this.ctx.badParams('items is empty.');
      if (new Set(items.map((i) => i.id)).size !== items.length) throw this.ctx.badParams('An item is listed twice.');
      const total = items.reduce((n, i) => n + i.size, 0);
      if (total !== expectedBytes) throw this.ctx.badParams(`expectedBytes (${expectedBytes}) isn't the total of the items (${total}).`);
      if (copyFirstTo && !/^[a-zA-Z]:\\/.test(copyFirstTo)) throw this.ctx.badParams('copyFirstTo must be a full folder path on this PC.');
      return { jobId: this.start(deviceId, items, { copyFirstTo, source: 'photos' }), skipped: 0 };
    },

    'photos.deleteJob': ({ jobId }) => this.status(jobId),

    'photos.deletePrepare': ({ deviceId }) => {
      this.prefetch(deviceId);
      return {};
    },

    'photos.deleteReach': ({ deviceId }) => this.reachOf(deviceId),
  };
}

type Phase = 'copying' | 'reading' | 'deleting' | 'checking' | 'finished';

/** One delete job on the mock's clock (a StagedRun for the transfer engine). */
class DeleteRun implements StagedRun {
  jobId = '';
  private phase: Phase;
  private clock = 0;
  /** Objects a read ahead had read when this delete started reading (v0.4.2). */
  private readFrom = 0;
  private batch = 0;
  private readonly results: (PhotoDeleteResult | null)[];
  private planned: { results: PhotoDeleteResult[]; goesTo: PhotoDeleteDestination; matched: boolean } | null = null;
  private copyIncomplete = false;
  private goesTo: PhotoDeleteDestination = 'unknown';
  private error: { code: string; message: string } | null = null;

  constructor(
    private readonly owner: MockPhotoDelete,
    private readonly ctx: PhotoDeleteContext,
    private readonly deviceId: string,
    private readonly ios: boolean,
    private readonly items: readonly { id: string; size: number }[],
    private readonly names: string[],
    private readonly copyJobIds: string[],
    private readonly options: StartOptions,
    private readonly speed: (typeof SPEED)[MockDeleteSpeed],
  ) {
    this.phase = copyJobIds.length > 0 ? 'copying' : 'reading';
    this.results = items.map(() => null);
  }

  private get readTotal(): number {
    return this.ios ? ROLL_OBJECTS : this.items.length;
  }

  started(): Partial<TransferJob> {
    if (this.phase === 'copying') return { stage: { name: 'copying', done: 0, total: this.items.length } };
    this.readFrom = this.ios ? this.owner.takeReadAhead(this.deviceId) : 0;
    return { stage: { name: 'reading', done: this.readFrom, total: this.readTotal } };
  }

  advance(seconds: number): Partial<TransferJob> {
    this.clock += seconds;
    switch (this.phase) {
      case 'copying': {
        const copies = this.copyJobIds.map((id) => this.ctx.job(id));
        if (copies.some((j) => !j || j.state === 'failed' || j.state === 'cancelled' || j.state === 'waitingForDevice')) {
          this.copyIncomplete = true;
          this.error = { code: 'copy_incomplete', message: COPY_INCOMPLETE };
          return this.finish(COPY_INCOMPLETE);
        }
        const done = copies.reduce((n, j) => n + (j?.doneItems ?? 0), 0);
        const total = Math.max(this.items.length, copies.reduce((n, j) => n + (j?.totalItems ?? 0), 0));
        if (copies.every((j) => j!.state === 'done')) {
          this.phase = 'reading';
          this.clock = 0;
          this.readFrom = this.ios ? this.owner.takeReadAhead(this.deviceId) : 0;
          return { stage: { name: 'reading', done: this.readFrom, total: this.readTotal } };
        }
        return { stage: { name: 'copying', done, total } };
      }
      case 'reading': {
        // It carries on from what a read ahead read (v0.4.2).
        const read = Math.min(this.readTotal, this.readFrom + Math.round(this.clock * this.speed.objectsPerSecond));
        if (read < this.readTotal) return { stage: { name: 'reading', done: read, total: this.readTotal } };
        if (this.ios) this.owner.readAll(this.deviceId);
        this.planned = this.owner.plan(this.deviceId, this.items);
        this.goesTo = this.planned.goesTo;
        if (!this.planned.matched) {
          this.planned.results.forEach((r, i) => (this.results[i] = r));
          const first = this.planned.results.find((r) => r.outcome === 'mismatch')!;
          this.error = { code: first.code ?? 'path_not_found', message: `Nothing was deleted. ${first.fileName}: ${first.message}` };
          return this.finish(null);
        }
        this.phase = 'deleting';
        this.clock = 0;
        return { stage: { name: 'deleting', done: 0, total: this.items.length } };
      }
      case 'deleting': {
        while (this.clock >= this.speed.batchSeconds && this.phase === 'deleting') {
          this.clock -= this.speed.batchSeconds;
          this.deleteBatch();
        }
        return this.phase === 'deleting'
          ? { stage: { name: 'deleting', done: Math.min(this.items.length, this.batch * BATCH), total: this.items.length }, ...this.counts() }
          : this.afterDeleting();
      }
      case 'checking': {
        const deleted = this.results.filter((r) => r?.outcome === 'deleted').length;
        if (this.clock < this.speed.checkSeconds) {
          return { stage: { name: 'checking', done: Math.floor((deleted * this.clock) / this.speed.checkSeconds), total: deleted } };
        }
        return this.finish(null);
      }
      case 'finished':
        return {};
    }
  }

  /** Deletes the next batch; after the last one, or one the phone refused, the delete moves on to checking. */
  private deleteBatch(): void {
    const start = this.batch * BATCH;
    if (start >= this.items.length) return;
    const indexes = Array.from({ length: Math.min(BATCH, this.items.length - start) }, (_, n) => start + n);
    const planned = this.planned!.results;
    const gone = indexes.filter((i) => planned[i]!.outcome === 'deleted').map((i) => this.items[i]!.id);
    this.owner.remove(this.deviceId, gone, this.goesTo);
    for (const i of indexes) this.results[i] = planned[i]!;
    this.batch++;
    const refused = indexes.some((i) => planned[i]!.outcome === 'failed');
    if (refused) this.stopAt(this.batch * BATCH, 'Not tried: the delete stopped at an earlier item.');
    if (refused || this.batch * BATCH >= this.items.length) this.phase = 'checking';
  }

  private afterDeleting(): Partial<TransferJob> {
    this.clock = 0;
    if (!this.ios) return this.finish(null);
    const deleted = this.results.filter((r) => r?.outcome === 'deleted').length;
    return { stage: { name: 'checking', done: 0, total: deleted }, ...this.counts() };
  }

  cancel(): Partial<TransferJob> {
    if (this.phase === 'finished') return {};
    if (this.phase === 'checking') {
      // Only reading what went: it finishes (the drawer offers no Cancel here).
      return {};
    }
    if (this.phase === 'deleting') {
      // Stops after the batch on its way: that one still goes.
      this.deleteBatch();
    } else {
      for (const id of this.copyJobIds) this.ctx.cancelJob(id);
    }
    return this.finish(STOPPED, 'cancelled');
  }

  fail(error: { code: string; message: string }): Partial<TransferJob> {
    if (this.phase === 'finished') return {};
    this.error = error;
    return this.finish(STOPPED, 'failed');
  }

  private stopAt(from: number, why: string): void {
    for (let i = from; i < this.items.length; i++) {
      this.results[i] ??= result(this.items[i]!, this.names[i]!, 'notTried', null, why);
    }
  }

  private counts(): Partial<TransferJob> {
    let doneItems = 0;
    let failedItems = 0;
    let doneBytes = 0;
    for (const r of this.results) {
      if (r?.outcome === 'deleted') {
        doneItems++;
        doneBytes += r.size; // what it freed (a Live Photo's video included), as the host counts it
      } else if (r?.outcome === 'failed' || r?.outcome === 'mismatch') {
        failedItems++;
      }
    }
    return { doneItems, failedItems, doneBytes };
  }

  private finish(notReached: string | null, state?: 'cancelled' | 'failed'): Partial<TransferJob> {
    this.phase = 'finished';
    this.stopAt(0, notReached ?? STOPPED);
    const deleted = this.results.filter((r) => r?.outcome === 'deleted');
    const freed = deleted.reduce((n, r) => n + r!.size, 0);
    if (deleted.length > 0) this.options.onFinished?.(new Set(deleted.map((r) => r!.id)), freed);
    const nothingWorked = deleted.length === 0 && (this.error !== null || this.results.some((r) => r?.outcome === 'failed' || r?.outcome === 'mismatch'));
    if (nothingWorked && !this.error) {
      const first = this.results.find((r) => r?.outcome === 'failed' || r?.outcome === 'mismatch')!;
      this.error = { code: first.code ?? 'protocol_error', message: `Nothing was deleted. ${first.fileName}: ${first.message}` };
    }
    return {
      state: state ?? (nothingWorked ? 'failed' : 'done'),
      stage: null,
      error: this.error,
      finishedAt: new Date().toISOString(),
      bytesPerSecond: null,
      etaSeconds: null,
      ...this.counts(),
    };
  }

  status(job: TransferJob): PhotoDeleteJobStatus {
    const items = this.items.map((item, i) => this.results[i] ?? result(item, this.names[i]!, 'notTried', null, null));
    const deleted = items.filter((r) => r.outcome === 'deleted');
    return {
      jobId: job.id,
      deviceId: this.deviceId,
      source: this.options.source,
      state: job.state,
      stage: job.stage ?? null,
      items,
      deleted: deleted.length,
      freedBytes: deleted.reduce((n, r) => n + r.size, 0),
      goesTo: this.goesTo,
      copyJobIds: [...this.copyJobIds],
      copyIncomplete: this.copyIncomplete,
      skipped: this.options.skipped ?? 0,
      error: job.error,
    };
  }
}

function result(item: { id: string; size: number }, fileName: string, outcome: PhotoDeleteResult['outcome'], code: string | null, message: string | null): PhotoDeleteResult {
  return { id: item.id, fileName, size: item.size, outcome, code, message, also: [], wentTo: 'unknown' };
}
