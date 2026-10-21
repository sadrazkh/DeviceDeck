// Deleting photos in the mock (round 4, photodelete): photos.deleteSupport and photos.deleteItems, with the same
// rules as the host: every item matched first, nothing deleted unless all match, batches of five, a result per item.
// ?photodelete=iCloudPhotos|readOnly|locked|notCamera|converted (the iPhone can't delete, for that reason),
// recentlyDeleted (deletes go to Recently Deleted), fails (the third photo of a delete is refused).

import type { DeviceCapability, DeviceSummary } from '../types';
import type { PhotoDeleteBlock, PhotoDeleteDestination, PhotoDeleteResult, PhotoDeleteSupport } from '../photoDeleteTypes';
import type { MethodName, MethodParams, MethodResult } from '../methods';
import { assetsInAlbum, type MockLibrary } from './photos';
import type { JobItem } from './transfers';

export const MOCK_PHOTODELETE_SCENARIOS = ['ok', 'iCloudPhotos', 'readOnly', 'locked', 'notCamera', 'converted', 'recentlyDeleted', 'fails'] as const;
export type MockPhotoDeleteScenario = (typeof MOCK_PHOTODELETE_SCENARIOS)[number];

const BATCH = 5;

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
  addJob(spec: { deviceId: string; kind: 'photosExport'; title: string; items: JobItem[] }): { id: string };
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

export class MockPhotoDelete {
  scenario: MockPhotoDeleteScenario = 'ok';
  /** Where deletes went on each phone this session (what a real iPhone shows after its first delete). */
  private readonly observed = new Map<string, PhotoDeleteDestination>();

  constructor(private readonly ctx: PhotoDeleteContext) {}

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

  /** One result per item, in order, after deleting in batches of five (cleanup.deleteMedia uses it too). */
  deleteItems(deviceId: string, items: readonly { id: string; size: number }[]): { results: PhotoDeleteResult[]; goesTo: PhotoDeleteDestination } {
    const device = this.ctx.device(deviceId)!;
    const library = this.ctx.library(deviceId);
    const ios = device.platform === 'ios';
    const wentTo: PhotoDeleteDestination = ios && this.scenario === 'recentlyDeleted' ? 'recentlyDeleted' : 'permanent';
    const results: (PhotoDeleteResult | null)[] = items.map((item) => {
      const asset = library.byId.get(item.id);
      if (!asset) return result(item, item.id.split('/').pop() ?? item.id, 'mismatch', 'path_not_found', 'It’s no longer on the phone.');
      if (asset.size !== item.size) return result(item, asset.fileName, 'mismatch', 'path_not_found', 'It changed since it was selected.');
      if (asset.inCloudOnly) return result(item, asset.fileName, 'mismatch', 'path_not_found', 'Its original is in iCloud only, not on the iPhone.');
      return null;
    });
    if (results.some((r) => r !== null)) {
      return {
        results: results.map((r, i) => r ?? result(items[i]!, library.byId.get(items[i]!.id)?.fileName ?? '', 'notTried', null, 'Nothing was deleted, because another selected item didn’t match.')),
        goesTo: wentTo,
      };
    }

    const gone = new Set<string>();
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
          wentTo,
        };
        gone.add(item.id);
      }
    }

    library.assets = library.assets.filter((a) => !gone.has(a.id));
    library.hidden = library.hidden.filter((a) => !gone.has(a.id));
    for (const id of gone) library.byId.delete(id);
    library.albums = library.albums.map((album) => ({ ...album, count: assetsInAlbum(library, album.id).length }));
    if (ios && gone.size > 0) this.observed.set(deviceId, wentTo);
    return {
      results: results.map((r, i) => r ?? result(items[i]!, library.byId.get(items[i]!.id)?.fileName ?? '', 'notTried', null, 'Not tried: the delete stopped at an earlier item.')),
      goesTo: wentTo,
    };
  }

  readonly handlers: { 'photos.deleteSupport': Handler<'photos.deleteSupport'>; 'photos.deleteItems': Handler<'photos.deleteItems'> } = {
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
      const device = this.ctx.requireReady(deviceId, 'photosRead');
      if (!device.capabilities.includes('photosDelete')) {
        throw this.ctx.fault('not_supported', `${device.name} doesn’t let the PC delete its photos right now (${this.support(device).reason}).`);
      }
      const library = this.ctx.library(deviceId);
      const copyJobIds = copyFirstTo
        ? [
            this.ctx.addJob({
              deviceId,
              kind: 'photosExport',
              title: `Copying ${items.length} photos to ${copyFirstTo} before deleting them`,
              items: items.map((i) => ({ name: library.byId.get(i.id)?.fileName ?? i.id, size: i.size })),
            }).id,
          ]
        : [];
      const { results, goesTo } = this.deleteItems(deviceId, items);
      const deleted = results.filter((r) => r.outcome === 'deleted');
      return {
        items: results,
        deleted: deleted.length,
        freedBytes: deleted.reduce((n, r) => n + r.size, 0),
        copyJobIds,
        copyIncomplete: false,
        goesTo,
      };
    },
  };
}

function result(item: { id: string; size: number }, fileName: string, outcome: PhotoDeleteResult['outcome'], code: string | null, message: string | null): PhotoDeleteResult {
  return { id: item.id, fileName, size: item.size, outcome, code, message, also: [], wentTo: 'unknown' };
}
