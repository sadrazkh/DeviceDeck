import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { isBridgeError } from '../../src/bridge/errors';
import type { DriverDownloadStatus, DriverStatus } from '../../src/bridge';
import { MockBackend, type MockOptions } from '../../src/bridge/mock/backend';
import { MOCK_DRIVERS_SCENARIOS, PLATFORM_TOOLS_BYTES } from '../../src/bridge/mock/drivers';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { mockOptionsFromUrl } from '../../src/bridge';
import { en } from '../../src/i18n/en';
import { orderDrivers, phoneHints, rowModel } from '../../src/screens/drivers/driverModel';

// The Drivers card (v0.6): what each row shows for every state the host can send, and the mock behind ?drivers=….

const ios = (state: DriverStatus['state'], extra: Partial<DriverStatus> = {}): DriverStatus => ({ providerId: 'ios', state, version: '18.4.0.4', detail: null, ...extra });
const adb = (state: DriverStatus['state'], extra: Partial<DriverStatus> = {}): DriverStatus => ({ providerId: 'android', state, version: '37.0.1', detail: null, ...extra });
const dl = (state: DriverDownloadStatus['state'], extra: Partial<DriverDownloadStatus> = {}): DriverDownloadStatus => ({
  providerId: 'android',
  state,
  receivedBytes: 0,
  totalBytes: PLATFORM_TOOLS_BYTES,
  error: null,
  errorCode: null,
  version: null,
  ...extra,
});

describe('driver rows', () => {
  it('is green only when ready, grey while not started or starting, red only for real failures', () => {
    expect(rowModel(ios('ready'))).toMatchObject({ chip: 'ready', line: 'drivers.ios.ready', action: null });
    expect(rowModel(adb('ready'))).toMatchObject({ chip: 'ready', line: 'drivers.android.ready', action: null });
    // v0.5.3 showed a red "Stopped" here while adb was starting.
    expect(rowModel(adb('starting', { reason: 'adbStarting' }))).toMatchObject({ chip: 'starting', action: null });
    expect(rowModel(adb('idle', { reason: 'adbNotStarted' }))).toMatchObject({ chip: 'notStarted', line: 'drivers.android.idle', action: null });
    expect(rowModel(adb('error', { reason: 'adbFailed', detail: 'cannot bind' }))).toMatchObject({
      chip: 'problem',
      line: 'drivers.android.failed',
      detail: 'cannot bind',
      action: 'restartAdb',
    });
    expect(rowModel(adb('stopped'))).toMatchObject({ chip: 'action', action: 'startAdb' });
  });

  it('offers the one thing to do when a driver is missing or stopped', () => {
    expect(rowModel(ios('missing', { reason: 'amdsMissing' }))).toMatchObject({ chip: 'action', line: 'drivers.ios.missing', action: 'getAppleDevices' });
    expect(rowModel(ios('stopped', { reason: 'amdsStopped' }))).toMatchObject({ chip: 'action', line: 'drivers.ios.stopped', action: 'startAmds' });
    expect(rowModel(ios('error', { reason: 'amdsError', detail: 'odd answer' }))).toMatchObject({ chip: 'problem', detail: 'odd answer', action: null });
    expect(rowModel(adb('missing', { reason: 'adbMissing' }))).toMatchObject({ chip: 'action', line: 'drivers.android.missing', action: 'downloadAdb' });
  });

  it('shows a running download instead of the missing row, and a failed one with its reason and Try again', () => {
    const missing = adb('missing', { reason: 'adbMissing', version: null });
    expect(rowModel(missing, dl('downloading', { receivedBytes: 100 }))).toMatchObject({ chip: 'downloading', line: 'drivers.download.label', action: null });
    expect(rowModel(missing, dl('verifying'))).toMatchObject({ chip: 'downloading', line: 'drivers.download.verifying' });
    expect(rowModel(missing, dl('installing'))).toMatchObject({ chip: 'downloading', line: 'drivers.download.installing' });
    expect(rowModel(missing, dl('failed', { errorCode: 'unverified', error: 'adb.exe isn’t signed by Google.' }))).toMatchObject({
      chip: 'problem',
      line: 'drivers.download.failed.unverified',
      action: 'retryDownload',
    });
    expect(rowModel(missing, dl('cancelled'))).toMatchObject({ chip: 'action', action: 'downloadAdb' });
    // Once installed the host's row decides (starting, then ready).
    expect(rowModel(adb('ready'), dl('done'))).toMatchObject({ chip: 'ready' });
  });

  it('words every row and download failure in the dictionary', () => {
    const lines = [
      ...(['ready', 'missing', 'stopped', 'error', 'starting', 'idle'] as const).flatMap((state) => [rowModel(ios(state)).line, rowModel(adb(state)).line]),
      ...(['network', 'corrupt', 'invalid', 'unverified', 'disk'] as const).map((errorCode) => rowModel(adb('missing'), dl('failed', { errorCode })).line),
    ];
    for (const line of lines) expect(en[line], line).toBeTruthy();
  });

  it('lists the iPhone first, Android second and leaves out the old mtp row', () => {
    const order = orderDrivers([adb('ready'), { providerId: 'mtp', state: 'ready', version: null, detail: null }, ios('ready')]).map((d) => d.providerId);
    expect(order).toEqual(['ios', 'android']);
  });

  it('helps with each phone once', () => {
    const hint = { name: 'Galaxy S23', problem: 'driverMissing' as const, maker: 'samsung' };
    expect(phoneHints(adb('ready', { phones: [hint, hint, { ...hint, problem: 'offline' }] })).map((h) => h.problem)).toEqual(['driverMissing', 'offline']);
    expect(phoneHints(adb('ready'))).toEqual([]);
  });
});

