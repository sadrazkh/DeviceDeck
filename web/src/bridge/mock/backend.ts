// The mock backend: serves every method in docs/bridge-contract.md from the sample data, so the
// whole UI runs in a plain browser with no phone. It also simulates connecting, the Trust prompt,
// a pulled cable and a reconnect.

import { deviceError } from '../errors';
import {
  RPC_INVALID_PARAMS,
  RPC_METHOD_NOT_FOUND,
  type JsonRpcErrorObject,
} from '../jsonrpc';
import type { BridgeEvents, EventName, MethodName, MethodParams, MethodResult } from '../methods';
import type { AppInfo, DeviceCapability, DeviceSummary, DriverStatus, FolderPattern, NotificationShowParams, Settings } from '../types';
import { mockAppFiles, mockMeasure } from './appFiles';
import { addLongNameApps, addLongNameFiles } from './longNames';
import { batteryHandlers } from './battery';
import { cleanupHandlers } from './cleanup';
import { MockFileSystem, joinPath, normalise } from './files';
import { appIconUrl, photoUrl, phoneScreenshotUrl, thumbnailUrl } from './images';
import {
  ANDROID_ID,
  GB,
  IPHONE_ID,
  IPHONE_LIMITED_CAPABILITIES,
  MB,
  android,
  appIconColours,
  apps as sampleApps,
  battery,
  defaultSettings,
  details,
  drivers,
  driversIosMissing,
  iphone,
  pickableFolders,
  seedTransfers,
  storage,
} from './mock-data';
import { MockBackups } from './backup';
import { MockPc, PcError } from './pc';
import { assetsInAlbum, generateLibrary, toContractAsset, type MockAsset, type MockLibrary } from './photos';
import { MockPhotoBackup, hiddenSettingsProblem } from './photosBackup';
import { hashString } from './random';
import { StagedRefusal, TransferEngine, type JobItem } from './transfers';
import { MockToolbox } from './toolbox';
import { MockLocalSend, type MockLocalSendScenario } from './localSend';
import { MockUpdates, type MockNotesScenario, type MockUpdateScenario } from './updates';
import { MockWhatsApp } from './whatsApp';
import { MockPhotoDelete, type MockDeleteSpeed, type MockPhotoDeleteScenario } from './photoDelete';

export type MockScenario = 'ready' | 'empty' | 'trust' | 'limited' | 'driverMissing' | 'iphoneOnly';

export interface MockOptions {
  scenario?: MockScenario;
  settings?: Partial<Settings>;
  /** Simulate the first photo indexing ("Reading 12,481 photos"). Default true. */
  indexing?: boolean;
  /** Seed the sample transfer queue. Default true. */
  seedQueue?: boolean;
  now?: () => number;
  /** Long, Persian and mixed-direction app and file names for layout checks (round 3, ui). Default false. */
  longNames?: boolean;
  /** LocalSend on the phone (round 3): ?localsend=closed and so on. Default "ready". */
  localSend?: MockLocalSendScenario;
  /** Updates (round 4): ?update=ready and so on (./updates.ts). Default "idle". */
  update?: MockUpdateScenario;
  /** Release notes (round 4): ?notes=hostile and so on. Default "owner". */
  notes?: MockNotesScenario;
  /** Deleting iPhone photos (round 4): ?photodelete=iCloudPhotos and so on. Default "ok". */
  photoDelete?: MockPhotoDeleteScenario;
  /** A delete job's pace (v0.4.1): ?deletespeed=slow. Default "normal". */
  deleteSpeed?: MockDeleteSpeed;
}

/** A JSON-RPC error raised by a mock handler. */
export class RpcFault extends Error {
  constructor(readonly error: JsonRpcErrorObject) {
    super(error.message);
  }
}

const MESSAGES: Record<string, string> = {
  device_not_found: 'That phone is not connected.',
  device_disconnected: 'The phone was disconnected.',
  device_locked: 'Unlock the phone to continue.',
  trust_required: 'Tap Trust on the iPhone first.',
  unauthorized: 'Allow USB debugging on the phone first.',
  not_supported: 'This phone can’t do that.',
  path_not_found: 'That file or folder is no longer there.',
  permission_denied: 'The phone doesn’t allow access here.',
  already_exists: 'Something with that name is already here.',
  install_failed: 'The app could not be installed.',
  driver_missing: 'A driver this phone needs is missing.',
  protocol_error: 'The phone answered in a way the app did not expect.',
  internal: 'Something went wrong in the app.',
};

function fault(code: string, message?: string, detail?: string): RpcFault {
  return new RpcFault(deviceError(code, message ?? MESSAGES[code] ?? MESSAGES.internal!, detail));
}

function badParams(message: string): RpcFault {
  return new RpcFault({ code: RPC_INVALID_PARAMS, message });
}

const basename = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path;

/** A photo deleted a moment ago, still on screen until the list is read again (round 4): an empty picture, never src="". */
const GONE_IMAGE = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw==';
const en = new Intl.NumberFormat('en-US');

type Handler<M extends MethodName> = (params: MethodParams<M>) => MethodResult<M>;

