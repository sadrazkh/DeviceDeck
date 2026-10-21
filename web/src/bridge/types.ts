// Shapes of docs/bridge-contract.md (v0), hand-written from the C# records in
// src/DeviceDeck.Core/Abstractions. JSON is camelCase, enums are camelCase strings,
// dates are ISO 8601 strings and sizes are bytes.

export type DevicePlatform = 'ios' | 'android';
export type ConnectionKind = 'usb' | 'wifi';
/** full = the platform's own protocol (usbmux/lockdown, adb); limited = MTP/PTP. */
export type DeviceMode = 'full' | 'limited';
export type DeviceState =
  | 'connecting'
  | 'awaitingTrust'
  | 'unauthorized'
  | 'locked'
  | 'ready'
  | 'disconnected';

export const DEVICE_CAPABILITIES = [
  'info',
  'storage',
  'battery',
  'photosRead',
  'photosDelete',
  'photosImport',
  'files',
  'appDocuments',
  'appsList',
  'appsInstall',
  'appsUninstall',
  'appsExport',
  'backup',
  'restore',
  'contacts',
  'messages',
  'screenshot',
  'screenMirror',
  'syslog',
  'crashReports',
  'reboot',
  'shutdown',
  'rebootToRecovery',
  'rebootToBootloader',
  // Round 2
  'rename',
] as const;
export type DeviceCapability = (typeof DEVICE_CAPABILITIES)[number];

export interface DeviceSummary {
  /** Stable across reconnects: the iPhone's UDID or the Android serial. */
  id: string;
  name: string;
  platform: DevicePlatform;
  model: string;
  marketingName: string | null;
  osVersion: string;
  connection: ConnectionKind;
  state: DeviceState;
  mode: DeviceMode;
  capabilities: DeviceCapability[];
  colorHex: string | null;
  /** One plain sentence when the state needs the user ("Don't Trust was tapped · unplug…"); absent from older hosts. */
  stateDetail?: string | null;
}

export type StorageCategoryKey = 'photos' | 'videos' | 'apps' | 'media' | 'system' | 'other';
export const STORAGE_CATEGORY_ORDER: readonly StorageCategoryKey[] = [
  'photos',
  'videos',
  'apps',
  'media',
  'system',
  'other',
];

export interface StorageCategory {
  key: StorageCategoryKey;
  bytes: number;
}

export interface StorageVolume {
  key: string;
  label: string;
  totalBytes: number;
  freeBytes: number;
  categories: StorageCategory[];
}

/** Internal storage first; an Android SD card is a second volume. */
export interface StorageInfo {
  volumes: StorageVolume[];
}

export interface BatteryInfo {
  levelPercent: number | null;
  isCharging: boolean;
  healthPercent: number | null;
  cycleCount: number | null;
  temperatureCelsius: number | null;
  designCapacityMah: number | null;
  fullChargeCapacityMah: number | null;
}

export interface DetailRow {
  key: string;
  label: string;
  value: string;
  /** IMEI, serial and the like: the UI masks them until Reveal. */
  sensitive: boolean;
}

export interface DeviceDetails {
  rows: DetailRow[];
}

export interface FileEntry {
  /** Absolute path inside the root it was listed from, '/' separated. */
  path: string;
  name: string;
  isDirectory: boolean;
  size: number;
  modified: string | null;
  readOnly: boolean;
}

export type FileRootKind = 'media' | 'appDocuments' | 'internalStorage' | 'sdCard';

export interface FileRoot {
  /** iPhone: "media" plus one "app:<bundleId>" per file-sharing app. Android: "internal", "sd:<id>". */
  key: string;
  label: string;
  kind: FileRootKind;
  appId: string | null;
}

export type AppSource = 'store' | 'sideloaded' | 'system';

export interface AppInfo {
  /** Bundle id (iOS) or package name (Android). */
  id: string;
  name: string;
  version: string;
  appBytes: number;
  dataBytes: number;
  source: AppSource;
  /** iOS: UIFileSharingEnabled, so house_arrest can vend its Documents. */
  fileSharing: boolean;
  installedAt: string | null;
}

