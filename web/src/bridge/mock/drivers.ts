// The Drivers card in the mock (v0.6, drivers): every state the card shows, chosen with ?drivers=…, plus Start,
// the Android tools download (progress, cancel, failure) and the links it opens. Nothing here goes online.

import type { DriverDownloadStatus, DriverLink } from '../drivers';
import type { BridgeEvents, EventName, MethodName, MethodParams, MethodResult } from '../methods';
import type { DriverStatus } from '../types';

/**
 * ready (both green) · iosMissing (no Apple Mobile Device Support) · iosStopped (installed, service stopped; Start shows
 * Windows' prompt) · adbMissing (no adb.exe: Download) · adbDownloading (a download at 42 %, stays until cancelled) ·
 * adbDownloadFails (Download ends with "not signed by Google") · adbIdle (not started yet, grey) · adbStarting (grey) ·
 * adbFailed (red, Restart) · androidNoDriver (a Samsung whose USB driver is missing) · androidUsbOff (a Pixel with USB
 * debugging off) · androidUnauthorized (waiting for Allow).
 */
export type MockDriversScenario =
  | 'ready'
  | 'iosMissing'
  | 'iosStopped'
  | 'adbMissing'
  | 'adbDownloading'
  | 'adbDownloadFails'
  | 'adbIdle'
  | 'adbStarting'
  | 'adbFailed'
  | 'androidNoDriver'
  | 'androidUsbOff'
  | 'androidUnauthorized';

export const MOCK_DRIVERS_SCENARIOS: readonly MockDriversScenario[] = [
  'ready',
  'iosMissing',
  'iosStopped',
  'adbMissing',
  'adbDownloading',
  'adbDownloadFails',
  'adbIdle',
  'adbStarting',
  'adbFailed',
  'androidNoDriver',
  'androidUsbOff',
  'androidUnauthorized',
];

type Handler<M extends MethodName> = (params: MethodParams<M>) => MethodResult<M>;

export interface MockDriversHost {
  emit<E extends EventName>(event: E, params: BridgeEvents[E]): void;
  after(ms: number, run: () => void): void;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

/** Google's platform-tools-latest-windows.zip as measured on 2026-10-22 (Content-Length). */
export const PLATFORM_TOOLS_BYTES = 8_044_989;

const IOS_READY: DriverStatus = { providerId: 'ios', state: 'ready', version: '18.4.0.4', detail: null };
const ADB_READY: DriverStatus = { providerId: 'android', state: 'ready', version: '37.0.1', detail: null, source: 'bundled' };

function iosRow(scenario: MockDriversScenario): DriverStatus {
  switch (scenario) {
    case 'iosMissing':
      return {
        providerId: 'ios',
        state: 'missing',
        version: null,
        detail: 'Apple Mobile Device Support is not installed. Install Apple Devices from the Microsoft Store (or iTunes).',
        reason: 'amdsMissing',
      };
    case 'iosStopped':
      return {
        providerId: 'ios',
        state: 'stopped',
        version: '18.4.0.4',
        detail: 'Apple Mobile Device Service is installed but not answering. Start it in Services, or reinstall Apple Devices.',
        reason: 'amdsStopped',
      };
    default:
      return IOS_READY;
  }
}

function androidRow(scenario: MockDriversScenario): DriverStatus {
  switch (scenario) {
    case 'adbMissing':
    case 'adbDownloading':
    case 'adbDownloadFails':
      return {
        providerId: 'android',
        state: 'missing',
        version: null,
        detail: 'Android tools (Google’s platform-tools) aren’t on this PC; download them from the Drivers card.',
        reason: 'adbMissing',
      };
    case 'adbIdle':
      return { providerId: 'android', state: 'idle', version: '37.0.1', detail: 'DeviceDeck starts its adb server by itself.', reason: 'adbNotStarted', source: 'bundled' };
    case 'adbStarting':
      return { providerId: 'android', state: 'starting', version: '37.0.1', detail: 'Starting the adb server on port 5137.', reason: 'adbStarting', source: 'bundled' };
    case 'adbFailed':
      return {
        providerId: 'android',
        state: 'error',
        version: '37.0.1',
        detail:
          'The adb server on port 5137 won’t start: could not install *smartsocket* listener: cannot bind to 127.0.0.1:5137: Only one usage of each socket address is normally permitted. (10048)',
        reason: 'adbFailed',
        source: 'bundled',
      };
    case 'androidNoDriver':
      return { ...ADB_READY, phones: [{ name: 'Galaxy S23', problem: 'driverMissing', maker: 'samsung' }] };
    case 'androidUsbOff':
      return { ...ADB_READY, phones: [{ name: 'Pixel 8', problem: 'usbDebuggingOff', maker: 'google' }] };
    case 'androidUnauthorized':
      return { ...ADB_READY, phones: [{ name: 'Redmi Note 11', problem: 'unauthorized', maker: null }] };
    default:
      return ADB_READY;
  }
}

export class MockDrivers {
  private rows: DriverStatus[] = [IOS_READY, ADB_READY];
  private download: DriverDownloadStatus = { providerId: 'android', state: 'idle', receivedBytes: 0, totalBytes: null, error: null, errorCode: null, version: null };
  private failDownload = false;
  /** Each download run, so a cancelled run's later steps do nothing. */
  private run = 0;
  /** What drivers.openLink was asked to open, for tests and the sim strip. */
  readonly opened: DriverLink[] = [];
  /** ?uac=decline: Windows' prompt for Apple Mobile Device Service is declined. */
  uacDeclined = false;