export class MockBackend {
  readonly devices = new Map<string, DeviceSummary>();
  private driverList: DriverStatus[];
  private settings: Settings;
  private readonly libraries = new Map<string, MockLibrary>();
  private readonly fileSystems = new Map<string, MockFileSystem>();
  private readonly appLists = new Map<string, AppInfo[]>();
  private readonly indexed = new Set<string>();
  private readonly engine: TransferEngine;
  private readonly timeline: { at: number; run: () => void }[] = [];
  private elapsed = 0;
  private pickIndex = 0;
  /** Sizes of files the page handed over (by the fake path the mock gave them). */
  private readonly fileSizes = new Map<string, number>();
  private readonly indexing: boolean;
  private readonly now: () => number;
  private readonly longNames: boolean;
  // Desktop (round 2)
  private readonly pc = new MockPc();
  /** What notifications.show was asked to show, for tests. */
  readonly notifications: NotificationShowParams[] = [];

  constructor(
    private readonly emit: <E extends EventName>(event: E, params: BridgeEvents[E]) => void,
    options: MockOptions = {},
  ) {
    this.now = options.now ?? (() => Date.now());
    this.indexing = options.indexing ?? true;
    this.settings = { ...defaultSettings, ...options.settings };
    this.localSend.scenario = options.localSend ?? 'ready';
    this.updates.configure(options.update ?? 'idle', options.notes ?? 'owner');
    this.photoDelete.scenario = options.photoDelete ?? 'ok';
    this.photoDelete.speed = options.deleteSpeed ?? 'normal';
    const scenario = options.scenario ?? 'ready';
    this.driverList = scenario === 'limited' || scenario === 'driverMissing' ? driversIosMissing : drivers;

    for (const [id, list] of Object.entries(sampleApps)) this.appLists.set(id, list.map((a) => ({ ...a })));
    this.longNames = options.longNames ?? false;
    if (this.longNames) addLongNameApps(this.appLists);

    switch (scenario) {
      case 'ready':
        this.devices.set(IPHONE_ID, { ...iphone });
        this.devices.set(ANDROID_ID, { ...android });
        break;
      case 'iphoneOnly':
        this.devices.set(IPHONE_ID, { ...iphone });
        break;
      case 'trust':
        this.devices.set(IPHONE_ID, { ...iphone, state: 'awaitingTrust' });
        this.devices.set(ANDROID_ID, { ...android, connection: 'usb', state: 'unauthorized' });
        break;
      case 'limited':
        this.devices.set(IPHONE_ID, { ...iphone, mode: 'limited', capabilities: [...IPHONE_LIMITED_CAPABILITIES] });
        break;
      case 'empty':
      case 'driverMissing':
        break;
    }
    // Deleting photos (round 4): an iPhone that can't delete in this scenario has no photosDelete.
    for (const [id, device] of this.devices) this.devices.set(id, this.photoDelete.adjust(device));

    this.engine = new TransferEngine(
      (job) => this.emit('transfer.updated', job),
      this.now,
      () => this.settings.conflictRule,
    );
    if (options.seedQueue ?? true) {
      const seeded = seedTransfers(this.now());
      this.engine.seed(seeded, { holdSeconds: { 'job-snapseed': 6 } });
      // Work for a phone that is not ready waits for it.
      for (const deviceId of new Set(seeded.map((j) => j.deviceId))) {
        if (this.devices.get(deviceId)?.state !== 'ready') this.engine.deviceOffline(deviceId);
      }
    }
  }

  // ---------- Simulation controls (mock only; the palette and Welcome screen call these) ----------

  /** Pulls the cable: the phone is disconnected and its transfers wait for it. */
  unplug(deviceId: string): void {
    const device = this.devices.get(deviceId);
    if (!device || device.state === 'disconnected') return;
    this.engine.deviceOffline(deviceId);
    this.setDevice({ ...device, state: 'disconnected' });
  }

  /** Plugs the same phone back in: it reconnects and its transfers resume. */
  plugBack(deviceId: string): void {
    const device = this.devices.get(deviceId);
    if (!device) return this.connectNew(deviceId);
    this.setDevice({ ...device, state: 'connecting' });
    this.after(900, () => {
      const current = this.devices.get(deviceId);
      if (!current || current.state !== 'connecting') return;
      this.setDevice({ ...current, state: 'ready' });
      this.engine.deviceOnline(deviceId);
      this.photoBackup.connected(deviceId); // photo backup (round 3): "When this phone connects"
    });
  }

  /** A phone this PC has not seen: it connects, then asks to Trust / Allow USB debugging. */
  connectNew(deviceId: string): void {
    const base = deviceId === IPHONE_ID ? iphone : deviceId === ANDROID_ID ? { ...android, connection: 'usb' as const } : null;
    if (!base) return;
    const added = !this.devices.has(deviceId);
    const device: DeviceSummary = this.photoDelete.adjust({ ...base, state: 'connecting' });
    this.devices.set(deviceId, device);
    this.emit(added ? 'device.added' : 'device.changed', { ...device });
    this.after(1200, () => {
      const current = this.devices.get(deviceId);
      if (!current || current.state !== 'connecting') return;
      this.setDevice({ ...current, state: current.platform === 'ios' ? 'awaitingTrust' : 'unauthorized' });
    });
  }

