// The mock's photo backup: which photos each phone has already backed up, per-phone options over the
// Settings defaults, backup jobs through the transfer engine, and photos copied into an iPhone app.

import type { RpcFault } from './backend';
import type { MockFileSystem } from './files';
import type { MockAsset, MockLibrary } from './photos';
import type { TransferEngine, JobItem } from './transfers';
import type { MethodName, MethodParams, MethodResult } from '../methods';
import type {
  AppInfo,
  DeviceCapability,
  DeviceSummary,
  PhotoBackupCount,
  PhotoBackupOptions,
  PhotoBackupRun,
  PhotoBackupStatus,
  Settings,
} from '../types';
import { ANDROID_ID, GB, IPHONE_ID } from './mock-data';

export type PhotoBackupMethod =
  | 'photos.newCount'
  | 'photos.backupStatus'
  | 'photos.backupConfigure'
  | 'photos.backup'
  | 'photos.backupForget'
  | 'photos.importToApp';

type Handlers = { [M in PhotoBackupMethod & MethodName]: (params: MethodParams<M>) => MethodResult<M> };

/** What the backup needs from the mock backend. */
export interface PhotoBackupHost {
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  library(deviceId: string): MockLibrary;
  engine(): TransferEngine;
  settings(): Settings;
  apps(deviceId: string): AppInfo[];
  fs(deviceId: string): MockFileSystem;
  sizeOfLocal(path: string): number;
  now(): number;
  fault(code: string, message?: string): RpcFault;
  badParams(message: string): RpcFault;
}

const PATTERN = /^(yyyy|MM|dd)([/\-_. ](yyyy|MM|dd))*$/;

export const isValidPattern = (pattern: string) => pattern === '' || PATTERN.test(pattern);

/** Photos taken since the (pretend) last backup a few days ago. */
const NEW_SINCE_LAST: Record<string, number> = { [IPHONE_ID]: 128, [ANDROID_ID]: 46 };

interface DeviceBackup {
  backedUp: Set<string>;
  own: Partial<PhotoBackupOptions>;
  lastRun: PhotoBackupRun | null;
  activeJobId: string | null;
}

const LIVE_MOTION_BYTES = 2.6 * 1024 * 1024;

function folderFor(pattern: string, takenAt: string | null): string {
  if (pattern === '') return '';
  if (!takenAt) return 'Undated\\';
  const t = new Date(takenAt);
  const pad = (n: number) => String(n).padStart(2, '0');
  return (
    pattern
      .replace('yyyy', String(t.getFullYear()))
      .replace('MM', pad(t.getMonth() + 1))
      .replace('dd', pad(t.getDate()))
      .split('/')
      .join('\\') + '\\'
  );
}

function numbered(name: string, n: number): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? `${name.slice(0, dot)} (${n})${name.slice(dot)}` : `${name} (${n})`;
}

export class MockPhotoBackup {
  private readonly devices = new Map<string, DeviceBackup>();

  constructor(private readonly host: PhotoBackupHost) {}

  private state(deviceId: string): DeviceBackup {
    let state = this.devices.get(deviceId);
    if (!state) {
      const library = this.host.library(deviceId);
      const fresh = NEW_SINCE_LAST[deviceId] ?? 0;
      const backedUp = new Set(library.assets.slice(fresh).map((a) => a.id));
      const copied = backedUp.size;
      const finished = this.host.now() - 3 * 24 * 3600 * 1000;
      state = {
        backedUp,
        own: {},
        lastRun:
          copied > 0
            ? {
                jobId: `backup-earlier-${deviceId}`,
                destination: '',
                state: 'done',
                planned: copied,
                copied,
                duplicates: 0,
                failed: 0,
                bytes: Math.round(copied * 0.004 * GB),
                keptHeic: 0,
                startedAt: new Date(finished - 40 * 60 * 1000).toISOString(),
                finishedAt: new Date(finished).toISOString(),
              }
            : null,
        activeJobId: null,
      };
      this.devices.set(deviceId, state);
    }
    return state;
  }

  private options(device: DeviceSummary): PhotoBackupOptions {
    const s = this.host.settings();
    const own = this.state(device.id).own;
    return {
      destination: own.destination ?? `${s.photoBackupFolder}\\${device.name}`,
      folderPattern: own.folderPattern ?? s.photoBackupPattern,
      convertHeic: own.convertHeic ?? s.photoBackupConvertHeic,
      includeHidden: own.includeHidden ?? false,
    };
  }

  private candidates(deviceId: string, options: PhotoBackupOptions): { copy: MockAsset[]; cloud: number; hidden: number } {
    const library = this.host.library(deviceId);
    const state = this.state(deviceId);
    const fresh = library.assets.filter((a) => !state.backedUp.has(a.id));
    const hiddenFresh = library.hidden.filter((a) => !state.backedUp.has(a.id));
    const copy = fresh.filter((a) => !a.inCloudOnly);
    return {
      copy: options.includeHidden ? [...copy, ...hiddenFresh] : copy,
      cloud: fresh.length - copy.length,
      hidden: options.includeHidden ? 0 : hiddenFresh.length,
    };
  }

  count(device: DeviceSummary): PhotoBackupCount {
    const { copy, cloud, hidden } = this.candidates(device.id, this.options(device));
    const videos = copy.filter((a) => a.kind === 'video').length;
    return {
      deviceId: device.id,
      newCount: copy.length,
      newPhotos: copy.length - videos,
      newVideos: videos,
      newBytes: copy.reduce((n, a) => n + a.size + (a.livePairId ? LIVE_MOTION_BYTES : 0), 0),
      cloudOnly: cloud,
      hiddenLeftOut: hidden,
    };
  }

