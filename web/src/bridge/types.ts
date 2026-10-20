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
  | 'appExport';

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