  /** The user tapped Trust (iPhone) or Allow (Android) on the phone. */
  approve(deviceId: string): void {
    const device = this.devices.get(deviceId);
    if (!device || (device.state !== 'awaitingTrust' && device.state !== 'unauthorized' && device.state !== 'locked')) return;
    this.setDevice({ ...device, state: 'connecting' });
    this.after(800, () => {
      const current = this.devices.get(deviceId);
      if (!current || current.state !== 'connecting') return;
      this.setDevice({ ...current, state: 'ready' });
      this.engine.deviceOnline(deviceId);
      this.photoBackup.connected(deviceId); // photo backup (round 3): "When this phone connects"
    });
  }

  /** Removes a phone from the list altogether. */
  remove(deviceId: string): void {
    if (!this.devices.delete(deviceId)) return;
    this.engine.deviceOffline(deviceId);
    this.emit('device.removed', { deviceId });
  }

  setDrivers(list: DriverStatus[]): void {
    this.driverList = list;
    this.emit('drivers.changed', list.map((d) => ({ ...d })));
  }

  /** Advances simulated time: scheduled steps run, transfers progress. */
  tick(ms: number): void {
    this.elapsed += ms;
    const due = this.timeline.filter((t) => t.at <= this.elapsed);
    if (due.length > 0) {
      this.timeline.splice(0, this.timeline.length, ...this.timeline.filter((t) => t.at > this.elapsed));
      for (const step of due) step.run();
    }
    this.engine.tick(ms);
    this.backups.tick(ms);
  }

  // ---------- Images (the mock's stand-in for https://device.local/) ----------

  thumbUrl(deviceId: string, assetId: string): string {
    const asset = this.libraries.get(deviceId)?.byId.get(assetId);
    return asset ? thumbnailUrl(asset) : GONE_IMAGE;
  }

  photoUrl(deviceId: string, assetId: string): string {
    const asset = this.libraries.get(deviceId)?.byId.get(assetId);
    return asset ? photoUrl(asset) : GONE_IMAGE;
  }

  iconUrl(deviceId: string, appId: string): string {
    const app = this.appLists.get(deviceId)?.find((a) => a.id === appId);
    return appIconUrl(appId, app?.name ?? appId, appIconColours[appId]);
  }

  /** Makes up a local file of a given size (for demos: a film to copy into VLC). */
  registerLocalFile(name: string, size: number): string {
    const path = `C:\\Users\\Nima\\Videos\\${name}`;
    this.fileSizes.set(path, size);
    return path;
  }

  /** Gives dropped File objects the paths the host would have read from CoreWebView2File.Path. */
  registerFiles(files: readonly File[]): string[] {
    return files.map((file) => {
      const path = `C:\\Users\\Nima\\Downloads\\${file.name}`;
      this.fileSizes.set(path, file.size);
      return path;
    });
  }

  // ---------- JSON-RPC ----------

  handle(method: string, params: unknown): unknown {
    const handler = (this.handlers as Record<string, ((p: unknown) => unknown) | undefined>)[method];
    if (!handler) throw new RpcFault({ code: RPC_METHOD_NOT_FOUND, message: `Unknown method ${method}.` });
    return handler((params ?? {}) as never);
  }

  // Photos backup and adding photos to an iPhone app (round 2): see ./photosBackup.ts.
  private readonly photoBackup = new MockPhotoBackup({
    requireReady: (deviceId, capability) => this.requireReady(deviceId, capability),
    library: (deviceId) => this.library(deviceId),
    engine: () => this.engine,
    settings: () => this.settings,
    apps: (deviceId) => this.appLists.get(deviceId) ?? [],
    fs: (deviceId) => this.fs(deviceId),
    sizeOfLocal: (path) => this.sizeOfLocal(path),
    now: () => this.now(),
    fault,
    badParams,
  });

  /** Toolbox methods (round 2); its arrows reach this backend only when called. */
  private readonly toolbox = new MockToolbox({
    emit: (event, params) => this.emit(event, params),
    ready: (deviceId, capability) => this.requireReady(deviceId, capability),
    after: (ms, run) => this.after(ms, run),
    now: () => this.now(),
    details: (deviceId) => structuredClone(details[deviceId] ?? { rows: [] }),
    fault: (code, message) => fault(code, message),
    badParams: (message) => badParams(message),
  });

  /** Backups, contacts and messages (round 2); its handlers are part of the table below. */
  readonly backups = new MockBackups({
    emit: (event, params) => this.emit(event, params),
    now: () => this.now(),
    device: (id) => this.devices.get(id),
    requireReady: (id, capability) => this.requireReady(id, capability),
    settings: () => this.settings,
    fault: (code, message) => fault(code, message),
    badParams: (message) => badParams(message),
    restart: (id) => {
      this.unplug(id);
      this.after(2500, () => this.plugBack(id));
    },
  });