  constructor(private readonly host: MockDriversHost) {}

  /** Sets the scenario (the backend's constructor, from ?drivers=…). */
  configure(scenario: MockDriversScenario, uacDeclined = false): void {
    this.uacDeclined = uacDeclined;
    this.rows = [iosRow(scenario), androidRow(scenario)];
    this.failDownload = scenario === 'adbDownloadFails';
    // ?drivers=adbDownloading: a download at 42 % with nothing scheduled, so it stays there until cancelled.
    if (scenario === 'adbDownloading') {
      this.download = { ...this.download, state: 'downloading', receivedBytes: Math.round(PLATFORM_TOOLS_BYTES * 0.42), totalBytes: PLATFORM_TOOLS_BYTES };
    }
  }

  list(): DriverStatus[] {
    return this.rows.map((row) => structuredClone(row));
  }

  /** Replaces the rows (tests and the old setDrivers) and tells the page. */
  set(rows: DriverStatus[]): void {
    this.rows = rows.map((row) => structuredClone(row));
    this.publish();
  }

  /** Tests: the host's answer changes without a drivers.changed (one the page missed): only asking again shows it. */
  setQuietly(rows: DriverStatus[]): void {
    this.rows = rows.map((row) => structuredClone(row));
  }

  private publish(): void {
    this.host.emit('drivers.changed', this.list());
  }

  private setRow(row: DriverStatus): void {
    this.rows = this.rows.map((r) => (r.providerId === row.providerId ? row : r));
    this.publish();
  }

  private row(providerId: string): DriverStatus {
    const row = this.rows.find((r) => r.providerId === providerId);
    if (!row) throw this.host.badParams(`Invalid params: There is no driver called "${providerId}".`);
    return row;
  }

  private setDownload(next: DriverDownloadStatus): void {
    this.download = next;
    this.host.emit('drivers.download', { ...next });
  }

