import type {
  AppInfo,
  BatteryInfo,
  ConflictChoice,
  DeletedCount,
  DragOutParams,
  DragOutResult,
  NotificationActivation,
  NotificationShowParams,
  PcEntry,
  PcRoot,
  UpdateStatus,
  DeviceDetails,
  DeviceSummary,
  DriverStatus,
  Empty,
  FileEntry,
  FileRoot,
  FolderPattern,
  HelloResult,
  JobStarted,
  Page,
  PhotoAlbum,
  PhotoAsset,
  PhotoBackupCount,
  PhotoBackupStatus,
  RebootTarget,
  Settings,
  StorageInfo,
  TransferJob,
} from './types';
import type { AppFilesOverview, FolderUsage } from './appFiles';

type DeviceParams = { deviceId: string };

/** Every method in docs/bridge-contract.md: its params and its result. */
export interface BridgeMethods {
  'app.hello': { params: Empty; result: HelloResult };
  'drivers.status': { params: Empty; result: DriverStatus[] };
  'device.list': { params: Empty; result: DeviceSummary[] };
  'device.storage': { params: DeviceParams; result: StorageInfo };
  'device.battery': { params: DeviceParams; result: BatteryInfo };
  'device.details': { params: DeviceParams; result: DeviceDetails };
  'photos.albums': { params: DeviceParams; result: PhotoAlbum[] };
  'photos.list': {
    params: DeviceParams & { albumId?: string | null; offset: number; limit: number };
    result: Page<PhotoAsset>;
  };
  'photos.export': {
    params: DeviceParams & {
      assetIds: string[];
      destination: string;
      convertHeic: boolean;
      folderPattern: FolderPattern;
    };
    result: JobStarted;
  };
  'photos.delete': { params: DeviceParams & { assetIds: string[] }; result: DeletedCount };
  'photos.import': { params: DeviceParams & { files: string[] }; result: JobStarted };
  'files.roots': { params: DeviceParams; result: FileRoot[] };
  'files.list': { params: DeviceParams & { root: string; path: string }; result: FileEntry[] };
  'files.mkdir': { params: DeviceParams & { root: string; path: string }; result: FileEntry };
  'files.rename': { params: DeviceParams & { root: string; from: string; to: string }; result: FileEntry };
  'files.delete': {
    params: DeviceParams & { root: string; paths: string[]; recursive: boolean };
    result: DeletedCount;
  };
  'files.copyToPc': {
    params: DeviceParams & { root: string; paths: string[]; destination: string };
    result: JobStarted;
  };
  'files.copyToDevice': {
    params: DeviceParams & { root: string; files: string[]; path: string };
    result: JobStarted;
  };
  'apps.list': { params: DeviceParams & { includeSystem: boolean }; result: AppInfo[] };
  'apps.install': { params: DeviceParams & { files: string[] }; result: JobStarted };
  'apps.uninstall': { params: DeviceParams & { appId: string }; result: Empty };
  'apps.export': { params: DeviceParams & { appId: string; destination: string }; result: JobStarted };
  'tools.screenshot': { params: DeviceParams; result: { url: string } };
  'tools.reboot': { params: DeviceParams & { target: RebootTarget }; result: Empty };
  'tools.shutdown': { params: DeviceParams; result: Empty };
  'transfers.list': { params: Empty; result: TransferJob[] };
  'transfers.pause': { params: { jobId: string }; result: Empty };
  'transfers.resume': { params: { jobId: string }; result: Empty };
  'transfers.cancel': { params: { jobId: string }; result: Empty };
  'transfers.resolveConflict': {
    params: { jobId: string; choice: ConflictChoice; applyToAll: boolean };
    result: Empty;
  };
  'shell.pickFolder': { params: { title?: string }; result: { path: string | null } };
  'shell.revealInExplorer': { params: { path: string }; result: Empty };
  'settings.get': { params: Empty; result: Settings };
  'settings.set': { params: Partial<Settings>; result: Settings };
  // Desktop (round 2)
  'device.rename': { params: DeviceParams & { name: string }; result: DeviceSummary };
  'transfers.retry': { params: { jobId: string }; result: Empty };
  'transfers.clear': { params: { jobIds?: string[] }; result: { cleared: number } };
  'notifications.show': { params: NotificationShowParams; result: Empty };
  'pc.roots': { params: Empty; result: PcRoot[] };
  'pc.list': { params: { path: string; showHidden?: boolean }; result: PcEntry[] };
  'pc.mkdir': { params: { path: string }; result: PcEntry };
  'pc.rename': { params: { path: string; name: string }; result: PcEntry };
  'shell.openFile': { params: { path: string }; result: Empty };
  'shell.dragOut': { params: DragOutParams; result: DragOutResult };
  'app.updateStatus': { params: Empty; result: UpdateStatus };
  'app.checkForUpdates': { params: Empty; result: UpdateStatus };
  'app.applyUpdate': { params: Empty; result: Empty };
  // App files (round 2)
  'appFiles.list': { params: DeviceParams; result: AppFilesOverview };
  'appFiles.measure': { params: DeviceParams & { root: string; paths: string[] }; result: FolderUsage };
  // Photos backup and adding photos to an iPhone app (round 2).
  'photos.newCount': { params: DeviceParams; result: PhotoBackupCount };
  'photos.backupStatus': { params: DeviceParams; result: PhotoBackupStatus };
  'photos.backupConfigure': {
    params: DeviceParams & {
      destination?: string;
      folderPattern?: string;
      convertHeic?: boolean;
      includeHidden?: boolean;
      useDefaults?: boolean;
    };
    result: PhotoBackupStatus;
  };
  'photos.backup': { params: DeviceParams; result: JobStarted };
  'photos.backupForget': { params: DeviceParams; result: { forgotten: number } };
  'photos.importToApp': { params: DeviceParams & { appId: string; files: string[] }; result: JobStarted };
}