  /** LocalSend on the phone (round 3): ./localSend.ts; its handlers end the table below. */
  readonly localSend = new MockLocalSend({
    requireReady: (deviceId, capability) => this.requireReady(deviceId, capability),
    engine: () => this.engine,
    sizeOfLocal: (path) => this.sizeOfLocal(path),
    emit: (event, params) => this.emit(event, params),
    after: (ms, run) => this.after(ms, run),
    fault: (code, message) => fault(code, message),
    badParams: (message) => badParams(message),
  });

  /** Updates (round 4): ./updates.ts; its handlers end the table below. */
  readonly updates = new MockUpdates({
    emit: (event, params) => this.emit(event, params),
    now: () => this.now(),
    after: (ms, run) => this.after(ms, run),
    fault: (code, message) => fault(code, message),
    badParams: (message) => badParams(message),
  });

  /** WhatsApp (round 4): ./whatsApp.ts; its handlers end the table below. */
  private readonly whatsApp = new MockWhatsApp({
    backups: () => this.backups.handlers['backup.list']({}),
    startExport: (backup, title, destination, files, bytes, ms) => this.backups.startExport(backup, title, destination, files, bytes, ms),
    requireReady: (deviceId, capability) => this.requireReady(deviceId, capability),
    fs: (deviceId) => this.fs(deviceId),
    apps: (deviceId) => this.appLists.get(deviceId) ?? [],
    addJob: (spec) => this.engine.add(spec),
    now: () => this.now(),
    fault: (code, message) => fault(code, message),
    badParams: (message) => badParams(message),
  });

  /** Deleting photos, iPhone included (round 4): ./photoDelete.ts; its handlers end the table below. */
  readonly photoDelete = new MockPhotoDelete({
    requireReady: (deviceId, capability) => this.requireReady(deviceId, capability),
    device: (deviceId) => this.devices.get(deviceId),
    setDevice: (device) => this.setDevice(device),
    library: (deviceId) => this.library(deviceId),
    addJob: (spec) => this.engine.add(spec),
    job: (jobId) => this.engine.get(jobId),
    cancelJob: (jobId) => void this.engine.cancel(jobId),
    fault: (code, message) => fault(code, message),
    badParams: (message) => badParams(message),
  });