export type PhotoKind = 'photo' | 'video' | 'livePhoto' | 'screenshot';
export type AlbumKind = 'all' | 'camera' | 'screenshots' | 'videos' | 'live' | 'favorites' | 'user' | 'folder';

export interface PhotoAlbum {
  id: string;
  name: string;
  kind: AlbumKind;
  count: number;
}

export interface PhotoAsset {
  id: string;
  fileName: string;
  kind: PhotoKind;
  takenAt: string | null;
  size: number;
  width: number | null;
  height: number | null;
  durationSeconds: number | null;
  isFavorite: boolean;
  /** Optimised storage: the original is in iCloud, not on the phone; not exportable over USB. */
  inCloudOnly: boolean;
  /** Id of the video half of a Live Photo, when there is one. */
  livePairId: string | null;
}

export interface Page<T> {
  items: T[];
  total: number;
}

export type RebootTarget = 'system' | 'recovery' | 'bootloader';

export type DriverState = 'ready' | 'missing' | 'stopped' | 'error';

export interface DriverStatus {
  /** "ios", "android" or "mtp". */
  providerId: string;
  state: DriverState;
  version: string | null;
  /** One plain sentence for the Drivers card when the state is not ready. */
  detail: string | null;
}

// ---------- Bridge-only shapes ----------

export type TransferKind =
  | 'photosExport'
  | 'photosImport'
  | 'copyToPc'
  | 'copyToDevice'
  | 'appInstall'
  | 'appExport'
  /** Photos or files dragged into File Explorer (virtual files the queue copies while Explorer reads them). */
  | 'dragOut'
  // Photos (round 2): backup to the PC, photos into an iPhone app's Documents.
  | 'photosBackup'
  | 'photosToApp'
  /** Round 3: PC photos to the LocalSend app on the phone, which saves them into Photos. */
  | 'localSend'
  /** Round 4: WhatsApp's media and encrypted backups copied off an Android phone. */
  | 'whatsAppMedia';

export type TransferState =
  | 'queued'
  | 'running'
  | 'paused'
  | 'waitingForDevice'
  | 'needsDecision'
  | 'done'
  | 'failed'
  | 'cancelled';

export interface TransferConflict {
  itemName: string;
  existingSize: number;
  newSize: number;
}

export interface TransferJob {
  id: string;
  deviceId: string;
  kind: TransferKind;
  /** e.g. "Exporting 1,284 photos to D:\\Photos\\2026" */
  title: string;
  state: TransferState;
  totalItems: number;
  doneItems: number;
  failedItems: number;
  totalBytes: number;
  doneBytes: number;
  bytesPerSecond: number | null;
  etaSeconds: number | null;
  /** Set when state = needsDecision. */
  conflict: TransferConflict | null;
  error: { code: string; message: string } | null;
  createdAt: string;
  finishedAt: string | null;
}

export type ConflictChoice = 'replace' | 'keepBoth' | 'skip';
export type FolderPattern = 'yyyy/MM' | '';

export interface Settings {
  language: 'en' | 'fa';
  theme: 'system' | 'light' | 'dark';
  density: 'comfortable' | 'compact';
  photoExportFolder: string;
  photoFolderPattern: FolderPattern;
  convertHeic: boolean;
  fileCopyFolder: string;
  conflictRule: 'ask' | ConflictChoice;
  // Desktop (round 2)
  /** Closing the window keeps DeviceDeck in the notification area. */
  closeToTray: boolean;
  /** A Windows notification when a phone connects while the window isn't in front. */
  notifyOnConnect: boolean;
  /** Look for updates on GitHub Releases in the background (installed copies only). */
  autoUpdate: boolean;
  // Photos backup defaults, for a phone without options of its own.
  /** Each phone backs up into a sub-folder named after it. */
  photoBackupFolder: string;
  /** "" or yyyy, MM and dd joined by / - _ . or a space ("yyyy/MM"). */
  photoBackupPattern: string;
  photoBackupConvertHeic: boolean;
  /** Where backups go (round 2, backup). */
  backupFolder: string;
  /** Backups of each phone to keep; older ones are offered for deletion. */
  backupKeep: number;
}

