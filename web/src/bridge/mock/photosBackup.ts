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
  PhotoBackupOnConnect,
  PhotoBackupOptions,
  PhotoBackupRange,
  PhotoBackupRun,
  PhotoBackupSpace,
  PhotoBackupStatus,
  PhotoBackupTrigger,
  Settings,
} from '../types';
import { ANDROID_ID, GB, IPHONE_ID } from './mock-data';
import { HIDDEN_LAYOUTS, HIDDEN_MODES, hiddenFolderProblem, hiddenRoot } from '../hiddenBackup';

export type PhotoBackupMethod =
  | 'photos.newCount'
  | 'photos.backupStatus'
  | 'photos.backupConfigure'
  | 'photos.backup'
  | 'photos.backupForget'
  | 'photos.importToApp'
  | 'photos.backupSpace';

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

export const EVERYTHING: PhotoBackupRange = { kind: 'all', days: null, since: null };

/** The PC's drives as the mock sees them: C: as short of space as the owner's, F: a USB stick that is nearly full. */
export const MOCK_DRIVES: Record<string, { free: number; total: number }> = {
  'C:\\': { free: 12.4 * GB, total: 237.9 * GB },
  'D:\\': { free: 412.7 * GB, total: 931.5 * GB },
  'E:\\': { free: 227.1 * GB, total: 439.9 * GB },
  'F:\\': { free: 0.4 * GB, total: 29.8 * GB },
};

/** The host's PhotoBackupSpaces.Check: the photos plus a margin (1 GB or a tenth), nearly full under 20 GB or a tenth. */
export function spaceFor(folder: string, bytes: number): PhotoBackupSpace {
  const root = /^[a-z]:\\/i.test(folder) ? folder.slice(0, 3).toUpperCase() : folder;
  const drive = MOCK_DRIVES[root];
  const needed = bytes <= 0 ? 0 : Math.round(bytes + Math.max(2 ** 30, bytes / 10));
  if (!drive) return { root, freeBytes: null, totalBytes: null, neededBytes: needed, enough: false, nearlyFull: false, driveMissing: true };
  const free = Math.round(drive.free);
  return {
    root,
    freeBytes: free,
    totalBytes: Math.round(drive.total),
    neededBytes: needed,
    enough: needed === 0 || free >= needed,
    nearlyFull: free < 20e9 || free < drive.total / 10,
    driveMissing: false,
  };
}

/** The range with only the field its kind uses, or an error message (the host's PhotoBackupRange.Validated). */
export function validRange(range: PhotoBackupRange): PhotoBackupRange | string {
  switch (range.kind) {
    case 'all':
      return EVERYTHING;
    case 'lastDays':
      return Number.isInteger(range.days) && range.days! >= 1 && range.days! <= 36_500
        ? { kind: 'lastDays', days: range.days, since: null }
        : 'range.days must be between 1 and 36500.';
    case 'since':
      return range.since && /^\d{4}-\d{2}-\d{2}$/.test(range.since) && !Number.isNaN(Date.parse(range.since))
        ? { kind: 'since', days: null, since: range.since }
        : 'range.since must be a date (yyyy-MM-dd).';
    default:
      return 'range.kind must be "all", "lastDays" or "since".';
  }
}

/** The oldest time (ms) a photo may be taken at to be in range; null for everything. "since" is midnight local time. */
export function rangeCutoff(range: PhotoBackupRange, now: number): number | null {
  if (range.kind === 'lastDays') return now - (range.days ?? 30) * 24 * 3600 * 1000;
  if (range.kind === 'since' && range.since) {
    const [y, m, d] = range.since.split('-').map(Number);
    return new Date(y!, m! - 1, d!).getTime();
  }
  return null;
}

/** Photos taken since the (pretend) last backup a few days ago. */
const NEW_SINCE_LAST: Record<string, number> = { [IPHONE_ID]: 128, [ANDROID_ID]: 46 };

interface DeviceBackup {
  backedUp: Set<string>;
  own: Partial<PhotoBackupOptions>;
  lastRun: PhotoBackupRun | null;
  activeJobId: string | null;
  onConnect: PhotoBackupOnConnect;
  editsCopied: Set<string>;
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