  readonly handlers: { [M in MethodName]: Handler<M> } = {
    ...this.backups.handlers,
    'app.hello': () => ({ version: this.updates.version, isWebView: false }),
    'drivers.status': () => this.driverList.map((d) => ({ ...d })),
    'device.list': () => [...this.devices.values()].map((d) => ({ ...d, capabilities: [...d.capabilities] })),
    'device.storage': ({ deviceId }) => {
      this.requireReady(deviceId, 'storage');
      return structuredClone(storage[deviceId]!);
    },
    'device.battery': ({ deviceId }) => {
      this.requireReady(deviceId, 'battery');
      return { ...battery[deviceId]! };
    },
    'device.details': ({ deviceId }) => {
      this.requireReady(deviceId, 'info');
      return structuredClone(details[deviceId] ?? { rows: [] });
    },
    'photos.albums': ({ deviceId }) => {
      this.requireReady(deviceId, 'photosRead');
      const library = this.library(deviceId);
      this.startIndexing(deviceId, library.assets.length);
      return library.albums.map((a) => ({ ...a }));
    },
    'photos.list': ({ deviceId, albumId, offset, limit }) => {
      this.requireReady(deviceId, 'photosRead');
      if (!Number.isInteger(offset) || offset < 0 || !Number.isInteger(limit) || limit < 1) {
        throw badParams('offset and limit must be whole numbers.');
      }
      const items = assetsInAlbum(this.library(deviceId), albumId);
      return { items: items.slice(offset, offset + Math.min(limit, 5000)).map(toContractAsset), total: items.length };
    },
    'photos.export': ({ deviceId, assetIds, destination, convertHeic, folderPattern }) => {
      this.requireReady(deviceId, 'photosRead');
      const library = this.library(deviceId);
      const assets = assetIds.map((id) => library.byId.get(id)).filter((a): a is MockAsset => !!a && !a.inCloudOnly);
      if (assets.length === 0) throw fault('path_not_found', 'None of these photos can be exported over USB.');
      const items = assets.map<JobItem>((a) => ({
        name: exportName(a, convertHeic, folderPattern),
        size: convertHeic && a.fileName.endsWith('.HEIC') ? Math.round(a.size * 1.6) : a.size,
      }));
      const what = assets.length === 1 ? assets[0]!.fileName : `${en.format(assets.length)} photos`;
      const job = this.engine.add({ deviceId, kind: 'photosExport', title: `Exporting ${what} to ${destination}`, items });
      return { jobId: job.id };
    },
    'photos.delete': ({ deviceId, assetIds }) => {
      this.requireReady(deviceId, 'photosDelete');
      const library = this.library(deviceId);
      const ids = new Set(assetIds);
      const before = library.assets.length + library.hidden.length;
      library.assets = library.assets.filter((a) => !ids.has(a.id));
      library.hidden = library.hidden.filter((a) => !ids.has(a.id));
      for (const id of ids) library.byId.delete(id);
      this.libraries.set(deviceId, { ...library, albums: recount(library) });
      return { deleted: before - library.assets.length - library.hidden.length };
    },
    'photos.import': ({ deviceId, files }) => {
      this.requireReady(deviceId, 'photosImport');
      const items = files.map<JobItem>((path) => ({ name: basename(path), size: this.sizeOfLocal(path) }));
      if (items.length === 0) throw badParams('No files to import.');
      const job = this.engine.add({
        deviceId,
        kind: 'photosImport',
        title: items.length === 1 ? `Adding ${items[0]!.name} to Camera` : `Adding ${en.format(items.length)} photos to Camera`,
        items,
        onItemDone: (item) => this.addImportedAsset(deviceId, item),
      });
      return { jobId: job.id };
    },
    'files.roots': ({ deviceId }) => {
      this.requireReady(deviceId, 'files');
      const sharing = (this.appLists.get(deviceId) ?? []).filter((a) => a.fileSharing);
      return MockFileSystem.rootsFor(deviceId, sharing);
    },
    'files.list': ({ deviceId, root, path }) => {
      this.requireReady(deviceId, 'files');
      return this.fsCall(() => this.fs(deviceId).list(root, path));
    },
    'files.mkdir': ({ deviceId, root, path }) => {
      this.requireReady(deviceId, 'files');
      return this.fsCall(() => this.fs(deviceId).mkdir(root, path));
    },
    'files.rename': ({ deviceId, root, from, to }) => {
      this.requireReady(deviceId, 'files');
      return this.fsCall(() => this.fs(deviceId).rename(root, from, to));
    },
    'files.delete': ({ deviceId, root, paths, recursive }) => {
      this.requireReady(deviceId, 'files');
      return { deleted: this.fsCall(() => this.fs(deviceId).delete(root, paths, recursive)) };
    },
    'files.copyToPc': ({ deviceId, root, paths, destination }) => {
      this.requireReady(deviceId, 'files');
      const fs = this.fs(deviceId);
      const items = paths.map<JobItem>((p) => ({ name: normalise(p) === '/' ? this.rootLabel(deviceId, root, '/') : basename(p), size: Math.max(1, fs.sizeOf(root, p)) }));
      if (items.length === 0) throw badParams('Nothing to copy.');
      const what = items.length === 1 ? items[0]!.name : `${en.format(items.length)} items`;
      const job = this.engine.add({ deviceId, kind: 'copyToPc', title: `Copying ${what} to ${destination}`, items });
      return { jobId: job.id };
    },
    'files.copyToDevice': ({ deviceId, root, files, path }) => {
      this.requireReady(deviceId, 'files');
      const fs = this.fs(deviceId);
      if (!fs.hasRoot(root)) throw fault('path_not_found');
      const items = files.map<JobItem>((local) => {
        const name = basename(local);
        const existing = fs.stat(root, joinPath(path, name));
        return {
          name,
          size: this.sizeOfLocal(local),
          ...(existing && !existing.isDirectory ? { existingSize: existing.size } : {}),
        };
      });
      if (items.length === 0) throw badParams('No files to copy.');
      const label = this.rootLabel(deviceId, root, path);
      const what = items.length === 1 ? items[0]!.name : `${en.format(items.length)} files`;
      const job = this.engine.add({
        deviceId,
        kind: 'copyToDevice',
        title: `${what} into ${label}`,
        items,
        onItemDone: (item) => fs.writeFile(root, joinPath(path, item.name), item.size),
      });
      return { jobId: job.id };
    },
    'apps.list': ({ deviceId, includeSystem }) => {
      this.requireReady(deviceId, 'appsList');
      return (this.appLists.get(deviceId) ?? [])
        .filter((a) => includeSystem || a.source !== 'system')
        .map((a) => ({ ...a }));
    },
    'apps.install': ({ deviceId, files }) => {
      const device = this.requireReady(deviceId, 'appsInstall');
      if (files.length === 0) throw badParams('No app file was given.');
      const allowed = device.platform === 'ios' ? ['.ipa'] : ['.apk', '.apks', '.xapk'];
      const names = files.map(basename);
      const wrong = names.find((n) => !allowed.some((ext) => n.toLowerCase().endsWith(ext)));
      if (wrong) {
        throw fault(
          'install_failed',
          device.platform === 'ios' ? 'Only .ipa files install on an iPhone.' : 'Only .apk, .apks or .xapk files install on Android.',
          `unsupported package: ${wrong}`,
        );
      }
      const main = names[0]!;
      const unsigned = main.toLowerCase().includes('unsigned');
      const items = files.map<JobItem>((p) => ({ name: basename(p), size: this.sizeOfLocal(p, 60 * MB) }));
      const job = this.engine.add({
        deviceId,
        kind: 'appInstall',
        title: `Installing ${main}`,
        items,
        failWith: unsigned
          ? { code: 'install_failed', message: device.platform === 'ios' ? 'This .ipa isn’t signed for this iPhone.' : 'The phone refused this app.' }
          : undefined,
        onDone: () => this.addInstalledApp(deviceId, main, items[0]!.size),
      });
      return { jobId: job.id };
    },
    'apps.uninstall': ({ deviceId, appId }) => {
      this.requireReady(deviceId, 'appsUninstall');
      const list = this.appLists.get(deviceId) ?? [];
      const app = list.find((a) => a.id === appId);
      if (!app) throw fault('path_not_found', 'That app is no longer on the phone.');
      if (app.source === 'system') throw fault('not_supported', 'System apps can’t be removed.');
      this.appLists.set(deviceId, list.filter((a) => a.id !== appId));
      return {};
    },
    'apps.export': ({ deviceId, appId, destination }) => {
      this.requireReady(deviceId, 'appsExport');
      const app = (this.appLists.get(deviceId) ?? []).find((a) => a.id === appId);
      if (!app) throw fault('path_not_found', 'That app is no longer on the phone.');
      const job = this.engine.add({
        deviceId,
        kind: 'appExport',
        title: `Exporting ${app.name} to ${destination}`,
        items: [{ name: `${app.id}.apk`, size: app.appBytes }],
      });
      return { jobId: job.id };
    },
    'tools.screenshot': ({ deviceId }) => {
      const device = this.requireReady(deviceId, 'screenshot');
      return { url: phoneScreenshotUrl(hashString(deviceId) + this.elapsed, device.platform) };
    },
    'tools.reboot': ({ deviceId, target }) => {
      const capability: DeviceCapability =
        target === 'recovery' ? 'rebootToRecovery' : target === 'bootloader' ? 'rebootToBootloader' : 'reboot';
      this.requireReady(deviceId, capability);
      this.after(300, () => this.unplug(deviceId));
      if (target === 'system') this.after(7000, () => this.plugBack(deviceId));
      return {};
    },
    'tools.shutdown': ({ deviceId }) => {
      this.requireReady(deviceId, 'shutdown');
      this.after(300, () => this.unplug(deviceId));
      return {};
    },
    'transfers.list': () => this.engine.list(),
    'transfers.pause': ({ jobId }) => this.jobCall(this.staged(() => this.engine.pause(jobId))),
    'transfers.resume': ({ jobId }) => this.jobCall(this.staged(() => this.engine.resume(jobId))),
    'transfers.cancel': ({ jobId }) => this.jobCall(this.engine.cancel(jobId)),
    'transfers.resolveConflict': ({ jobId, choice, applyToAll }) => {
      if (!['replace', 'keepBoth', 'skip'].includes(choice)) throw badParams('choice must be replace, keepBoth or skip.');
      return this.jobCall(this.engine.resolveConflict(jobId, choice, applyToAll));
    },
    'shell.pickFolder': () => {
      const path = pickableFolders[this.pickIndex % pickableFolders.length]!;
      this.pickIndex++;
      return { path };
    },
    'shell.revealInExplorer': () => ({}),
    'settings.get': () => ({ ...this.settings }),
    'settings.set': (patch) => {
      const hiddenProblem = hiddenSettingsProblem(patch);
      if (hiddenProblem) throw badParams(hiddenProblem);
      this.settings = { ...this.settings, ...pickSettings(patch) };
      return { ...this.settings };
    },
    // Desktop (round 2)
    'device.rename': ({ deviceId, name }) => {
      const device = this.requireReady(deviceId, 'rename');
      const trimmed = typeof name === 'string' ? name.trim() : '';
      if (!trimmed || trimmed.length > 64) throw badParams('Invalid params: The name must be 1 to 64 characters.');
      const renamed = { ...device, name: trimmed };
      this.setDevice(renamed);
      return { ...renamed, capabilities: [...renamed.capabilities] };
    },
    'transfers.retry': ({ jobId }) => {
      if (this.engine.list().find((j) => j.id === jobId)?.kind === 'dragOut') throw badParams("A drag to File Explorer can't be retried; drag the items again.");
      const outcome = this.engine.retry(jobId);
      if (outcome === 'missing') throw badParams('There is no transfer with that id.');
      if (outcome === 'nothing') throw fault('internal', 'This transfer has nothing to try again.');
      return {};
    },
    'transfers.clear': ({ jobIds }) => ({ cleared: this.engine.clear(jobIds) }),
    'notifications.show': (params) => {
      if (!params || typeof params.title !== 'string' || !params.title.trim()) throw badParams('Invalid params: title is required.');
      this.notifications.push({ ...params });
      return {};
    },
    'pc.roots': () => this.pc.roots(),
    'pc.list': ({ path, showHidden }) => this.pcCall(() => this.pc.list(path, showHidden ?? false)),
    'pc.mkdir': ({ path }) => this.pcCall(() => this.pc.mkdir(path)),
    'pc.rename': ({ path, name }) => this.pcCall(() => this.pc.rename(path, name)),
    'shell.openFile': ({ path }) => this.pcCall(() => {
      this.pc.requireFile(path);
      return {};
    }),
    // A plain browser can't start a native drag; the page falls back to its own drop handling.
    'shell.dragOut': () => ({ effect: 'none', droppedInside: null }),
    // App files (round 2)
    'appFiles.list': ({ deviceId }) => {
      const device = this.requireReady(deviceId, 'appDocuments');
      return mockAppFiles(device.platform, this.fs(deviceId), this.appLists.get(deviceId) ?? []);
    },
    'appFiles.measure': ({ deviceId, root, paths }) => {
      const device = this.requireReady(deviceId, root.startsWith('app:') ? 'appDocuments' : 'files');
      if (!root || !Array.isArray(paths) || paths.length === 0) throw badParams('root and at least one path are required.');
      // The host opens app folders as roots of their own; listing them once mounts them here.
      if (!this.fs(deviceId).hasRoot(root)) mockAppFiles(device.platform, this.fs(deviceId), this.appLists.get(deviceId) ?? []);
      if (!this.fs(deviceId).hasRoot(root)) throw fault('path_not_found');
      return mockMeasure(this.fs(deviceId), root, paths);
    },
    ...this.photoBackup.handlers,
    // ---------- Clean-up and battery history (round 2) ----------
    ...cleanupHandlers({
      requireReady: (deviceId, capability) => this.requireReady(deviceId, capability),
      library: (deviceId) => this.library(deviceId),
      apps: (deviceId) => this.appLists.get(deviceId) ?? [],
      setApps: (deviceId, list) => void this.appLists.set(deviceId, list),
      fs: (deviceId) => this.fs(deviceId),
      storage: (deviceId) => storage[deviceId],
      addJob: (spec) => this.engine.add(spec),
      emit: (event, params) => this.emit(event, params),
      after: (ms, run) => this.after(ms, run),
      now: () => this.now(),
      fault: (code, message) => fault(code, message),
      badParams,
      startPhotoDelete: (deviceId, items, options) => this.photoDelete.start(deviceId, items, options),
    }),
    ...batteryHandlers({
      requireReady: (deviceId, capability) => this.requireReady(deviceId, capability),
      battery: (deviceId) => battery[deviceId],
      emit: (event, params) => this.emit(event, params),
      now: () => this.now(),
      badParams,
    }),
    ...this.toolbox.handlers,
    ...this.localSend.handlers,
    ...this.updates.handlers,
    ...this.whatsApp.handlers,
    ...this.photoDelete.handlers,
  };