export interface HelloResult {
  version: string;
  isWebView: boolean;
}

export interface JobStarted {
  jobId: string;
}

export interface DeletedCount {
  deleted: number;
}

export type Empty = Record<string, never>;

/** DeviceErrorCodes plus "internal" (docs/bridge-contract.md, Errors). */
export const DEVICE_ERROR_CODES = [
  'device_not_found',
  'device_disconnected',
  'device_locked',
  'trust_required',
  'unauthorized',
  'not_supported',
  'path_not_found',
  'permission_denied',
  'already_exists',
  'install_failed',
  'driver_missing',
  'protocol_error',
  'internal',
] as const;
export type DeviceErrorCode = (typeof DEVICE_ERROR_CODES)[number];

// ---------- Desktop (round 2): this PC, drag out, notifications, updates ----------

export type PcRootKind =
  | 'home'
  | 'desktop'
  | 'downloads'
  | 'documents'
  | 'pictures'
  | 'videos'
  | 'music'
  | 'drive'
  | 'removableDrive'
  | 'networkDrive'
  | 'opticalDrive';

export interface PcRoot {
  path: string;
  /** A drive's volume label (may be empty) or the folder's own name; the page localises by kind. */
  label: string;
  kind: PcRootKind;
  totalBytes: number | null;
  freeBytes: number | null;
}

export interface PcEntry {
  /** Full Windows path. */
  path: string;
  name: string;
  isDirectory: boolean;
  size: number;
  modified: string | null;
  readOnly: boolean;
  hidden: boolean;
}

/** One dragged photo or phone file as the page knows it. */
export interface DragOutEntry {
  /** Asset id, or the file's path in its root. */
  id: string;
  name: string;
  size: number;
  isDirectory: boolean;
  modified: string | null;
}

export type DragOutParams =
  | { source: 'photos'; deviceId: string; items: DragOutEntry[] }
  | { source: 'files'; deviceId: string; root: string; items: DragOutEntry[] }
  /** Bare paths in the root; the host looks each one up on the phone. */
  | { source: 'files'; deviceId: string; root: string; paths: string[] }
  | { source: 'pc'; paths: string[] };

export interface DragOutResult {
  /** 'copy' when another app (File Explorer) took the drop. */
  effect: 'copy' | 'none';
  /** Set when the drop landed back on DeviceDeck's window, in CSS pixels: the page handles it. */
  droppedInside: { x: number; y: number } | null;
}

export interface NotificationButton {
  label: string;
  command: string;
}

export interface NotificationShowParams {
  title: string;
  body?: string;
  /** A later notification with the same tag replaces this one. */
  tag?: string;
  deviceId?: string;
  /** What a click on the notification means; default 'open'. */
  command?: string;
  buttons?: NotificationButton[];
  args?: Record<string, string>;
}

export interface NotificationActivation {
  command: string;
  deviceId: string | null;
  tag: string | null;
  args: Record<string, string>;
}

/**
 * ready: downloaded, a restart installs it (a Setup copy). available: a newer version is on GitHub that this copy can't
 * install by itself (the portable zip): Download opens its release page. Round 4 replaced 'notInstalled' with install: 'portable'.
 */
export type UpdateState = 'idle' | 'checking' | 'upToDate' | 'downloading' | 'ready' | 'available' | 'error';

export interface UpdateStatus {
  state: UpdateState;
  currentVersion: string;
  newVersion: string | null;
  percent: number | null;
  /** One plain English sentence for the log and a tooltip; the page shows its own words. */
  error: string | null;
  /** When a check last finished, also in an earlier run. */
  lastChecked: string | null;
  // Updates you can see (round 4)
  /** Installed by the Setup (updates itself) or the portable zip (only checks GitHub). */
  install: 'setup' | 'portable';
  channel: 'stable' | 'prerelease';
  architecture: 'x64' | 'arm64';
  /** What the check at lastChecked found. */
  lastResult: 'upToDate' | 'newVersion' | 'error' | null;
  /** With an error: GitHub asked to wait until then (its limit for checks from this network). */
  retryAt: string | null;
  /** The first run of a newer version: show "What's new in v{whatsNew}" once, then call app.whatsNewShown. */
  whatsNew: string | null;
}