export type MethodName = keyof BridgeMethods;
export type MethodParams<M extends MethodName> = BridgeMethods[M]['params'];
export type MethodResult<M extends MethodName> = BridgeMethods[M]['result'];

/** Methods whose `files` the page fills from dropped or picked File objects. */
export type FileMethodName = {
  [M in MethodName]: MethodParams<M> extends { files: string[] } ? M : never;
}[MethodName];

/** Runtime list of every method; `satisfies` keeps it in step with BridgeMethods. */
const methodTable = {
  'app.hello': true,
  'drivers.status': true,
  'device.list': true,
  'device.storage': true,
  'device.battery': true,
  'device.details': true,
  'photos.albums': true,
  'photos.list': true,
  'photos.export': true,
  'photos.delete': true,
  'photos.import': true,
  'files.roots': true,
  'files.list': true,
  'files.mkdir': true,
  'files.rename': true,
  'files.delete': true,
  'files.copyToPc': true,
  'files.copyToDevice': true,
  'apps.list': true,
  'apps.install': true,
  'apps.uninstall': true,
  'apps.export': true,
  'tools.screenshot': true,
  'tools.reboot': true,
  'tools.shutdown': true,
  'transfers.list': true,
  'transfers.pause': true,
  'transfers.resume': true,
  'transfers.cancel': true,
  'transfers.resolveConflict': true,
  'shell.pickFolder': true,
  'shell.revealInExplorer': true,
  'settings.get': true,
  'settings.set': true,
  // Desktop (round 2)
  'device.rename': true,
  'transfers.retry': true,
  'transfers.clear': true,
  'notifications.show': true,
  'pc.roots': true,
  'pc.list': true,
  'pc.mkdir': true,
  'pc.rename': true,
  'shell.openFile': true,
  'shell.dragOut': true,
  'app.updateStatus': true,
  'app.checkForUpdates': true,
  'app.applyUpdate': true,
  // App files (round 2)
  'appFiles.list': true,
  'appFiles.measure': true,
  'photos.newCount': true,
  'photos.backupStatus': true,
  'photos.backupConfigure': true,
  'photos.backup': true,
  'photos.backupForget': true,
  'photos.importToApp': true,
} as const satisfies Record<MethodName, true>;

export const METHOD_NAMES = Object.keys(methodTable) as MethodName[];

/** Every event (notification) the host pushes. */
export interface BridgeEvents {
  'device.added': DeviceSummary;
  'device.changed': DeviceSummary;
  'device.removed': { deviceId: string };
  'drivers.changed': DriverStatus[];
  'transfer.updated': TransferJob;
  'photos.indexProgress': { deviceId: string; done: number; total: number };
  // Desktop (round 2)
  'notification.activated': NotificationActivation;
  'app.updateChanged': UpdateStatus;
}

export type EventName = keyof BridgeEvents;

const eventTable = {
  'device.added': true,
  'device.changed': true,
  'device.removed': true,
  'drivers.changed': true,
  'transfer.updated': true,
  'photos.indexProgress': true,
  // Desktop (round 2)
  'notification.activated': true,
  'app.updateChanged': true,
} as const satisfies Record<EventName, true>;

export const EVENT_NAMES = Object.keys(eventTable) as EventName[];