  private pcCall<T>(run: () => T): T {
    if (typeof run !== 'function') throw badParams('Invalid params.');
    try {
      return run();
    } catch (error) {
      if (error instanceof PcError) throw error.code === 'invalid_params' ? badParams(`Invalid params: ${error.message}`) : fault(error.code, error.message);
      throw error;
    }
  }

  // ---------- helpers ----------

  private setDevice(device: DeviceSummary): void {
    this.devices.set(device.id, device);
    this.emit('device.changed', { ...device, capabilities: [...device.capabilities] });
  }

  private after(ms: number, run: () => void): void {
    this.timeline.push({ at: this.elapsed + ms, run });
  }

  private requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary {
    if (typeof deviceId !== 'string') throw badParams('deviceId is required.');
    const device = this.devices.get(deviceId);
    if (!device) throw fault('device_not_found');
    switch (device.state) {
      case 'disconnected':
        throw fault('device_disconnected');
      case 'awaitingTrust':
        throw fault('trust_required');
      case 'unauthorized':
        throw fault('unauthorized');
      case 'locked':
        throw fault('device_locked');
      case 'connecting':
        throw fault('device_disconnected', 'The phone is still connecting.');
      case 'ready':
        break;
    }
    if (!device.capabilities.includes(capability)) {
      throw fault('not_supported', undefined, `capability ${capability} is not available`);
    }
    return device;
  }