  status(device: DeviceSummary): PhotoBackupStatus {
    const state = this.state(device.id);
    const options = this.options(device);
    const active = state.activeJobId ? this.host.engine().get(state.activeJobId) : undefined;
    if (active && ['done', 'failed', 'cancelled'].includes(active.state)) state.activeJobId = null;
    return {
      deviceId: device.id,
      count: this.count(device),
      options,
      customized: Object.keys(state.own).length > 0,
      backedUp: state.backedUp.size,
      lastRun: state.lastRun ? { ...state.lastRun, destination: state.lastRun.destination || options.destination } : null,
      activeJobId: state.activeJobId,
      heicConversion: 'available',
    };
  }

  readonly handlers: Handlers = {
    'photos.newCount': ({ deviceId }) => this.count(this.host.requireReady(deviceId, 'photosRead')),
    'photos.backupStatus': ({ deviceId }) => this.status(this.host.requireReady(deviceId, 'photosRead')),
    'photos.backupConfigure': ({ deviceId, destination, folderPattern, convertHeic, includeHidden, useDefaults }) => {
      const device = this.host.requireReady(deviceId, 'photosRead');
      if (destination !== undefined && !/^[a-z]:\\/i.test(destination)) throw this.host.badParams('destination must be a full folder path on this PC.');
      if (folderPattern !== undefined && !isValidPattern(folderPattern)) {
        throw this.host.badParams('folderPattern must be "" or yyyy, MM and dd joined by / - _ . or a space.');
      }
      const state = this.state(deviceId);
      if (useDefaults) state.own = {};
      if (destination !== undefined) state.own.destination = destination;
      if (folderPattern !== undefined) state.own.folderPattern = folderPattern;
      if (convertHeic !== undefined) state.own.convertHeic = convertHeic;
      if (includeHidden !== undefined) state.own.includeHidden = includeHidden;
      return this.status(device);
    },
    'photos.backup': ({ deviceId }) => {
      const device = this.host.requireReady(deviceId, 'photosRead');
      const state = this.state(deviceId);
      const engine = this.host.engine();
      const running = state.activeJobId ? engine.get(state.activeJobId) : undefined;
      if (running && !['done', 'failed', 'cancelled'].includes(running.state)) return { jobId: running.id };

      const options = this.options(device);
      state.own.destination ??= options.destination;
      const { copy } = this.candidates(deviceId, options);
      const assetOf = new Map<JobItem, MockAsset>();
      const items = copy.map<JobItem>((a) => {
        const name = options.convertHeic ? a.fileName.replace(/\.HEIC$/i, '.jpg') : a.fileName;
        const item = { name: folderFor(options.folderPattern, a.takenAt) + name, size: a.size + (a.livePairId ? LIVE_MOTION_BYTES : 0) };
        assetOf.set(item, a);
        return item;
      });
      const startedAt = new Date(this.host.now()).toISOString();
      const run: PhotoBackupRun = {
        jobId: '',
        destination: options.destination,
        state: 'running',
        planned: items.length,
        copied: 0,
        duplicates: 0,
        failed: 0,
        bytes: 0,
        keptHeic: 0,
        startedAt,
        finishedAt: null,
      };
      const job = engine.add({
        deviceId,
        kind: 'photosBackup',
        title: `Backing up new photos from ${device.name} to ${options.destination}`,
        items,
        onItemDone: (item) => {
          const asset = assetOf.get(item);
          if (asset) state.backedUp.add(asset.id);
          run.copied++;
          run.bytes += item.size;
        },
        onDone: () => {
          run.state = 'done';
          run.finishedAt = new Date(this.host.now()).toISOString();
          state.activeJobId = null;
        },
      });
      run.jobId = job.id;
      state.lastRun = run;
      state.activeJobId = job.id;
      return { jobId: job.id };
    },
    'photos.backupForget': ({ deviceId }) => {
      this.host.requireReady(deviceId, 'photosRead');
      const state = this.state(deviceId);
      const forgotten = state.backedUp.size;
      state.backedUp.clear();
      return { forgotten };
    },
    'photos.importToApp': ({ deviceId, appId, files }) => {
      const device = this.host.requireReady(deviceId, 'appDocuments');
      // iPhones only, as in the host: Android adds photos straight to its gallery (photos.import).
      if (device.platform !== 'ios') throw this.host.fault('not_supported', `${device.name} adds photos straight to its gallery; this is for iPhones.`);
      const app = this.host.apps(deviceId).find((a) => a.id === appId && a.fileSharing);
      if (!app) throw this.host.fault('path_not_found', "That app doesn't share files with a PC, or isn't on the phone.");
      if (files.length === 0) throw this.host.badParams('Choose at least one photo.');
      const fs = this.host.fs(deviceId);
      const root = `app:${appId}`;
      const items = files.map<JobItem>((path) => ({ name: path.split(/[\\/]/).pop() ?? path, size: this.host.sizeOfLocal(path) }));
      const what = items.length === 1 ? items[0]!.name : `${items.length} items`;
      const job = this.host.engine().add({
        deviceId,
        kind: 'photosToApp',
        title: `Adding ${what} to ${app.name} on ${device.name}`,
        items,
        onItemDone: (item) => {
          if (!fs.hasRoot(root)) return;
          let name = item.name;
          for (let n = 1; fs.stat(root, `/${name}`); n++) name = numbered(item.name, n);
          fs.writeFile(root, `/${name}`, item.size);
        },
      });
      return { jobId: job.id };
    },
  };
}