  /** Where the last backup job's items go, relative to its destination ("Hidden\2026\10\IMG_H0001.HEIC"), for tests. */
  lastItems: string[] = [];

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
                trigger: 'manual',
                edited: 0,
                editsMissing: 0,
              }
            : null,
        activeJobId: null,
        onConnect: 'notify',
        editsCopied: new Set(),
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
      range: own.range ?? EVERYTHING,
      saveEdited: own.saveEdited ?? false,
      // As the host: only an iPhone has a Hidden album.
      hidden: device.platform === 'ios' ? (own.hidden ?? s.photoBackupHidden) : 'exclude',
      hiddenLayout: own.hiddenLayout ?? s.photoBackupHiddenLayout,
      hiddenFolder: own.hiddenFolder ?? s.photoBackupHiddenFolder,
    };
  }

  /** The mock iPhone's photos edited in Photos: every 97th; their edited version is copied once per edit. */
  private editsWanted(deviceId: string, options: PhotoBackupOptions): MockAsset[] {
    const device = this.host.library(deviceId);
    if (!options.saveEdited || deviceId !== IPHONE_ID || options.hidden === 'only') return [];
    const state = this.state(deviceId);
    const cutoff = rangeCutoff(options.range, this.host.now());
    return device.assets.filter(
      (a, i) => i % 97 === 0 && !a.inCloudOnly && !state.editsCopied.has(a.id) && (cutoff === null || !a.takenAt || Date.parse(a.takenAt) >= cutoff),
    );
  }

  /** As the host: hidden or not is the phone's now; a photo copied before stays copied (backedUp), whichever tree it went to. */
  private candidates(
    deviceId: string,
    options: PhotoBackupOptions,
  ): { copy: MockAsset[]; cloud: number; hidden: number; outOfRange: number; newHidden: number; normalLeftOut: number } {
    const library = this.host.library(deviceId);
    const state = this.state(deviceId);
    const cutoff = rangeCutoff(options.range, this.host.now());
    const inRange = (a: MockAsset) => cutoff === null || !a.takenAt || Date.parse(a.takenAt) >= cutoff;
    const unknown = [...library.assets, ...library.hidden].filter((a) => !state.backedUp.has(a.id));
    const fresh = library.assets.filter((a) => !state.backedUp.has(a.id) && inRange(a));
    const hiddenFresh = library.hidden.filter((a) => !state.backedUp.has(a.id) && inRange(a));
    const wantNormal = options.hidden !== 'only';
    const wantHidden = options.hidden !== 'exclude';
    const copy = wantNormal ? fresh.filter((a) => !a.inCloudOnly) : [];
    return {
      copy: wantHidden ? [...copy, ...hiddenFresh] : copy,
      cloud: wantNormal ? fresh.length - copy.length : 0,
      hidden: wantHidden ? 0 : hiddenFresh.length,
      outOfRange: unknown.filter((a) => !inRange(a)).length,
      newHidden: wantHidden ? hiddenFresh.length : 0,
      normalLeftOut: wantNormal ? 0 : fresh.length,
    };
  }

  count(device: DeviceSummary): PhotoBackupCount {
    const { copy, cloud, hidden, outOfRange, newHidden, normalLeftOut } = this.candidates(device.id, this.options(device));
    const videos = copy.filter((a) => a.kind === 'video').length;
    return {
      deviceId: device.id,
      newCount: copy.length,
      newPhotos: copy.length - videos,
      newVideos: videos,
      newBytes: copy.reduce((n, a) => n + a.size + (a.livePairId ? LIVE_MOTION_BYTES : 0), 0),
      cloudOnly: cloud,
      hiddenLeftOut: hidden,
      outOfRange,
      newEdits: this.editsWanted(device.id, this.options(device)).length,
      newHidden,
      normalLeftOut,
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
      space: spaceFor(options.destination, this.count(device).newBytes),
      onConnect: state.onConnect,
    };
  }

  /** photos.backup, and the start when a phone connects: the job already going, or a new one (refused without room or drive). */
  private start(device: DeviceSummary, trigger: PhotoBackupTrigger): { jobId: string } {
    const deviceId = device.id;
    const state = this.state(deviceId);
    const engine = this.host.engine();
    const running = state.activeJobId ? engine.get(state.activeJobId) : undefined;
    if (running && !['done', 'failed', 'cancelled'].includes(running.state)) return { jobId: running.id };

    const options = this.options(device);
    const space = spaceFor(options.destination, this.count(device).newBytes);
    if (space.driveMissing) throw this.host.fault('path_not_found', `The backup folder's drive ${space.root} isn't there. Connect it, or choose another folder.`);
    if (!space.enough && this.count(device).newCount + this.count(device).newEdits > 0) {
      throw this.host.fault('no_space', `Not enough space on ${space.root} for the new photos. Free some space, or choose another folder.`);
    }
    state.own.destination ??= options.destination;
    const { copy } = this.candidates(deviceId, options);
    const edits = new Set(this.editsWanted(deviceId, options).map((a) => a.id));
    const assetOf = new Map<JobItem, MockAsset>();
    const editOf = new Map<JobItem, MockAsset>();
    // Hidden ones under "<hiddenFolder>\", dated inside the same way, when the options keep them apart.
    const hiddenIds = new Set(this.host.library(deviceId).hidden.map((a) => a.id));
    const apart = hiddenRoot(options.destination, options.hidden, options.hiddenLayout, options.hiddenFolder) ? `${options.hiddenFolder}\\` : '';
    const items = copy.map<JobItem>((a) => {
      const name = options.convertHeic ? a.fileName.replace(/\.HEIC$/i, '.jpg') : a.fileName;
      const tree = hiddenIds.has(a.id) ? apart : '';
      const item = { name: tree + folderFor(options.folderPattern, a.takenAt) + name, size: a.size + (a.livePairId ? LIVE_MOTION_BYTES : 0) };
      assetOf.set(item, a);
      if (edits.delete(a.id)) editOf.set(item, a);
      return item;
    });
    // Edits of photos copied before: the edited version alone.
    for (const id of edits) {
      const a = this.host.library(deviceId).byId.get(id);
      if (!a) continue;
      const item = { name: folderFor(options.folderPattern, a.takenAt) + a.fileName.replace(/(\.[^.]+)$/, ' (edited)$1'), size: Math.round(a.size * 0.8) };
      editOf.set(item, a);
      items.push(item);
    }
    this.lastItems = items.map((i) => i.name);
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
      trigger,
      edited: 0,
      editsMissing: 0,
    };
    const job = engine.add({
      deviceId,
      kind: 'photosBackup',
      title: `Backing up new photos from ${device.name} to ${options.destination}`,
      items,
      onItemDone: (item) => {
        const asset = assetOf.get(item);
        if (asset) {
          state.backedUp.add(asset.id);
          run.copied++;
        }
        const edited = editOf.get(item);
        if (edited) {
          state.editsCopied.add(edited.id);
          run.edited++;
        }
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
  }

  /** The phone became ready (plugged back, trusted): what the host's PhotoBackupWatcher does, minus the Windows notification. */
  connected(deviceId: string): void {
    const state = this.state(deviceId);
    if (state.onConnect !== 'backUp') return;
    try {
      const device = this.host.requireReady(deviceId, 'photosRead');
      if (this.count(device).newCount > 0) this.start(device, 'automatic');
    } catch {
      // No room, or the drive is gone: the host shows a Windows notification instead; the page shows the panel's warning.
    }
  }

  readonly handlers: Handlers = {
    'photos.newCount': ({ deviceId }) => this.count(this.host.requireReady(deviceId, 'photosRead')),
    'photos.backupStatus': ({ deviceId }) => this.status(this.host.requireReady(deviceId, 'photosRead')),
    'photos.backupConfigure': ({
      deviceId,
      destination,
      folderPattern,
      convertHeic,
      useDefaults,
      range,
      onConnect,
      saveEdited,
      hidden,
      hiddenLayout,
      hiddenFolder,
    }) => {
      const device = this.host.requireReady(deviceId, 'photosRead');
      if (destination !== undefined && !/^[a-z]:\\/i.test(destination)) throw this.host.badParams('destination must be a full folder path on this PC.');
      if (folderPattern !== undefined && !isValidPattern(folderPattern)) {
        throw this.host.badParams('folderPattern must be "" or yyyy, MM and dd joined by / - _ . or a space.');
      }
      const checkedRange = range === undefined ? undefined : validRange(range);
      if (typeof checkedRange === 'string') throw this.host.badParams(checkedRange);
      if (onConnect !== undefined && !['nothing', 'notify', 'backUp'].includes(onConnect)) {
        throw this.host.badParams('onConnect must be "nothing", "notify" or "backUp".');
      }
      if (hidden !== undefined && !HIDDEN_MODES.includes(hidden)) throw this.host.badParams('hidden must be "exclude", "include" or "only".');
      if (hiddenLayout !== undefined && !HIDDEN_LAYOUTS.includes(hiddenLayout)) throw this.host.badParams('hiddenLayout must be "separate" or "together".');
      const folderProblem = hiddenFolder === undefined ? null : hiddenFolderProblem(hiddenFolder);
      if (folderProblem) throw this.host.badParams(`hiddenFolder: ${folderProblem}.`);
      const state = this.state(deviceId);
      if (onConnect !== undefined) state.onConnect = onConnect;
      if (useDefaults) state.own = {};
      if (checkedRange !== undefined) state.own.range = checkedRange;
      if (destination !== undefined) state.own.destination = destination;
      if (folderPattern !== undefined) state.own.folderPattern = folderPattern;
      if (convertHeic !== undefined) state.own.convertHeic = convertHeic;
      if (saveEdited !== undefined) state.own.saveEdited = saveEdited;
      if (hidden !== undefined) state.own.hidden = hidden;
      if (hiddenLayout !== undefined) state.own.hiddenLayout = hiddenLayout;
      if (hiddenFolder !== undefined) state.own.hiddenFolder = hiddenFolder.trim();
      return this.status(device);
    },
    'photos.backup': ({ deviceId }) => this.start(this.host.requireReady(deviceId, 'photosRead'), 'manual'),
    'photos.backupForget': ({ deviceId }) => {
      this.host.requireReady(deviceId, 'photosRead');
      const state = this.state(deviceId);
      const forgotten = state.backedUp.size;
      state.backedUp.clear();
      return { forgotten };
    },
    'photos.backupSpace': ({ folder }) => {
      if (!/^[a-z]:\\/i.test(folder)) throw this.host.badParams('folder must be a full folder path on this PC.');
      return spaceFor(folder, 0);
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
      // Round 3, as the host: a new dated folder per send, in parts of 200.
      const pad = (n: number) => String(n).padStart(2, '0');
      const today = new Date(this.host.now());
      const base = `DeviceDeck ${today.getFullYear()}-${pad(today.getMonth() + 1)}-${pad(today.getDate())}`;
      let folder = base;
      for (let n = 2; fs.hasRoot(root) && fs.stat(root, `/${folder}`); n++) folder = `${base} (${n})`;
      const parts = toAppParts(folder, items.length);
      let done = 0;
      const job = this.host.engine().add({
        deviceId,
        kind: 'photosToApp',
        title: `Adding ${what} to ${app.name} › ${folder} on ${device.name}`,
        items,
        onItemDone: (item) => {
          if (!fs.hasRoot(root)) return;
          const dir = `/${parts[Math.floor(done++ / 200)]!.path}`;
          let path = '';
          for (const segment of dir.split('/').filter(Boolean)) {
            path += `/${segment}`;
            if (!fs.stat(root, path)) fs.mkdir(root, path);
          }
          let name = item.name;
          for (let n = 1; fs.stat(root, `${dir}/${name}`); n++) name = numbered(item.name, n);
          fs.writeFile(root, `${dir}/${name}`, item.size);
        },
      });
      return { jobId: job.id, folder, parts };
    },
  };
}

/** Settings' hidden-photo defaults, checked as the host checks them (round 4); a message for -32602, or null. */
export function hiddenSettingsProblem(patch: Partial<Settings>): string | null {
  if (patch.photoBackupHidden !== undefined && !HIDDEN_MODES.includes(patch.photoBackupHidden)) return 'photoBackupHidden is out of range.';
  if (patch.photoBackupHiddenLayout !== undefined && !HIDDEN_LAYOUTS.includes(patch.photoBackupHiddenLayout)) return 'photoBackupHiddenLayout is out of range.';
  const folder = patch.photoBackupHiddenFolder;
  if (folder !== undefined && (hiddenFolderProblem(folder) || folder !== folder.trim())) return 'photoBackupHiddenFolder must be one folder name.';
  return null;
}

/** The folders a send to an app fills: the dated one, or its parts of 200 ("1-200", "201-400", …). */
export function toAppParts(folder: string, count: number): { path: string; count: number }[] {
  if (count <= 200) return [{ path: folder, count }];
  const parts: { path: string; count: number }[] = [];
  for (let start = 0; start < count; start += 200) {
    const size = Math.min(200, count - start);
    parts.push({ path: `${folder}/${start + 1}-${start + size}`, count: size });
  }
  return parts;
}