// ---------- Photos backup (docs/bridge-contract.md, "Photos backup and adding photos") ----------

export interface PhotoBackupCount {
  deviceId: string;
  /** Photos and videos on the phone this PC never backed up (a Live Photo counts once). */
  newCount: number;
  newPhotos: number;
  newVideos: number;
  /** Sizes the phone already reported. */
  newBytes: number;
  /** New but only in iCloud: can't be copied over USB, not in newCount. */
  cloudOnly: number;
  /** New, in the iPhone's Hidden album, left out by the options. */
  hiddenLeftOut: number;
  /** New, but taken before the options' date range: left out, in no other count. */
  outOfRange: number;
  /** Edited versions to copy (iPhone, with saveEdited): new photos' edits and new edits of photos already copied. */
  newEdits: number;
}

/** Which photos a backup looks at, by the date taken. Undated photos are always included. */
export interface PhotoBackupRange {
  kind: 'all' | 'lastDays' | 'since';
  /** For lastDays (1–36500). */
  days: number | null;
  /** For since: "yyyy-MM-dd", from midnight on the PC. */
  since: string | null;
}

export interface PhotoBackupOptions {
  destination: string;
  folderPattern: string;
  convertHeic: boolean;
  includeHidden: boolean;
  /** The phone's own only (Settings has none): everything by default. */
  range: PhotoBackupRange;
  /** iPhone: also "IMG_1234 (edited).jpg" next to a photo edited in Photos, kept up to date. Off by default. */
  saveEdited: boolean;
}

export type PhotoBackupRunState = 'running' | 'done' | 'failed' | 'cancelled';

export interface PhotoBackupRun {
  jobId: string;
  destination: string;
  state: PhotoBackupRunState;
  planned: number;
  copied: number;
  /** Already in the destination (same size and SHA-256), now remembered. */
  duplicates: number;
  failed: number;
  bytes: number;
  /** HEIC kept as HEIC because this PC couldn't convert. */
  keptHeic: number;
  startedAt: string;
  finishedAt: string | null;
  trigger: PhotoBackupTrigger;
  /** Edited versions written (new or updated). */
  edited: number;
  /** Photos the iPhone says were edited but has no edited version of where Photos keeps them. */
  editsMissing: number;
}

export type HeicConversion = 'available' | 'missingCodec' | 'unknown';

export interface PhotoBackupStatus {
  deviceId: string;
  count: PhotoBackupCount;
  options: PhotoBackupOptions;
  /** The phone has options of its own (false = the Settings defaults). */
  customized: boolean;
  backedUp: number;
  lastRun: PhotoBackupRun | null;
  activeJobId: string | null;
  heicConversion: HeicConversion;
  /** Room for the new photos on the destination's drive; photos.backup is refused when it isn't enough. */
  space: PhotoBackupSpace;
  /** "When this phone connects" (round 3); the phone's own, kept by useDefaults. */
  onConnect: PhotoBackupOnConnect;
}

/** Nothing; a Windows notification of new photos with "Back up now" (default); or a backup that starts by itself. */
export type PhotoBackupOnConnect = 'nothing' | 'notify' | 'backUp';

/** What started a run; the last two end with a notification. */
export type PhotoBackupTrigger = 'manual' | 'notification' | 'automatic';

/** Room on a backup folder's drive (round 3). */
export interface PhotoBackupSpace {
  /** "E:\" or the share. */
  root: string;
  /** null when the drive isn't there or can't say. */
  freeBytes: number | null;
  totalBytes: number | null;
  /** The new photos (estimated where the phone hasn't said; HEIC counted bigger as JPG) plus a margin; 0 when nothing is new. */
  neededBytes: number;
  enough: boolean;
  /** Under 20 GB, or under a tenth of the drive, left. */
  nearlyFull: boolean;
  driveMissing: boolean;
}

// Backup, contacts and messages (round 2).
export * from './backupTypes';
// WhatsApp (round 4).
export * from './whatsApp';
