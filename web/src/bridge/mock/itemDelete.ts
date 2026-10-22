// Deleting files, folders and apps in the mock (v0.6), with the host's rules (Core/Deletes/ItemDeleteHandler.cs): the
// call answers with a job at once; the job leaves alone what changed since the analysis (Clean-up), copies to the PC
// first when asked, then deletes one item at a time on the mock's clock, looking at each again just before (another
// size: "changed", gone: "missing"), goes on past an item the phone refuses, and stops between items. One difference:
// only Clean-up's own deletes update its mock report (the host does it for every screen). ?deletespeed=slow makes each
// item take 2 s, for watching the drawer.

import type { AppUninstallItem, DeleteStarted, FileDeleteItem, TransferItemState, TransferItemView } from '../deleteTypes';
import type { MethodName, MethodParams, MethodResult } from '../methods';
import type { AppInfo, DeviceCapability, DeviceSummary, TransferJob, TransferKind } from '../types';
import type { MockFileSystem } from './files';
import type { MockDeleteSpeed } from './photoDelete';
import type { JobItem, NewJob, StagedRun } from './transfers';

type Handler<M extends MethodName> = (params: MethodParams<M>) => MethodResult<M>;

export interface ItemDeleteContext {
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  fs(deviceId: string): MockFileSystem;
  apps(deviceId: string): AppInfo[];
  setApps(deviceId: string, apps: AppInfo[]): void;
  addJob(spec: NewJob): TransferJob;
  job(jobId: string): TransferJob | undefined;
  cancelJob(jobId: string): void;
  speed(): MockDeleteSpeed;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

interface Options {
  copyFirstTo?: string | null;
  /** Clean-up: what changed since the analysis is left alone. */
  skipChanged?: boolean;
  /** Called once when the job ends, with the items that went and those found gone (Clean-up takes both out of its report). */
  onFinished?: (gone: number[], missing: number[]) => void;
}

type Check = 'same' | 'changed' | 'missing';

const STOPPED = 'Not tried: the delete was stopped.';
const COPY_INCOMPLETE = 'The copy to the PC didn’t finish, so nothing was deleted.';
const SECONDS: Record<MockDeleteSpeed, { reading: number; item: number }> = { normal: { reading: 0.3, item: 0.25 }, slow: { reading: 2, item: 2 } };

/** One item of a delete: how to tell whether it changed, and how to delete it. */
interface Target {
  name: string;
  source: string;
  size: number;
  /** Still as the user confirmed it? */
  check(): Check;
  /** Throws { code, message } when the phone refuses. */
  remove(): void;
}

const LEFT_ALONE: Record<Exclude<Check, 'same'>, string> = {
  changed: 'It changed after it was chosen, so it was left alone.',
  missing: 'It isn’t on the phone any more.',
};

export class MockItemDeletes {
  private readonly runs = new Map<string, ItemDeleteRun>();

  constructor(private readonly ctx: ItemDeleteContext) {}