  private library(deviceId: string): MockLibrary {
    let library = this.libraries.get(deviceId);
    if (!library) {
      library = generateLibrary(deviceId);
      this.libraries.set(deviceId, library);
    }
    return library;
  }

  private startIndexing(deviceId: string, total: number): void {
    if (this.indexed.has(deviceId)) return;
    this.indexed.add(deviceId);
    if (!this.indexing) return;
    // About six seconds, like reading a large library's index the first time.
    const steps = 24;
    for (let i = 0; i <= steps; i++) {
      this.after(i * 250, () =>
        this.emit('photos.indexProgress', { deviceId, done: Math.round((total * i) / steps), total }),
      );
    }
  }

  private fs(deviceId: string): MockFileSystem {
    let fs = this.fileSystems.get(deviceId);
    if (!fs) {
      fs = new MockFileSystem(deviceId);
      if (this.longNames) addLongNameFiles(deviceId, fs);
      this.fileSystems.set(deviceId, fs);
      // App folders are roots of their own, as the host opens them.
      const platform = this.devices.get(deviceId)?.platform;
      if (platform) mockAppFiles(platform, fs, this.appLists.get(deviceId) ?? []);
    }
    return fs;
  }

  private fsCall<T>(run: () => T): T {
    try {
      return run();
    } catch (error) {
      const code = (error as { code?: string }).code;
      if (code) throw fault(code);
      throw error;
    }
  }