let handle: MockHandle | null = null;

function setup(options: MockOptions = {}) {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, ...options });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const changed: DriverStatus[][] = [];
  const downloads: DriverDownloadStatus[] = [];
  client.on('drivers.changed', (list) => changed.push(list));
  client.on('drivers.download', (status) => downloads.push(status));
  return { client, backend: handle.backend, changed, downloads };
}

function run(backend: MockBackend, ms: number, step = 250) {
  for (let t = 0; t < ms; t += step) backend.tick(step);
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('drivers mock', () => {
  it('reads ?drivers= and ?uac=decline from the URL', () => {
    expect(mockOptionsFromUrl('?drivers=adbFailed&uac=decline')).toMatchObject({ drivers: 'adbFailed', uacDeclined: true });
    expect(mockOptionsFromUrl('?drivers=nonsense').drivers).toBeUndefined();
    expect(MOCK_DRIVERS_SCENARIOS).toContain('androidNoDriver');
  });

  it('answers every scenario with an iPhone and an Android row', async () => {
    for (const scenario of MOCK_DRIVERS_SCENARIOS) {
      const { client } = setup({ drivers: scenario });
      const list = await client.call('drivers.status');
      expect(list.map((d) => d.providerId), scenario).toEqual(['ios', 'android']);
      handle?.dispose();
    }
  });

  it('keeps ?mock=driverMissing meaning a missing Apple driver', async () => {
    const { client } = setup({ scenario: 'driverMissing' });
    expect((await client.call('drivers.status'))[0]).toMatchObject({ providerId: 'ios', state: 'missing', reason: 'amdsMissing' });
  });

  it('downloads Android tools with progress, then the row goes starting and ready', async () => {
    const { client, backend, changed, downloads } = setup({ drivers: 'adbMissing' });
    expect(await client.call('drivers.download', { providerId: 'android' })).toMatchObject({ state: 'downloading', totalBytes: PLATFORM_TOOLS_BYTES });
    run(backend, 5000);
    await flush();
    const received = downloads.filter((d) => d.state === 'downloading').map((d) => d.receivedBytes);
    expect(received).toEqual([...received].sort((a, b) => a - b));
    expect(received.at(-1)).toBe(PLATFORM_TOOLS_BYTES);
    expect(downloads.map((d) => d.state)).toEqual(expect.arrayContaining(['verifying', 'installing', 'done']));
    expect(changed.map((list) => list[1]!.state)).toEqual(['starting', 'ready']);
    expect(changed.at(-1)![1]).toMatchObject({ source: 'downloaded' });
  });

  it('cancels a download and can start it again', async () => {
    const { client, backend, downloads } = setup({ drivers: 'adbDownloading' });
    expect(await client.call('drivers.downloadStatus', { providerId: 'android' })).toMatchObject({ state: 'downloading' });
    run(backend, 5000); // ?drivers=adbDownloading stays where it is
    expect(await client.call('drivers.downloadStatus', { providerId: 'android' })).toMatchObject({ state: 'downloading' });
    await client.call('drivers.cancelDownload', { providerId: 'android' });
    expect(await client.call('drivers.downloadStatus', { providerId: 'android' })).toMatchObject({ state: 'cancelled' });
    expect((await client.call('drivers.download', { providerId: 'android' })).state).toBe('downloading');
    await flush();
    expect(downloads.at(-1)!.receivedBytes).toBe(0);
  });

  it('a download that fails says why', async () => {
    const { client, backend } = setup({ drivers: 'adbDownloadFails' });
    await client.call('drivers.download', { providerId: 'android' });
    run(backend, 5000);
    expect(await client.call('drivers.downloadStatus', { providerId: 'android' })).toMatchObject({ state: 'failed', errorCode: 'unverified' });
    expect((await client.call('drivers.status'))[1]!.state).toBe('missing');
  });

  it('starts Apple Mobile Device Service after the prompt, or says it was declined', async () => {
    const accepted = setup({ drivers: 'iosStopped' });
    await accepted.client.call('drivers.start', { providerId: 'ios' });
    run(accepted.backend, 1500);
    await flush();
    expect(accepted.changed.at(-1)![0]).toMatchObject({ state: 'ready' });
    handle?.dispose();

    const declined = setup({ drivers: 'iosStopped', uacDeclined: true });
    const error = await declined.client.call('drivers.start', { providerId: 'ios' }).catch((e: unknown) => e);
    expect(isBridgeError(error, 'permission_denied')).toBe(true);
  });

  it('restarts Android tools that failed', async () => {
    const { client, backend, changed } = setup({ drivers: 'adbFailed' });
    await client.call('drivers.start', { providerId: 'android' });
    run(backend, 1000);
    await flush();
    expect(changed.map((list) => list[1]!.state)).toEqual(['starting', 'ready']);
  });

  it('opens only the named links', async () => {
    const { client, backend } = setup({ drivers: 'iosMissing' });
    await client.call('drivers.openLink', { link: 'appleDevicesStore' });
    await client.call('drivers.openLink', { link: 'itunes' });
    const bad = await client.call('drivers.openLink', { link: 'https://example.com' as never }).catch((e: unknown) => e);
    expect(isBridgeError(bad, 'invalid_params')).toBe(true);
    expect(backend.drivers.opened).toEqual(['appleDevicesStore', 'itunes']);
  });
});
