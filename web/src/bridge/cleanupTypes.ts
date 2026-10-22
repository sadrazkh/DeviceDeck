// Shapes of docs/bridge-contract.md, "Clean-up (round 2)" and "Battery history (round 2)",
// hand-written from src/DeviceDeck.Core/Cleanup/CleanupModels.cs and Battery/BatteryModels.cs.

import type { AppInfo, DevicePlatform, PhotoAsset } from './types';

export type CleanupSection = 'storage' | 'apps' | 'media' | 'caches' | 'appFiles' | 'duplicates';
export type CleanupState = 'idle' | 'running' | 'done' | 'failed' | 'cancelled';

export interface CleanupProgress {
  section: CleanupSection;
  /** Bytes hashed for duplicates; items otherwise. */
  done: number;
  total: number;
}

export interface CleanupNote {
  section: CleanupSection;
  code: string;
  message: string;
}

export interface CleanupApps {
  /** User apps, biggest (app + data) first. */
  largest: AppInfo[];
  count: number;
  totalBytes: number;
  canUninstall: boolean;
  /** Android: the APK can be saved to the PC first (not the app's data). */
  canCopyFirst: boolean;
}

export interface CleanupMedia {
  largestVideos: PhotoAsset[];
  largestPhotos: PhotoAsset[];
  videoCount: number;
  videoBytes: number;
  photoCount: number;
  photoBytes: number;
  cloudOnlyCount: number;
  /** Android's MediaStore, or an iPhone whose camera connection allows it (round 4). Otherwise: copy, then delete in Photos. */
  canDelete: boolean;
}

export interface DuplicateGroup {
  hash: string;
  size: number;
  /** Oldest first: the first is the one to keep, the rest are extra copies. */
  assets: PhotoAsset[];
  reclaimableBytes: number;
}

export interface CleanupDuplicates {
  groups: DuplicateGroup[];
  reclaimableBytes: number;
  hashedCount: number;
  hashedBytes: number;
  canDelete: boolean;
}

export interface AppFileHit {
  root: string;
  path: string;
  name: string;
  size: number;
  modified: string | null;
  appId: string | null;
  /** "Download", "Documents" when the file isn't in an app's folder. */
  folder: string | null;
}

export interface CleanupAppFiles {
  files: AppFileHit[];
  totalBytes: number;
  minBytes: number;
  appNames: Record<string, string>;
  canDelete: boolean;
}

export interface CleanupCacheApp {
  appId: string;
  name: string | null;
  bytes: number;
}

export interface CleanupCaches {
  /** Null when the phone hasn't counted its caches yet. */
  totalBytes: number | null;
  apps: CleanupCacheApp[];
  trimmedBytes: number | null;
  trimmedAt: string | null;
}

export interface CleanupReport {
  deviceId: string;
  platform: DevicePlatform;
  startedAt: string;
  totalBytes: number | null;
  freeBytes: number | null;
  apps: CleanupApps | null;
  media: CleanupMedia | null;
  caches: CleanupCaches | null;
  appFiles: CleanupAppFiles | null;
  duplicates: CleanupDuplicates | null;
  notes: CleanupNote[];
}

export interface CleanupStatus {
  deviceId: string;
  state: CleanupState;
  progress: CleanupProgress | null;
  /** Partial while running. */
  report: CleanupReport | null;
  error: { code: string; message: string } | null;
  finishedAt: string | null;
}

/** A photo, video or app the user confirmed, with the size the page showed. */
export interface CleanupItem {
  id: string;
  size: number;
}

export interface CleanupFileItem {
  root: string;
  path: string;
  size: number;
}

export interface CleanupResult {
  deleted: number;
  /** Left alone because they changed or disappeared since the analysis. */
  skipped: number;
  freedBytes: number;
  copyJobIds: string[];
  /** The copy to the PC didn't finish, so nothing was deleted. */
  copyIncomplete: boolean;
}

export interface BatterySnapshot {
  id: number;
  deviceId: string;
  deviceName: string;
  platform: DevicePlatform;
  model: string;
  osVersion: string;
  at: string;
  levelPercent: number | null;
  isCharging: boolean;
  healthPercent: number | null;
  /** iPhone: full-charge over design capacity, which differs from iOS Settings. */
  healthEstimated: boolean;
  cycleCount: number | null;
  designCapacityMah: number | null;
  fullChargeCapacityMah: number | null;
  temperatureCelsius: number | null;
}

export interface BatteryDevice {
  deviceId: string;
  name: string;
  platform: DevicePlatform;
  model: string;
  count: number;
  firstAt: string;
  lastAt: string;
  latest: BatterySnapshot;
}
