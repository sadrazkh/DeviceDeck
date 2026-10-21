import type {
  AppInfo,
  BackupDeleteResult,
  BackupDeviceInfo,
  BackupDomain,
  BackupEntry,
  BackupInfo,
  BackupItemRef,
  BackupJob,
  BatteryInfo,
  Contact,
  ContactsFormat,
  Conversation,
  Message,
  MessagesFormat,
  PrunePlan,
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
  PhotoBackupOnConnect,
  PhotoBackupRange,
  PhotoBackupSpace,
  PhotoBackupStatus,
  RebootTarget,
  Settings,
  StorageInfo,
  TransferJob,
} from './types';
import type { AppFilesOverview, FolderUsage } from './appFiles';
import type { BatteryDevice, BatterySnapshot, CleanupFileItem, CleanupItem, CleanupResult, CleanupStatus } from './cleanupTypes';
import type {
  CrashReportList,
  LogLevel,
  LogLine,
  MirrorInput,
  MirrorVideoEvent,
  ReportFormat,
  ToolboxError,
  ToolboxFeatures,
  ToolboxJob,
  WifiQrEvent,
  WirelessResult,
} from './toolbox';
import type { LocalSendDiscovery, LocalSendJobStatus, LocalSendTarget, PhotosToAppStarted } from './localSend';
import type { PhotoDeleteItem, PhotoDeleteReport, PhotoDeleteSupport } from './photoDeleteTypes';

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
      range?: PhotoBackupRange;
      onConnect?: PhotoBackupOnConnect;
      saveEdited?: boolean;
    };
    result: PhotoBackupStatus;
  };
  'photos.backup': { params: DeviceParams; result: JobStarted };
  'photos.backupForget': { params: DeviceParams; result: { forgotten: number } };
  // Round 3: the answer also says which dated folder (and parts of 200) the files go into.
  'photos.importToApp': { params: DeviceParams & { appId: string; files: string[] }; result: PhotosToAppStarted };
  // Clean-up and battery history (round 2)
  'cleanup.status': { params: DeviceParams; result: CleanupStatus };
  'cleanup.analyze': { params: DeviceParams; result: CleanupStatus };
  'cleanup.cancel': { params: DeviceParams; result: Empty };
  'cleanup.deleteMedia': {
    params: DeviceParams & { items: CleanupItem[]; expectedBytes: number; copyFirstTo?: string | null };
    result: CleanupResult;
  };
  'cleanup.deleteFiles': {
    params: DeviceParams & { items: CleanupFileItem[]; expectedBytes: number; copyFirstTo?: string | null };
    result: CleanupResult;
  };
  'cleanup.uninstallApps': {
    params: DeviceParams & { items: CleanupItem[]; expectedBytes: number; copyFirstTo?: string | null };
    result: CleanupResult;
  };
  'cleanup.trimCaches': { params: DeviceParams; result: CleanupResult };
  'battery.devices': { params: Empty; result: BatteryDevice[] };
  'battery.history': { params: DeviceParams; result: BatterySnapshot[] };
  'battery.record': { params: DeviceParams; result: BatterySnapshot };
  'battery.export': { params: DeviceParams & { destination: string; language?: 'en' | 'fa' }; result: { path: string } };

  // ---- Toolbox (round 2) ----
  'toolbox.features': { params: DeviceParams; result: ToolboxFeatures };
  'log.start': { params: DeviceParams; result: { sessionId: string } };
  'log.stop': { params: { sessionId: string }; result: Empty };
  'log.clear': { params: { sessionId: string }; result: Empty };
  'log.export': {
    params: { sessionId: string; minLevel?: LogLevel; search?: string | null; destination?: string | null };
    result: { path: string; count: number };
  };
  'crash.list': { params: DeviceParams; result: CrashReportList };
  'crash.read': { params: DeviceParams & { id: string }; result: { text: string; truncated: boolean; size: number } };
  'crash.export': {
    params: DeviceParams & { ids: string[]; destination?: string | null };
    result: { folder: string; files: string[] };
  };
  'crash.delete': { params: DeviceParams & { ids: string[] }; result: DeletedCount };
  'report.preview': { params: DeviceParams & { language: 'en' | 'fa'; revealSerials: boolean }; result: { html: string } };
  'report.save': {
    params: DeviceParams & { language: 'en' | 'fa'; revealSerials: boolean; format: ReportFormat; destination?: string | null };
    result: { path: string; count: number };
  };
  'heic.status': { params: Empty; result: { available: boolean } };
  'heic.convert': {
    params: { files: string[]; quality: number; destination?: string | null };
    result: { jobId: string; total: number };
  };
  'toolbox.cancelJob': { params: { jobId: string }; result: Empty };
  'mirror.open': { params: DeviceParams; result: Empty };
  'wifi.status': { params: Empty; result: { wirelessDebugging: boolean } };
  'wifi.pair': { params: { host: string; port: number; code: string }; result: WirelessResult };
  'wifi.connect': { params: { host: string; port: number }; result: WirelessResult };
  'wifi.disconnect': { params: DeviceParams; result: Empty };
  'wifi.qrStart': { params: Empty; result: { sessionId: string; name: string; password: string; payload: string } };
  'wifi.qrCancel': { params: { sessionId: string }; result: Empty };
  'wifi.iphoneSync': { params: DeviceParams; result: { enabled: boolean | null } };
  'wifi.setIphoneSync': { params: DeviceParams & { enabled: boolean }; result: { enabled: boolean | null } };
  'ringtone.create': {
    params: DeviceParams & { files: string[]; startSeconds: number; endSeconds: number; name?: string | null; setAsDefault: boolean };
    result: { devicePath: string; isDefault: boolean; bytes: number };
  };
  // Served by the mirror window's own bridge (web/mirror.html), not the main window's.
  'mirror.start': {
    params: DeviceParams & { maxSize?: number; bitRate?: number; maxFps?: number };
    result: { sessionId: string; deviceName: string; codec: string };
  };
  'mirror.input': { params: { sessionId: string } & MirrorInput; result: Empty };
  'mirror.stop': { params: { sessionId: string }; result: Empty };
  'mirror.screenshot': { params: DeviceParams; result: { path: string } };
  // Backup, contacts and messages (round 2)
  'backup.list': { params: { deviceId?: string }; result: BackupInfo[] };
  'backup.device': { params: DeviceParams; result: BackupDeviceInfo };
  'backup.start': { params: DeviceParams & { full?: boolean }; result: BackupJob };
  'backup.restore': {
    params: DeviceParams & { backupId: string; confirmName: string; password?: string; restoreSettings?: boolean };
    result: BackupJob;
  };
  'backup.setEncryption': { params: DeviceParams & { enable: boolean; password?: string; oldPassword?: string }; result: BackupJob };
  'backup.jobs': { params: Empty; result: BackupJob[] };
  'backup.cancel': { params: { jobId: string }; result: Empty };
  'backup.prunePlan': { params: DeviceParams; result: PrunePlan };
  'backup.deletePlan': { params: { backupIds: string[] }; result: PrunePlan };
  'backup.delete': { params: { backupIds: string[] }; result: BackupDeleteResult };
  'backup.unlock': { params: { backupId: string; password: string }; result: Empty };
  'backup.lock': { params: { backupId: string }; result: Empty };
  'backup.domains': { params: { backupId: string }; result: BackupDomain[] };
  'backup.files': { params: { backupId: string; domain: string; path: string }; result: BackupEntry[] };
  'backup.extract': { params: { backupId: string; items: BackupItemRef[]; destination: string }; result: BackupJob };
  'contacts.list': { params: { backupId: string }; result: Contact[] };
  'contacts.export': {
    params: { backupId: string; format: ContactsFormat; destination: string; contactIds?: string[] };
    result: BackupJob;
  };
  'messages.conversations': { params: { backupId: string }; result: Conversation[] };
  'messages.list': { params: { backupId: string; conversationId: string; offset: number; limit: number }; result: Page<Message> };
  'messages.export': {
    params: { backupId: string; conversationIds: string[]; format: MessagesFormat; destination: string };
    result: BackupJob;
  };
  // Photos into Photos with LocalSend (round 3)
  'localsend.discover': { params: DeviceParams & { wifi?: boolean; rescan?: boolean }; result: LocalSendDiscovery };
  'localsend.send': { params: DeviceParams & { target: LocalSendTarget; files: string[] }; result: JobStarted };
  'localsend.retry': { params: { jobId: string; pin?: string }; result: Empty };
  'localsend.job': { params: { jobId: string }; result: LocalSendJobStatus };
  // Photo backup (round 3)
  'photos.backupSpace': { params: { folder: string }; result: PhotoBackupSpace };
  // Deleting photos, iPhone included (round 4, photodelete)
  'photos.deleteSupport': { params: DeviceParams & { recheck?: boolean }; result: PhotoDeleteSupport };
  'photos.deleteItems': {
    params: DeviceParams & { items: PhotoDeleteItem[]; expectedBytes: number; copyFirstTo?: string | null };
    result: PhotoDeleteReport;
  };
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
  // Clean-up and battery history (round 2)
  'cleanup.status': true,
  'cleanup.analyze': true,
  'cleanup.cancel': true,
  'cleanup.deleteMedia': true,
  'cleanup.deleteFiles': true,
  'cleanup.uninstallApps': true,
  'cleanup.trimCaches': true,
  'battery.devices': true,
  'battery.history': true,
  'battery.record': true,
  'battery.export': true,
  // Toolbox (round 2)
  'toolbox.features': true,
  'log.start': true,
  'log.stop': true,
  'log.clear': true,
  'log.export': true,
  'crash.list': true,
  'crash.read': true,
  'crash.export': true,
  'crash.delete': true,
  'report.preview': true,
  'report.save': true,
  'heic.status': true,
  'heic.convert': true,
  'toolbox.cancelJob': true,
  'mirror.open': true,
  'wifi.status': true,
  'wifi.pair': true,
  'wifi.connect': true,
  'wifi.disconnect': true,
  'wifi.qrStart': true,
  'wifi.qrCancel': true,
  'wifi.iphoneSync': true,
  'wifi.setIphoneSync': true,
  'ringtone.create': true,
  'mirror.start': true,
  'mirror.input': true,
  'mirror.stop': true,
  'mirror.screenshot': true,
  // Backup, contacts and messages (round 2)
  'backup.list': true,
  'backup.device': true,
  'backup.start': true,
  'backup.restore': true,
  'backup.setEncryption': true,
  'backup.jobs': true,
  'backup.cancel': true,
  'backup.prunePlan': true,
  'backup.deletePlan': true,
  'backup.delete': true,
  'backup.unlock': true,
  'backup.lock': true,
  'backup.domains': true,
  'backup.files': true,
  'backup.extract': true,
  'contacts.list': true,
  'contacts.export': true,
  'messages.conversations': true,
  'messages.list': true,
  'messages.export': true,
  // Photos into Photos with LocalSend (round 3)
  'localsend.discover': true,
  'localsend.send': true,
  'localsend.retry': true,
  'localsend.job': true,
  // Photo backup (round 3)
  'photos.backupSpace': true,
  // Deleting photos, iPhone included (round 4, photodelete)
  'photos.deleteSupport': true,
  'photos.deleteItems': true,
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
  // Clean-up and battery history (round 2)
  'cleanup.updated': CleanupStatus;
  'battery.recorded': BatterySnapshot;
  // Toolbox (round 2)
  'log.lines': { sessionId: string; lines: LogLine[]; dropped: number };
  'log.ended': { sessionId: string; error: ToolboxError | null };
  'toolbox.job': ToolboxJob;
  'wifi.qr': WifiQrEvent;
  'mirror.video': MirrorVideoEvent;
  'mirror.ended': { sessionId: string; error: ToolboxError | null };
  // Backup, contacts and messages (round 2)
  'backup.updated': BackupJob;
  // Desktop (round 2)
  'notification.activated': NotificationActivation;
  'app.updateChanged': UpdateStatus;
  // Photos into Photos with LocalSend (round 3)
  'localsend.updated': LocalSendJobStatus;
}

export type EventName = keyof BridgeEvents;

const eventTable = {
  'device.added': true,
  'device.changed': true,
  'device.removed': true,
  'drivers.changed': true,
  'transfer.updated': true,
  'photos.indexProgress': true,
  // Clean-up and battery history (round 2)
  'cleanup.updated': true,
  'battery.recorded': true,
  // Toolbox (round 2)
  'log.lines': true,
  'log.ended': true,
  'toolbox.job': true,
  'wifi.qr': true,
  'mirror.video': true,
  'mirror.ended': true,
  // Backup, contacts and messages (round 2)
  'backup.updated': true,
  // Desktop (round 2)
  'notification.activated': true,
  'app.updateChanged': true,
  // Photos into Photos with LocalSend (round 3)
  'localsend.updated': true,
} as const satisfies Record<EventName, true>;

export const EVENT_NAMES = Object.keys(eventTable) as EventName[];