  /** Queues a delete of files and folders (Files, App files, Clean-up). */
  startFiles(deviceId: string, items: readonly FileDeleteItem[], expectedBytes: number, options: Options = {}): DeleteStarted {
    const device = this.ctx.requireReady(deviceId, 'info');
    confirm(this.ctx, items.map((i) => i.size), items.map((i) => `${i.root}\n${i.path}`), expectedBytes);
    const fs = this.ctx.fs(deviceId);
    for (const item of items) {
      if (!item.root || !item.path) throw this.ctx.badParams('Every item needs a root and a path.');
      if (item.path.replace(/\//g, '') === '') throw this.ctx.badParams('A whole shared folder can’t be deleted; choose what is inside it.');
      const needs: DeviceCapability = item.root.startsWith('app:') ? 'appDocuments' : 'files';
      if (!device.capabilities.includes(needs)) throw this.ctx.fault('not_supported', `${device.name} doesn’t let the PC delete files there.`);
    }
    const targets = items.map<Target>((item) => ({
      name: basename(item.path),
      source: `${item.root}\n${item.path}`,
      size: item.size,
      check: () => {
        const entry = fs.stat(item.root, item.path);
        if (!entry) return 'missing';
        return entry.isDirectory === !!item.isDirectory && (entry.isDirectory || entry.size === item.size) ? 'same' : 'changed';
      },
      remove: () => void fs.delete(item.root, [item.path], !!item.isDirectory),
    }));
    const copies = (folder: string) => ({ kind: 'copyToPc' as TransferKind, title: `Copying ${items.length} items to ${folder} before deleting them` });
    const title = items.length === 1 ? `Deleting ${targets[0]!.name} from ${device.name}` : `Deleting ${items.length} items from ${device.name}`;
    return this.start(deviceId, 'filesDelete', 'deleting', title, targets, copies, options);
  }

  /** Queues the removal of apps (Apps, Clean-up). */
  startApps(deviceId: string, items: readonly AppUninstallItem[], expectedBytes: number, options: Options = {}): DeleteStarted {
    const device = this.ctx.requireReady(deviceId, 'appsUninstall');
    confirm(this.ctx, items.map((i) => i.size), items.map((i) => i.id), expectedBytes);
    if (options.copyFirstTo && !device.capabilities.includes('appsExport')) throw this.ctx.fault('not_supported', 'Apps on this phone can’t be copied to the PC.');
    const targets = items.map<Target>((item) => ({
      name: item.name || item.id,
      source: item.id,
      size: item.size,
      check: () => {
        const app = this.ctx.apps(deviceId).find((a) => a.id === item.id);
        return !app ? 'missing' : app.source === 'system' ? 'changed' : 'same';
      },
      remove: () => {
        const list = this.ctx.apps(deviceId);
        const app = list.find((a) => a.id === item.id);
        if (!app) throw { code: 'path_not_found', message: 'That app isn’t on the phone.' };
        if (app.source === 'system') throw { code: 'permission_denied', message: 'System apps can’t be removed.' };
        this.ctx.setApps(deviceId, list.filter((a) => a.id !== item.id));
      },
    }));
    const copies = (folder: string) => ({ kind: 'appExport' as TransferKind, title: `Exporting ${items.length} apps to ${folder} before removing them` });
    const title = items.length === 1 ? `Removing ${targets[0]!.name} from ${device.name}` : `Removing ${items.length} apps from ${device.name}`;
    return this.start(deviceId, 'appsUninstall', 'removing', title, targets, copies, options);
  }

  private start(
    deviceId: string,
    kind: 'filesDelete' | 'appsUninstall',
    deleting: string,
    title: string,
    targets: Target[],
    copies: (folder: string) => { kind: TransferKind; title: string },
    options: Options,
  ): DeleteStarted {
    if (options.copyFirstTo && !/^[a-zA-Z]:\\/.test(options.copyFirstTo)) throw this.ctx.badParams('copyFirstTo must be a full folder path on this PC.');
    const run = new ItemDeleteRun(this.ctx, targets, deleting, options, SECONDS[this.ctx.speed()]);
    const job = this.ctx.addJob({ deviceId, kind, title, items: targets.map((t): JobItem => ({ name: t.name, size: t.size })), staged: run });
    run.deviceId = deviceId;
    run.copies = copies;
    this.runs.set(job.id, run);
    return { jobId: job.id };
  }

  /** transfers.items for a delete of files or apps; null for any other job. */
  items(jobId: string): TransferItemView[] | null {
    return this.runs.get(jobId)?.view() ?? null;
  }

  readonly handlers: {
    'files.deleteItems': Handler<'files.deleteItems'>;
    'apps.uninstallItems': Handler<'apps.uninstallItems'>;
  } = {
    'files.deleteItems': ({ deviceId, items, expectedBytes }) => this.startFiles(deviceId, items, expectedBytes),
    'apps.uninstallItems': ({ deviceId, items, expectedBytes, copyFirstTo }) => this.startApps(deviceId, items, expectedBytes, { copyFirstTo }),
  };
}

type Phase = 'reading' | 'copying' | 'deleting' | 'finished';

class ItemDeleteRun implements StagedRun {
  deviceId = '';
  copies: (folder: string) => { kind: TransferKind; title: string } = () => ({ kind: 'copyToPc', title: '' });
  private phase: Phase = 'reading';
  private clock = 0;
  private kept: number[] = [];
  private next = 0;
  private copyJobId: string | null = null;
  private error: { code: string; message: string } | null = null;
  private readonly states: { state: TransferItemState; code: string | null; message: string | null }[];

  constructor(
    private readonly ctx: ItemDeleteContext,
    private readonly targets: Target[],
    private readonly deletingStage: string,
    private readonly options: Options,
    private readonly seconds: { reading: number; item: number },
  ) {
    this.states = targets.map(() => ({ state: 'queued', code: null, message: null }));
  }

  started(): Partial<TransferJob> {
    if (this.options.skipChanged) return { stage: { name: 'reading', done: 0, total: this.targets.length } };
    this.kept = this.targets.map((_, i) => i);
    return this.afterReading();
  }

  advance(seconds: number): Partial<TransferJob> {
    this.clock += seconds;
    switch (this.phase) {
      case 'reading': {
        if (this.clock < this.seconds.reading) return {};
        this.kept = [];
        this.targets.forEach((t, i) => {
          const check = t.check();
          if (check === 'same') this.kept.push(i);
          else this.states[i] = { state: 'skipped', code: check, message: LEFT_ALONE[check] };
        });
        return { ...this.afterReading(), ...this.counts() };
      }
      case 'copying': {
        const copy = this.copyJobId ? this.ctx.job(this.copyJobId) : undefined;
        // As the host: a copy that failed, stopped, waits for its phone or a decision, or skipped an item, isn't a copy.
        const incomplete = copy?.state === 'done' && (copy.failedItems > 0 || copy.doneItems < copy.totalItems);
        if (!copy || incomplete || ['failed', 'cancelled', 'waitingForDevice', 'needsDecision'].includes(copy.state)) {
          this.error = { code: 'copy_incomplete', message: COPY_INCOMPLETE };
          return this.finish(COPY_INCOMPLETE);
        }
        if (copy.state !== 'done') return { stage: { name: 'copying', done: copy.doneItems, total: Math.max(copy.totalItems, this.kept.length) } };
        return this.startDeleting();
      }
      case 'deleting': {
        while (this.clock >= this.seconds.item && this.next < this.kept.length) {
          this.clock -= this.seconds.item;
          this.deleteNext();
        }
        if (this.next >= this.kept.length) return this.finish(null);
        return { stage: { name: this.deletingStage, done: this.next, total: this.kept.length }, ...this.counts() };
      }
      case 'finished':
        return {};
    }
  }

  cancel(): Partial<TransferJob> {
    if (this.phase === 'finished') return {};
    if (this.copyJobId) this.ctx.cancelJob(this.copyJobId);
    return this.finish(STOPPED, 'cancelled');
  }

  fail(error: { code: string; message: string }): Partial<TransferJob> {
    if (this.phase === 'finished') return {};
    this.error = error;
    return this.finish(STOPPED, 'failed');
  }

  view(): TransferItemView[] {
    return this.targets.map((t, index) => ({ index, name: t.name, source: t.source, size: t.size, ...this.states[index]! }));
  }

  private afterReading(): Partial<TransferJob> {
    if (this.kept.length > 0 && this.options.copyFirstTo) {
      this.phase = 'copying';
      const what = this.copies(this.options.copyFirstTo);
      const copy = this.ctx.addJob({
        deviceId: this.deviceId,
        kind: what.kind,
        title: what.title,
        items: this.kept.map((i) => ({ name: this.targets[i]!.name, size: Math.max(1, this.targets[i]!.size) })),
      });
      this.copyJobId = copy.id;
      return { stage: { name: 'copying', done: 0, total: this.kept.length } };
    }
    return this.startDeleting();
  }

  private startDeleting(): Partial<TransferJob> {
    this.phase = 'deleting';
    this.clock = 0;
    if (this.kept.length === 0) return this.finish(null);
    return { stage: { name: this.deletingStage, done: 0, total: this.kept.length } };
  }

  private deleteNext(): void {
    const i = this.kept[this.next++]!;
    // Looked at again just before it goes: jobs take turns, so it may have changed since Delete.
    const check = this.targets[i]!.check();
    if (check !== 'same') {
      this.states[i] = { state: 'skipped', code: check, message: LEFT_ALONE[check] };
      return;
    }
    try {
      this.targets[i]!.remove();
      this.states[i] = { state: 'done', code: null, message: null };
    } catch (error) {
      const { code, message } = error as { code?: string; message?: string };
      this.states[i] = { state: 'failed', code: code ?? 'protocol_error', message: message ?? 'The phone couldn’t do it.' };
    }
  }

  private counts(): Partial<TransferJob> {
    let doneItems = 0;
    let failedItems = 0;
    let skippedItems = 0;
    let doneBytes = 0;
    this.states.forEach((s, i) => {
      if (s.state === 'done') {
        doneItems++;
        doneBytes += this.targets[i]!.size;
      } else if (s.state === 'failed') failedItems++;
      else if (s.state === 'skipped') skippedItems++;
    });
    return { doneItems, failedItems, skippedItems, doneBytes };
  }

  private finish(notReached: string | null, state?: 'cancelled' | 'failed'): Partial<TransferJob> {
    this.phase = 'finished';
    for (const s of this.states) if (s.state === 'queued') s.message = notReached ?? STOPPED;
    const gone = this.states.flatMap((s, i) => (s.state === 'done' ? [i] : []));
    const missing = this.states.flatMap((s, i) => (s.state === 'skipped' && s.code === 'missing' ? [i] : []));
    if (gone.length + missing.length > 0) this.options.onFinished?.(gone, missing);
    const failed = this.states.find((s) => s.state === 'failed');
    const nothingWorked = gone.length === 0 && (this.error !== null || !!failed);
    if (nothingWorked && !this.error && failed) {
      const name = this.targets[this.states.indexOf(failed)]!.name;
      this.error = { code: failed.code ?? 'protocol_error', message: `Nothing was deleted. ${name}: ${failed.message}` };
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
}

/** What the page sends must add up to what it showed the user, item by item. */
function confirm(ctx: ItemDeleteContext, sizes: number[], keys: string[], expectedBytes: number): void {
  if (sizes.length === 0) throw ctx.badParams('items is empty.');
  if (sizes.some((s) => s < 0)) throw ctx.badParams('A size is negative.');
  if (new Set(keys).size !== keys.length) throw ctx.badParams('An item is listed twice.');
  const total = sizes.reduce((n, s) => n + s, 0);
  if (total !== expectedBytes) throw ctx.badParams(`expectedBytes (${expectedBytes}) isn't the total of the items (${total}).`);
}

function basename(path: string): string {
  const trimmed = path.replace(/\/+$/, '');
  return trimmed.slice(trimmed.lastIndexOf('/') + 1);
}