  /** A delete job (v0.4.1) can't be paused or resumed: the host answers with an error. */
  private staged(run: () => boolean): boolean {
    try {
      return run();
    } catch (error) {
      if (error instanceof StagedRefusal) throw fault('internal', error.message);
      throw error;
    }
  }

  private jobCall(found: boolean): Record<string, never> {
    if (!found) throw badParams('There is no transfer with that id.');
    return {};
  }

  private rootLabel(deviceId: string, root: string, path: string): string {
    const sharing = (this.appLists.get(deviceId) ?? []).find((a) => `app:${a.id}` === root);
    const appFolder = /^(appmedia|appdata|shared):(.+)$/.exec(root);
    const base = sharing?.name ?? appFolder?.[2] ?? (root === 'internal' ? 'Internal storage' : root === 'media' ? 'Media folder' : root);
    const folder = normalise(path);
    return folder === '/' ? base : `${base}${folder}`;
  }

  /** A size for a local file: the dropped File's size, else a plausible one from its name. */
  private sizeOfLocal(path: string, fallback?: number): number {
    const known = this.fileSizes.get(path);
    if (known !== undefined) return Math.max(1, known);
    if (fallback !== undefined) return fallback;
    const h = hashString(path);
    if (/\.(mkv|mp4|mov|avi)$/i.test(path)) return Math.round((1 + (h % 11)) * GB);
    if (/\.(jpe?g|heic|png)$/i.test(path)) return Math.round((2 + (h % 5)) * MB);
    return Math.round((0.2 + (h % 40) / 4) * MB);
  }

  private addImportedAsset(deviceId: string, item: JobItem): void {
    const library = this.library(deviceId);
    const id = `import-${library.assets.length + 1}-${hashString(item.name)}`;
    const asset: MockAsset = {
      id,
      fileName: item.name,
      kind: /\.(mp4|mov)$/i.test(item.name) ? 'video' : 'photo',
      takenAt: new Date(this.now()).toISOString(),
      size: item.size,
      width: 4000,
      height: 3000,
      durationSeconds: null,
      isFavorite: false,
      inCloudOnly: false,
      livePairId: null,
      seed: hashString(id),
      albumIds: ['folder-1'],
    };
    library.assets.unshift(asset);
    library.byId.set(id, asset);
    this.libraries.set(deviceId, { ...library, albums: recount(library) });
  }

  private addInstalledApp(deviceId: string, fileName: string, size: number): void {
    const list = this.appLists.get(deviceId) ?? [];
    const stem = fileName.replace(/\.(ipa|apks?|xapk)$/i, '');
    const [rawName, version] = stem.split(/[_ -]v?(?=\d)/);
    const name = (rawName ?? stem).replace(/[-_]+/g, ' ').trim() || stem;
    const existing = list.find((a) => a.name.toLowerCase() === name.toLowerCase());
    if (existing) {
      existing.installedAt = new Date(this.now()).toISOString();
      if (version) existing.version = version;
      return;
    }
    const prefix = deviceId === IPHONE_ID ? 'com.example' : 'com.example.android';
    list.push({
      id: `${prefix}.${name.toLowerCase().replace(/[^a-z0-9]+/g, '')}`,
      name,
      version: version ?? '1.0',
      appBytes: size,
      dataBytes: 0,
      source: 'sideloaded',
      fileSharing: false,
      installedAt: new Date(this.now()).toISOString(),
    });
    this.appLists.set(deviceId, list);
  }
}

function exportName(asset: MockAsset, convertHeic: boolean, pattern: FolderPattern): string {
  const name = convertHeic ? asset.fileName.replace(/\.HEIC$/i, '.JPG') : asset.fileName;
  if (pattern === '' || !asset.takenAt) return name;
  const t = new Date(asset.takenAt);
  return `${t.getUTCFullYear()}\\${String(t.getUTCMonth() + 1).padStart(2, '0')}\\${name}`;
}

function recount(library: MockLibrary): MockLibrary['albums'] {
  return library.albums.map((album) => ({ ...album, count: assetsInAlbum(library, album.id).length }));
}

const SETTINGS_KEYS: (keyof Settings)[] = [
  'language',
  'theme',
  'density',
  'photoExportFolder',
  'photoFolderPattern',
  'convertHeic',
  'fileCopyFolder',
  'conflictRule',
  'closeToTray',
  'notifyOnConnect',
  'autoUpdate',
  'photoBackupFolder',
  'photoBackupPattern',
  'photoBackupConvertHeic',
  'backupFolder',
  'backupKeep',
  // Hidden photos in the photo backup (round 4, hiddenbackup)
  'photoBackupHidden',
  'photoBackupHiddenLayout',
  'photoBackupHiddenFolder',
];

function pickSettings(patch: Partial<Settings>): Partial<Settings> {
  const out: Partial<Settings> = {};
  for (const key of SETTINGS_KEYS) {
    if (patch[key] !== undefined) (out as Record<string, unknown>)[key] = patch[key];
  }
  return out;
}
