// The Drivers card (v0.6, drivers): docs/bridge-contract.md, "Drivers card". The extra DriverStatus fields live on
// DriverStatus itself (types.ts); these are the card's own shapes.

/** Why a driver row is what it is; the page words it. */
export type DriverReason =
  | 'amdsMissing'
  | 'amdsStopped'
  | 'amdsError'
  | 'adbMissing'
  | 'adbStarting'
  | 'adbNotStarted'
  | 'adbFailed'
  | 'startFailed'
  | 'checkFailed';

/** Where adb.exe came from. */
export type DriverSource = 'bundled' | 'downloaded' | 'environment' | 'configured';

export type DriverPhoneProblem = 'usbDebuggingOff' | 'driverMissing' | 'unauthorized' | 'offline' | 'notSeenByAdb';

/** A phone Windows (or adb) sees that can't be used yet. */
export interface DriverPhoneHint {
  name: string;
  problem: DriverPhoneProblem;
  /** "samsung", "google"… from the USB vendor id. */
  maker?: string | null;
}

export type DriverDownloadState = 'idle' | 'downloading' | 'verifying' | 'installing' | 'done' | 'failed' | 'cancelled';

export type DriverDownloadError = 'network' | 'corrupt' | 'invalid' | 'unverified' | 'disk';

export interface DriverDownloadStatus {
  providerId: string;
  state: DriverDownloadState;
  receivedBytes: number;
  totalBytes: number | null;
  /** One English sentence while failed (the page words errorCode itself). */
  error: string | null;
  errorCode: DriverDownloadError | null;
  /** Once done: "37.0.1". */
  version: string | null;
}

/** The only links drivers.openLink opens. */
export type DriverLink =
  | 'appleDevicesStore'
  | 'appleDevicesWeb'
  | 'itunes'
  | 'samsungUsbDriver'
  | 'googleUsbDriver'
  | 'oemUsbDrivers'
  | 'platformToolsLicense';