  private startDownload(): DriverDownloadStatus {
    if (this.download.state === 'downloading' || this.download.state === 'verifying' || this.download.state === 'installing') return { ...this.download };
    const run = ++this.run;
    this.setDownload({ providerId: 'android', state: 'downloading', receivedBytes: 0, totalBytes: PLATFORM_TOOLS_BYTES, error: null, errorCode: null, version: null });
    const steps = 8;
    for (let i = 1; i <= steps; i++) {
      this.host.after(i * 250, () => {
        if (run !== this.run) return;
        this.setDownload({ ...this.download, receivedBytes: Math.round((PLATFORM_TOOLS_BYTES * i) / steps) });
      });
    }
    this.host.after(steps * 250 + 250, () => {
      if (run !== this.run) return;
      this.setDownload({ ...this.download, state: 'verifying' });
    });
    this.host.after(steps * 250 + 750, () => {
      if (run !== this.run) return;
      if (this.failDownload) {
        this.setDownload({
          ...this.download,
          state: 'failed',
          error: 'adb.exe isn’t signed by Google.',
          errorCode: 'unverified',
        });
        return;
      }
      this.setDownload({ ...this.download, state: 'installing' });
    });
    this.host.after(steps * 250 + 1250, () => {
      if (run !== this.run || this.failDownload) return;
      this.setDownload({ ...this.download, state: 'done', version: '37.0.1' });
      this.setRow({ providerId: 'android', state: 'starting', version: '37.0.1', detail: 'Starting the adb server on port 5137.', reason: 'adbStarting', source: 'downloaded' });
      this.host.after(750, () => {
        if (run !== this.run) return;
        this.setRow({ ...ADB_READY, source: 'downloaded' });
      });
    });
    return { ...this.download };
  }

  readonly handlers: {
    [M in 'drivers.start' | 'drivers.download' | 'drivers.downloadStatus' | 'drivers.cancelDownload' | 'drivers.openLink']: Handler<M>;
  } = {
    'drivers.start': ({ providerId }) => {
      const row = this.row(providerId);
      if (row.state === 'ready') return this.list();
      if (providerId === 'ios') {
        if (row.state === 'missing') throw this.host.fault('driver_missing', 'Apple Mobile Device Support isn’t installed. Get Apple Devices from the Microsoft Store first.');
        if (this.uacDeclined) throw this.host.fault('permission_denied', 'Windows asked for permission to start Apple Mobile Device Service, and it wasn’t given.');
        // The prompt was accepted: the service answers a moment later; the card hears it through drivers.changed.
        this.host.after(1000, () => this.setRow(IOS_READY));
        return this.list();
      }
      if (row.state === 'missing') throw this.host.fault('driver_missing', 'Android tools aren’t on this PC; download them first.');
      this.setRow({ providerId: 'android', state: 'starting', version: row.version, detail: 'Starting the adb server on port 5137.', reason: 'adbStarting', source: row.source });
      this.host.after(750, () => this.setRow({ ...ADB_READY, source: row.source ?? 'bundled' }));
      return this.list();
    },
    'drivers.download': ({ providerId }) => {
      if (providerId !== 'android') throw this.host.fault('not_supported', 'DeviceDeck can’t download this driver.');
      return this.startDownload();
    },
    'drivers.downloadStatus': ({ providerId }) => {
      if (providerId !== 'android') throw this.host.fault('not_supported', 'DeviceDeck can’t download this driver.');
      return { ...this.download };
    },
    'drivers.cancelDownload': ({ providerId }) => {
      if (providerId !== 'android') throw this.host.fault('not_supported', 'DeviceDeck can’t download this driver.');
      if (this.download.state === 'downloading' || this.download.state === 'verifying' || this.download.state === 'installing') {
        this.run++;
            this.setDownload({ ...this.download, state: 'cancelled', receivedBytes: 0 });
      }
      return {};
    },
    'drivers.openLink': ({ link }) => {
      const known: readonly DriverLink[] = ['appleDevicesStore', 'appleDevicesWeb', 'itunes', 'samsungUsbDriver', 'googleUsbDriver', 'oemUsbDrivers', 'platformToolsLicense'];
      if (!known.includes(link)) throw this.host.badParams(`Invalid params: There is no link called "${link}".`);
      this.opened.push(link);
      return {};
    },
  };

  /** Simulation: Apple Devices finished installing (the card turns green by itself). */
  installAppleDevices(): void {
    this.setRow(IOS_READY);
  }
}
