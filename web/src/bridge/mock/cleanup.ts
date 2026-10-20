// The mock clean-up: an analysis that runs over about two and a half simulated seconds, section by section,
// and the confirmed deletes, which check items and sizes against the report like the host does.

import type { BridgeEvents, EventName, MethodName, MethodParams, MethodResult } from '../methods';
import type {
  AppFileHit,
  CleanupApps,
  CleanupCaches,
  CleanupDuplicates,
  CleanupMedia,
  CleanupReport,
  CleanupResult,
  CleanupSection,
  CleanupStatus,
  DuplicateGroup,
} from '../cleanupTypes';
import type { AppInfo, DeviceCapability, DeviceSummary, PhotoAsset, StorageInfo, TransferJob, TransferKind } from '../types';
import type { MockFileSystem } from './files';
import { MB } from './mock-data';
import { assetsInAlbum, toContractAsset, type MockAsset, type MockLibrary } from './photos';
import { createRandom, hashString } from './random';
import type { JobItem } from './transfers';

export type CleanupMethodName = Extract<MethodName, `cleanup.${string}`>;

export interface MockCleanupContext {
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  library(deviceId: string): MockLibrary;
  apps(deviceId: string): AppInfo[];
  setApps(deviceId: string, apps: AppInfo[]): void;
  fs(deviceId: string): MockFileSystem;
  storage(deviceId: string): StorageInfo | undefined;
  addJob(spec: { deviceId: string; kind: TransferKind; title: string; items: JobItem[] }): TransferJob;
  emit<E extends EventName>(event: E, params: BridgeEvents[E]): void;
  after(ms: number, run: () => void): void;
  now(): number;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

const TOP_APPS = 30;
const TOP_MEDIA = 40;
const APP_FILE_MIN = 10 * MB;
const ANDROID_APP_FOLDERS = ['/Android/data', '/Android/media', '/Download', '/Documents'];
const IOS_PHOTO_MESSAGE =
  'Photos on an iPhone can’t be deleted safely over USB. Copy them to the PC, then delete them in the Photos app on the iPhone.';

const SECTIONS: { section: CleanupSection; at: number }[] = [
  { section: 'storage', at: 0 },
  { section: 'apps', at: 250 },
  { section: 'media', at: 500 },
  { section: 'caches', at: 1250 },
  { section: 'appFiles', at: 1500 },
  { section: 'duplicates', at: 1750 },
];
const DONE_AT = 2500;

const idle = (deviceId: string): CleanupStatus => ({ deviceId, state: 'idle', progress: null, report: null, error: null, finishedAt: null });

/** The cleanup.* handlers, one state per phone. */
export function cleanupHandlers(ctx: MockCleanupContext): { [M in CleanupMethodName]: (params: MethodParams<M>) => MethodResult<M> } {
  const statuses = new Map<string, CleanupStatus>();
  const runs = new Map<string, number>();
  const trimmed = new Set<string>();

  const status = (deviceId: string) => statuses.get(deviceId) ?? idle(deviceId);
  const publish = (next: CleanupStatus) => {
    statuses.set(next.deviceId, next);
    ctx.emit('cleanup.updated', structuredClone(next));
  };

  const requireReport = (deviceId: string): CleanupReport => {
    const current = status(deviceId);
    if (current.state === 'running') throw ctx.fault('internal', 'Wait for the analysis to finish first.');
    if (!current.report) throw ctx.fault('internal', 'Analyse the phone first: only what the analysis lists can be cleaned up.');
    return current.report;
  };

  const confirmed = (sizes: number[], keys: string[], expectedBytes: number) => {
    if (sizes.length === 0) throw ctx.badParams('items is empty.');
    if (new Set(keys).size !== keys.length) throw ctx.badParams('An item is listed twice.');
    const total = sizes.reduce((a, b) => a + b, 0);
    if (total !== expectedBytes) throw ctx.badParams(`expectedBytes (${expectedBytes}) isn't the total of the items (${total}).`);
  };

  const mutate = (deviceId: string, change: (report: CleanupReport) => CleanupReport) => {
    const current = status(deviceId);
    if (current.report) publish({ ...current, report: change(current.report) });
  };

  const copyJob = (deviceId: string, kind: TransferKind, destination: string | null | undefined, items: JobItem[], what: string): string[] => {
    if (!destination) return [];
    if (!/^[a-zA-Z]:\\/.test(destination)) throw ctx.badParams('copyFirstTo must be a full folder path on this PC.');
    const job = ctx.addJob({ deviceId, kind, title: `Copying ${what} to ${destination} before clean-up`, items });
    return [job.id];
  };

  return {
    'cleanup.status': ({ deviceId }) => structuredClone(status(deviceId)),

    'cleanup.analyze': ({ deviceId }) => {
      const device = ctx.requireReady(deviceId, 'info');
      const current = status(deviceId);
      if (current.state === 'running') return structuredClone(current);
      const run = (runs.get(deviceId) ?? 0) + 1;
      runs.set(deviceId, run);
      const startedAt = new Date(ctx.now()).toISOString();
      const full = buildReport(ctx, device, startedAt, trimmed.has(deviceId));
      const empty: CleanupReport = { ...full, totalBytes: null, freeBytes: null, apps: null, media: null, caches: null, appFiles: null, duplicates: null, notes: [] };
      const running: CleanupStatus = { deviceId, state: 'running', progress: { section: 'storage', done: 0, total: 0 }, report: empty, error: null, finishedAt: null };
      publish(running);

      // Each step reveals one more section of the finished report, as the host's partial reports do.
      const revealed: Partial<CleanupReport> = {};
      const reveal: Record<CleanupSection, () => void> = {
        storage: () => Object.assign(revealed, { totalBytes: full.totalBytes, freeBytes: full.freeBytes }),
        apps: () => Object.assign(revealed, { apps: full.apps }),
        media: () => Object.assign(revealed, { media: full.media }),
        caches: () => Object.assign(revealed, { caches: full.caches }),
        appFiles: () => Object.assign(revealed, { appFiles: full.appFiles }),
        duplicates: () => Object.assign(revealed, { duplicates: full.duplicates }),
      };
      const live = () => runs.get(deviceId) === run && status(deviceId).state === 'running';
      SECTIONS.forEach(({ section, at }, i) => {
        const previous = SECTIONS[i - 1];
        ctx.after(at, () => {
          if (!live()) return;
          if (previous) reveal[previous.section]();
          const total = section === 'duplicates' ? (full.duplicates?.hashedBytes ?? 0) : section === 'media' ? (full.media?.videoCount ?? 0) + (full.media?.photoCount ?? 0) : 1;
          publish({ ...status(deviceId), progress: { section, done: 0, total }, report: { ...empty, ...revealed, notes: [] } });
        });
      });
      for (const at of [1950, 2150, 2350]) {
        ctx.after(at, () => {
          if (!live()) return;
          const total = full.duplicates?.hashedBytes ?? 0;
          publish({ ...status(deviceId), progress: { section: 'duplicates', done: Math.round((total * (at - 1750)) / 750), total } });
        });
      }
      ctx.after(DONE_AT, () => {
        if (!live()) return;
        publish({ deviceId, state: 'done', progress: null, report: full, error: null, finishedAt: new Date(ctx.now()).toISOString() });
      });
      return structuredClone(status(deviceId));
    },

    'cleanup.cancel': ({ deviceId }) => {
      const current = status(deviceId);
      if (current.state === 'running') {
        runs.set(deviceId, (runs.get(deviceId) ?? 0) + 1);
        publish({ ...current, state: 'cancelled', progress: null, finishedAt: new Date(ctx.now()).toISOString() });
      }
      return {};
    },

    'cleanup.deleteMedia': ({ deviceId, items, expectedBytes, copyFirstTo }) => {
      confirmed(items.map((i) => i.size), items.map((i) => i.id), expectedBytes);
      const device = ctx.requireReady(deviceId, 'photosRead');
      const report = requireReport(deviceId);
      if (device.platform === 'ios') throw ctx.fault('not_supported', IOS_PHOTO_MESSAGE);
      if (!device.capabilities.includes('photosDelete')) throw ctx.fault('not_supported');
      const listed = listedMedia(report);
      for (const item of items) {
        if (listed.get(item.id)?.size !== item.size) throw ctx.badParams(`'${item.id}' isn't a photo or video the analysis listed at that size.`);
      }
      const library = ctx.library(deviceId);
      const present = items.filter((i) => library.byId.has(i.id));
      const jobs = copyJob(deviceId, 'photosExport', copyFirstTo, present.map((i) => ({ name: listed.get(i.id)!.fileName, size: i.size })), `${present.length} photos and videos`);
      const gone = new Set(present.map((i) => i.id));
      library.assets = library.assets.filter((a) => !gone.has(a.id));
      for (const id of gone) library.byId.delete(id);
      library.albums = library.albums.map((album) => ({ ...album, count: assetsInAlbum(library, album.id).length }));
      const freed = present.reduce((n, i) => n + i.size, 0);
      mutate(deviceId, (r) => withoutMedia(r, gone, freed));
      return result(present.length, items.length - present.length, freed, jobs);
    },

    'cleanup.deleteFiles': ({ deviceId, items, expectedBytes, copyFirstTo }) => {
      confirmed(items.map((i) => i.size), items.map((i) => `${i.root}\n${i.path}`), expectedBytes);
      ctx.requireReady(deviceId, 'info');
      const report = requireReport(deviceId);
      const listed = new Map((report.appFiles?.files ?? []).map((f) => [`${f.root}\n${f.path}`, f]));
      for (const item of items) {
        if (listed.get(`${item.root}\n${item.path}`)?.size !== item.size) throw ctx.badParams(`'${item.path}' isn't a file the analysis listed at that size.`);
      }
      const fs = ctx.fs(deviceId);
      const present = items.filter((i) => fs.stat(i.root, i.path)?.size === i.size);
      const jobs = copyJob(deviceId, 'copyToPc', copyFirstTo, present.map((i) => ({ name: listed.get(`${i.root}\n${i.path}`)!.name, size: i.size })), `${present.length} files`);
      for (const item of present) fs.delete(item.root, [item.path], false);
      const gone = new Set(present.map((i) => `${i.root}\n${i.path}`));
      const freed = present.reduce((n, i) => n + i.size, 0);
      mutate(deviceId, (r) =>
        r.appFiles
          ? {
              ...r,
              freeBytes: r.freeBytes === null ? null : r.freeBytes + freed,
              appFiles: { ...r.appFiles, files: r.appFiles.files.filter((f) => !gone.has(`${f.root}\n${f.path}`)), totalBytes: r.appFiles.totalBytes - freed },
            }
          : r,
      );
      return result(present.length, items.length - present.length, freed, jobs);
    },

    'cleanup.uninstallApps': ({ deviceId, items, expectedBytes, copyFirstTo }) => {
      confirmed(items.map((i) => i.size), items.map((i) => i.id), expectedBytes);
      const device = ctx.requireReady(deviceId, 'appsUninstall');
      const report = requireReport(deviceId);
      if (copyFirstTo && !device.capabilities.includes('appsExport')) throw ctx.fault('not_supported', 'Apps on this phone can’t be copied to the PC.');
      const listed = new Map((report.apps?.largest ?? []).map((a) => [a.id, a]));
      for (const item of items) {
        const app = listed.get(item.id);
        if (!app || app.appBytes + app.dataBytes !== item.size) throw ctx.badParams(`'${item.id}' isn't an app the analysis listed at that size.`);
      }
      const installed = ctx.apps(deviceId);
      const present = items.filter((i) => installed.some((a) => a.id === i.id && a.source !== 'system'));
      const jobs = copyFirstTo
        ? present.flatMap((i) => copyJob(deviceId, 'appExport', copyFirstTo, [{ name: `${i.id}.apk`, size: listed.get(i.id)!.appBytes }], listed.get(i.id)!.name))
        : [];
      const gone = new Set(present.map((i) => i.id));
      ctx.setApps(deviceId, installed.filter((a) => !gone.has(a.id)));
      const freed = present.reduce((n, i) => n + i.size, 0);
      mutate(deviceId, (r) => ({
        ...r,
        freeBytes: r.freeBytes === null ? null : r.freeBytes + freed,
        apps: r.apps
          ? { ...r.apps, largest: r.apps.largest.filter((a) => !gone.has(a.id)), count: r.apps.count - gone.size, totalBytes: r.apps.totalBytes - freed }
          : null,
        caches: r.caches ? { ...r.caches, apps: r.caches.apps.filter((c) => !gone.has(c.appId)) } : null,
      }));
      return result(present.length, items.length - present.length, freed, jobs);
    },

    'cleanup.trimCaches': ({ deviceId }) => {
      ctx.requireReady(deviceId, 'info');
      const report = requireReport(deviceId);
      if (!report.caches) throw ctx.fault('not_supported', 'This phone doesn’t let the PC clear app caches.');
      const freed = trimmed.has(deviceId) ? 0 : (report.caches.totalBytes ?? 0);
      trimmed.add(deviceId);
      mutate(deviceId, (r) => ({
        ...r,
        freeBytes: r.freeBytes === null ? null : r.freeBytes + freed,
        caches: r.caches ? { ...r.caches, trimmedBytes: freed, trimmedAt: new Date(ctx.now()).toISOString() } : null,
      }));
      return result(report.caches.apps.length, 0, freed, []);
    },
  };
}

function result(deleted: number, skipped: number, freedBytes: number, copyJobIds: string[]): CleanupResult {
  return { deleted, skipped, freedBytes, copyJobIds, copyIncomplete: false };
}

function listedMedia(report: CleanupReport): Map<string, PhotoAsset> {
  const listed = new Map<string, PhotoAsset>();
  for (const asset of [...(report.media?.largestVideos ?? []), ...(report.media?.largestPhotos ?? []), ...(report.duplicates?.groups ?? []).flatMap((g) => g.assets)]) {
    listed.set(asset.id, asset);
  }
  return listed;
}

function withoutMedia(r: CleanupReport, gone: Set<string>, freed: number): CleanupReport {
  const listed = listedMedia(r);
  const removed = [...gone].map((id) => listed.get(id)).filter((a): a is PhotoAsset => !!a);
  const videos = removed.filter((a) => a.kind === 'video');
  const photos = removed.filter((a) => a.kind !== 'video');
  const sum = (list: PhotoAsset[]) => list.reduce((n, a) => n + a.size, 0);
  const groups = (r.duplicates?.groups ?? [])
    .map((g) => {
      const assets = g.assets.filter((a) => !gone.has(a.id));
      return { ...g, assets, reclaimableBytes: g.size * Math.max(0, assets.length - 1) };
    })
    .filter((g) => g.assets.length > 1);
  return {
    ...r,
    freeBytes: r.freeBytes === null ? null : r.freeBytes + freed,
    media: r.media
      ? {
          ...r.media,
          largestVideos: r.media.largestVideos.filter((a) => !gone.has(a.id)),
          largestPhotos: r.media.largestPhotos.filter((a) => !gone.has(a.id)),
          videoCount: r.media.videoCount - videos.length,
          videoBytes: r.media.videoBytes - sum(videos),
          photoCount: r.media.photoCount - photos.length,
          photoBytes: r.media.photoBytes - sum(photos),
        }
      : null,
    duplicates: r.duplicates ? { ...r.duplicates, groups, reclaimableBytes: groups.reduce((n, g) => n + g.reclaimableBytes, 0) } : null,
  };
}

function buildReport(ctx: MockCleanupContext, device: DeviceSummary, startedAt: string, cachesTrimmed: boolean): CleanupReport {
  const caps = new Set(device.capabilities);
  const android = device.platform === 'android';
  const volume = ctx.storage(device.id)?.volumes[0];
  const notes: CleanupReport['notes'] = [];

  const installed = caps.has('appsList') ? ctx.apps(device.id) : [];
  const user = installed.filter((a) => a.source !== 'system');
  const apps: CleanupApps | null = caps.has('appsList')
    ? {
        largest: [...user].sort((a, b) => b.appBytes + b.dataBytes - (a.appBytes + a.dataBytes)).slice(0, TOP_APPS),
        count: user.length,
        totalBytes: user.reduce((n, a) => n + a.appBytes + a.dataBytes, 0),
        canUninstall: caps.has('appsUninstall'),
        canCopyFirst: caps.has('appsExport'),
      }
    : null;
  const names = new Map(installed.map((a) => [a.id, a.name]));

  let media: CleanupMedia | null = null;
  let duplicates: CleanupDuplicates | null = null;
  if (caps.has('photosRead')) {
    const library = ctx.library(device.id);
    const local = library.assets.filter((a) => !a.inCloudOnly);
    const videos = local.filter((a) => a.kind === 'video');
    const photos = local.filter((a) => a.kind !== 'video');
    const largest = (list: MockAsset[]) => [...list].sort((a, b) => b.size - a.size).slice(0, TOP_MEDIA).map(toContractAsset);
    const sum = (list: MockAsset[]) => list.reduce((n, a) => n + a.size, 0);
    const canDelete = android && caps.has('photosDelete');
    media = {
      largestVideos: largest(videos),
      largestPhotos: largest(photos),
      videoCount: videos.length,
      videoBytes: sum(videos),
      photoCount: photos.length,
      photoBytes: sum(photos),
      cloudOnlyCount: library.assets.length - local.length,
      canDelete,
    };
    duplicates = mockDuplicates(device.id, local, canDelete);
  }

  let caches: CleanupCaches | null = null;
  if (android) {
    const list = installed
      .map((a) => ({ appId: a.id, name: a.name, bytes: cachesTrimmed ? 0 : Math.round(a.dataBytes * 0.12) }))
      .filter((c) => c.bytes > 0)
      .sort((a, b) => b.bytes - a.bytes);
    caches = { totalBytes: list.reduce((n, c) => n + c.bytes, 0), apps: list, trimmedBytes: null, trimmedAt: null };
  }

  const files: AppFileHit[] = [];
  const fs = ctx.fs(device.id);
  const walk = (root: string, path: string, appId: string | null, folder: string | null, depth = 0) => {
    let entries;
    try {
      entries = fs.list(root, path);
    } catch {
      if (android) notes.push({ section: 'appFiles', code: 'permission_denied', message: `The phone wouldn't let these folders be read: ${path.slice(1)}.` });
      return;
    }
    for (const entry of entries) {
      const parts = entry.path.split('/').filter(Boolean);
      const owner = android && parts[0] === 'Android' && parts.length > 2 ? parts[2]! : appId;
      if (entry.isDirectory) {
        if (depth < 8) walk(root, entry.path, owner, folder, depth + 1);
      } else if (entry.size >= APP_FILE_MIN) {
        files.push({ root, path: entry.path, name: entry.name, size: entry.size, modified: entry.modified, appId: owner, folder: owner ? null : folder });
      }
    }
  };
  if (android) {
    for (const folder of ANDROID_APP_FOLDERS) {
      if (fs.stat('internal', folder)) walk('internal', folder, null, folder.split('/')[1] ?? null);
    }
  } else if (caps.has('appDocuments')) {
    for (const app of installed.filter((a) => a.fileSharing)) {
      const root = `app:${app.id}`;
      if (fs.hasRoot(root)) walk(root, '/', app.id, null);
    }
  }
  files.sort((a, b) => b.size - a.size || a.path.localeCompare(b.path));
  const appNames: Record<string, string> = {};
  for (const f of files) if (f.appId) appNames[f.appId] = names.get(f.appId) ?? f.appId;

  return {
    deviceId: device.id,
    platform: device.platform,
    startedAt,
    totalBytes: volume?.totalBytes ?? null,
    freeBytes: volume?.freeBytes ?? null,
    apps,
    media,
    caches,
    appFiles: {
      files,
      totalBytes: files.reduce((n, f) => n + f.size, 0),
      minBytes: APP_FILE_MIN,
      appNames,
      canDelete: android ? caps.has('files') : caps.has('appDocuments'),
    },
    duplicates,
    notes,
  };
}

/**
 * The mock library has no real copies, so a seeded handful of photos and videos stand in for them:
 * each group is one asset plus one or two others reported at its size (the screen shows the group's first thumbnail).
 */
function mockDuplicates(deviceId: string, assets: MockAsset[], canDelete: boolean): CleanupDuplicates {
  const rnd = createRandom(hashString(deviceId) % 1000);
  const used = new Set<string>();
  const groups: DuplicateGroup[] = [];
  const pool = assets.slice(0, 6000);
  const count = deviceId.startsWith('R') ? 14 : 9;
  for (let g = 0; g < count && pool.length > 3; g++) {
    const base = pool[Math.floor(rnd.next() * pool.length)]!;
    if (used.has(base.id)) continue;
    const copies = rnd.chance(0.3) ? 2 : 1;
    const members = [base];
    for (let c = 0; c < copies; c++) {
      const other = pool.find((a, i) => i > (g * 397 + c * 131) % pool.length && !used.has(a.id) && a.id !== base.id && a.kind === base.kind);
      if (other) members.push(other);
    }
    if (members.length < 2) continue;
    members.forEach((m) => used.add(m.id));
    const ordered = members
      .map((m) => ({ ...toContractAsset(m), size: base.size, fileName: m === base ? base.fileName : m.fileName }))
      .sort((a, b) => (a.takenAt ?? '').localeCompare(b.takenAt ?? ''));
    groups.push({
      hash: hashString(base.id).toString(16).padStart(8, '0').repeat(8),
      size: base.size,
      assets: ordered,
      reclaimableBytes: base.size * (ordered.length - 1),
    });
  }
  groups.sort((a, b) => b.reclaimableBytes - a.reclaimableBytes);
  const hashedCount = groups.reduce((n, g) => n + g.assets.length, 0) + 23;
  return {
    groups,
    reclaimableBytes: groups.reduce((n, g) => n + g.reclaimableBytes, 0),
    hashedCount,
    hashedBytes: groups.reduce((n, g) => n + g.size * g.assets.length, 0) + 23 * 2 * MB,
    canDelete,
  };
}
