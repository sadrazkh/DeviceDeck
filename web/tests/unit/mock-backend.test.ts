import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { METHOD_NAMES, type BridgeEvents, type EventName } from '../../src/bridge/methods';
import { MockBackend, type MockOptions } from '../../src/bridge/mock/backend';
import { ANDROID_ID, GB, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { keepBothName } from '../../src/bridge/mock/transfers';
import type { TransferJob } from '../../src/bridge/types';

let handle: MockHandle | null = null;

function setup(options: MockOptions = {}) {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, ...options });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const events: { event: string; params: unknown }[] = [];
  const record = <E extends EventName>(event: E) => client.on(event, (params: BridgeEvents[E]) => events.push({ event, params }));
  (['device.added', 'device.changed', 'device.removed', 'transfer.updated', 'photos.indexProgress', 'drivers.changed'] as const).forEach(record);
  return { client, backend: handle.backend, events };
}

/** Runs simulated time in steps, like the 250 ms timer in the app. */
function run(backend: MockBackend, ms: number, step = 250) {
  for (let t = 0; t < ms; t += step) backend.tick(step);
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('mock backend', () => {
  it('implements every method in the contract', () => {
    const backend = new MockBackend(() => {});
    for (const method of METHOD_NAMES) {
      expect(typeof backend.handlers[method], method).toBe('function');
    }
    expect(METHOD_NAMES).toHaveLength(34 + 11); // round 1, plus clean-up and battery history
  });

  it('serves the sample devices', async () => {
    const { client } = setup();
    const devices = await client.call('device.list');
    expect(devices.map((d) => [d.name, d.marketingName, d.osVersion, d.connection, d.state])).toEqual([
      ['Nima’s iPhone', 'iPhone 17 Pro', '26.1', 'usb', 'ready'],
      ['Galaxy S25 Ultra', 'Galaxy S25 Ultra', '16', 'wifi', 'ready'],
    ]);
    const hello = await client.call('app.hello');
    expect(hello.isWebView).toBe(false);
  });

  it('matches the brief’s storage figures', async () => {
    const { client } = setup();
    const info = await client.call('device.storage', { deviceId: IPHONE_ID });
    const volume = info.volumes[0]!;
    const used = volume.categories.reduce((n, c) => n + c.bytes, 0);
    expect(volume.totalBytes).toBe(256 * GB);
    expect(volume.freeBytes).toBe(101 * GB);
    expect(Math.round(used / 1e8) / 10).toBe(155);
    expect(volume.categories.map((c) => c.key)).toEqual(['photos', 'videos', 'apps', 'media', 'system', 'other']);
    const battery = await client.call('device.battery', { deviceId: IPHONE_ID });
    expect(battery).toMatchObject({ healthPercent: 87, cycleCount: 412, temperatureCelsius: 31, isCharging: true });
  });

  it('generates the photo libraries, newest first, with exact counts', async () => {
    const { client } = setup({ indexing: false });
    const albums = await client.call('photos.albums', { deviceId: IPHONE_ID });
    // The brief's 18 albums plus the Hidden album, which the iOS provider lists as id "hidden".
    expect(albums).toHaveLength(19);
    expect(albums.at(-1)).toEqual({ id: 'hidden', name: 'Hidden', kind: 'user', count: 1847 });
    const hidden = await client.call('photos.list', { deviceId: IPHONE_ID, albumId: 'hidden', offset: 0, limit: 5000 });
    expect(hidden.total).toBe(1847);
    expect(hidden.items.every((a) => a.id.startsWith('ios-hidden-'))).toBe(true);
    const all = albums.find((a) => a.kind === 'all')!;
    const videos = albums.find((a) => a.kind === 'videos')!;
    expect(all.count).toBe(12_481 + 732);
    expect(videos.count).toBe(732);

    const page = await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 500 });
    expect(page.total).toBe(13_213);
    expect(page.items).toHaveLength(500);
    const times = page.items.map((a) => Date.parse(a.takenAt!));
    expect([...times].sort((a, b) => b - a)).toEqual(times);
    // Contract shape only: no mock-only fields leak.
    expect(Object.keys(page.items[0]!).sort()).toEqual(
      ['durationSeconds', 'fileName', 'height', 'id', 'inCloudOnly', 'isFavorite', 'kind', 'livePairId', 'size', 'takenAt', 'width'].sort(),
    );

    const next = await client.call('photos.list', { deviceId: IPHONE_ID, offset: 500, limit: 500 });
    expect(next.items[0]!.id).not.toBe(page.items[0]!.id);
    const tail = await client.call('photos.list', { deviceId: IPHONE_ID, offset: 13_000, limit: 500 });
    expect(tail.items).toHaveLength(213);

    const androidAlbums = await client.call('photos.albums', { deviceId: ANDROID_ID });
    expect(androidAlbums.find((a) => a.kind === 'all')!.count).toBe(21_906 + 1_140);
    expect(androidAlbums.filter((a) => a.kind === 'folder')).toHaveLength(34);
  });

  it('reports first indexing progress', async () => {
    const { client, backend, events } = setup();
    await client.call('photos.albums', { deviceId: IPHONE_ID });
    run(backend, 7000);
    const progress = events.filter((e) => e.event === 'photos.indexProgress').map((e) => e.params as { done: number; total: number });
    expect(progress.length).toBeGreaterThan(5);
    expect(progress.at(-1)).toEqual({ deviceId: IPHONE_ID, done: 13_213, total: 13_213 });
  });

  it('refuses calls with the contract’s error codes', async () => {
    const { client } = setup({ scenario: 'trust' });
    await expect(client.call('device.storage', { deviceId: IPHONE_ID })).rejects.toMatchObject({ code: 'trust_required', rpcCode: -32000 });
    await expect(client.call('device.storage', { deviceId: ANDROID_ID })).rejects.toMatchObject({ code: 'unauthorized' });
    await expect(client.call('device.storage', { deviceId: 'nope' })).rejects.toMatchObject({ code: 'device_not_found' });
    await expect(client.call('photos.list', { deviceId: IPHONE_ID, offset: -1, limit: 1 })).rejects.toBeTruthy();
  });

  it('limited mode only reads photos', async () => {
    const { client } = setup({ scenario: 'limited' });
    const [device] = await client.call('device.list');
    expect(device!.mode).toBe('limited');
    await expect(client.call('device.storage', { deviceId: IPHONE_ID })).rejects.toMatchObject({ code: 'not_supported' });
    const drivers = await client.call('drivers.status');
    expect(drivers.find((d) => d.providerId === 'ios')!.state).toBe('missing');
  });

  it('progresses the transfer queue over time', async () => {
    const { client, backend } = setup();
    const before = await client.call('transfers.list');
    expect(before.map((j) => j.state)).toEqual(['running', 'running', 'queued', 'failed', 'done']);
    const exportBefore = before[0]!;
    expect(Math.round((exportBefore.doneBytes / exportBefore.totalBytes) * 100)).toBe(63);
    expect(exportBefore.etaSeconds).toBeGreaterThanOrEqual(110);

    run(backend, 10_000);
    const after = await client.call('transfers.list');
    expect(after[0]!.doneBytes).toBeGreaterThan(exportBefore.doneBytes);
    expect(after[0]!.bytesPerSecond).toBeGreaterThan(20_000_000);
    // The Snapseed install waits a few seconds, then runs.
    expect(['running', 'done']).toContain(after[2]!.state);

    run(backend, 200_000);
    const done = await client.call('transfers.list');
    expect(done[0]!.state).toBe('done');
    expect(done[0]!.doneItems).toBe(1284);
    expect(done[0]!.finishedAt).not.toBeNull();
  });

  it('a pulled cable pauses work; the same phone reconnecting resumes it', async () => {
    const { client, backend, events } = setup();
    run(backend, 1000);
    backend.unplug(IPHONE_ID);
    const offline = await client.call('transfers.list');
    const export1 = offline.find((j) => j.id === 'job-export-1284')!;
    expect(export1.state).toBe('waitingForDevice');
    expect(offline.find((j) => j.id === 'job-snapseed')!.state).toBe('waitingForDevice');
    expect(events.some((e) => e.event === 'device.changed' && (e.params as { state: string }).state === 'disconnected')).toBe(true);
    await expect(client.call('device.storage', { deviceId: IPHONE_ID })).rejects.toMatchObject({ code: 'device_disconnected' });

    run(backend, 5000);
    const still = (await client.call('transfers.list')).find((j) => j.id === 'job-export-1284')!;
    expect(still.doneBytes).toBe(export1.doneBytes);

    backend.plugBack(IPHONE_ID);
    run(backend, 2000);
    const [iphoneBack] = await client.call('device.list');
    expect(iphoneBack!.state).toBe('ready');
    const resumed = (await client.call('transfers.list')).find((j) => j.id === 'job-export-1284')!;
    expect(resumed.state).toBe('running');
    expect(resumed.doneBytes).toBeGreaterThan(export1.doneBytes);
  });

  it('asks about a name conflict once and honours keep both', async () => {
    const { client, backend } = setup({ seedQueue: false });
    const file = new File([new Uint8Array(1024)], 'Persepolis.mkv');
    const { jobId } = await client.callWithFiles('files.copyToDevice', { deviceId: IPHONE_ID, root: 'app:org.videolan.vlc-ios', path: '/' }, [file]);
    run(backend, 500);
    let job = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(job.title).toBe('Persepolis.mkv into VLC');
    expect(job.state).toBe('needsDecision');
    expect(job.conflict).toMatchObject({ itemName: 'Persepolis.mkv', newSize: 1024 });
    expect(job.conflict!.existingSize).toBeGreaterThan(1e9);

    await client.call('transfers.resolveConflict', { jobId, choice: 'keepBoth', applyToAll: true });
    run(backend, 1000);
    job = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(job.state).toBe('done');
    const listing = await client.call('files.list', { deviceId: IPHONE_ID, root: 'app:org.videolan.vlc-ios', path: '/' });
    expect(listing.map((e) => e.name)).toContain('Persepolis (2).mkv');
  });

  it('installs apps and reports an unsigned .ipa', async () => {
    const { client, backend } = setup({ seedQueue: false });
    await expect(
      client.callWithFiles('apps.install', { deviceId: IPHONE_ID }, ['C:\\x\\Termux.apk']),
    ).rejects.toMatchObject({ code: 'install_failed' });

    const bad = await client.callWithFiles('apps.install', { deviceId: IPHONE_ID }, ['C:\\x\\Tool-unsigned.ipa']);
    const good = await client.callWithFiles('apps.install', { deviceId: ANDROID_ID }, ['C:\\x\\NewsReader_2.4.apk']);
    run(backend, 20_000);
    const jobs = await client.call('transfers.list');
    const failed = jobs.find((j) => j.id === bad.jobId)!;
    expect(failed.state).toBe('failed');
    expect(failed.error).toEqual({ code: 'install_failed', message: 'This .ipa isn’t signed for this iPhone.' });
    expect(jobs.find((j) => j.id === good.jobId)!.state).toBe('done');
    const apps = await client.call('apps.list', { deviceId: ANDROID_ID, includeSystem: false });
    expect(apps.find((a) => a.name === 'NewsReader')).toMatchObject({ version: '2.4', source: 'sideloaded' });
  });

  it('filters system apps and refuses to remove them', async () => {
    const { client } = setup();
    const user = await client.call('apps.list', { deviceId: ANDROID_ID, includeSystem: false });
    const all = await client.call('apps.list', { deviceId: ANDROID_ID, includeSystem: true });
    expect(user.every((a) => a.source !== 'system')).toBe(true);
    expect(all.length).toBeGreaterThan(user.length);
    await expect(client.call('apps.uninstall', { deviceId: ANDROID_ID, appId: 'com.android.chrome' })).rejects.toMatchObject({ code: 'not_supported' });
    await client.call('apps.uninstall', { deviceId: ANDROID_ID, appId: 'com.termux' });
    const after = await client.call('apps.list', { deviceId: ANDROID_ID, includeSystem: true });
    expect(after.find((a) => a.id === 'com.termux')).toBeUndefined();
  });

  it('serves the file roots and folders, including blocked and very large ones', async () => {
    const { client } = setup();
    const roots = await client.call('files.roots', { deviceId: IPHONE_ID });
    expect(roots.map((r) => r.label)).toEqual(['VLC', 'Documents', 'Infuse', 'GoodReader', 'Media folder']);
    expect(await client.call('files.list', { deviceId: IPHONE_ID, root: 'app:com.goodiware.GoodReader', path: '/' })).toEqual([]);
    expect(roots[0]).toMatchObject({ kind: 'appDocuments', appId: 'org.videolan.vlc-ios' });

    await expect(client.call('files.list', { deviceId: ANDROID_ID, root: 'internal', path: '/Android/data' })).rejects.toMatchObject({
      code: 'permission_denied',
    });
    const camera = await client.call('files.list', { deviceId: ANDROID_ID, root: 'internal', path: '/DCIM/Camera' });
    expect(camera).toHaveLength(20_000);

    const made = await client.call('files.mkdir', { deviceId: ANDROID_ID, root: 'internal', path: '/Download/New folder' });
    expect(made).toMatchObject({ isDirectory: true, path: '/Download/New folder' });
    await expect(client.call('files.mkdir', { deviceId: ANDROID_ID, root: 'internal', path: '/Download/New folder' })).rejects.toMatchObject({
      code: 'already_exists',
    });
    const renamed = await client.call('files.rename', { deviceId: ANDROID_ID, root: 'internal', from: '/Download/New folder', to: '/Download/Trips' });
    expect(renamed.name).toBe('Trips');
    const { deleted } = await client.call('files.delete', { deviceId: ANDROID_ID, root: 'internal', paths: ['/Download/Trips'], recursive: true });
    expect(deleted).toBe(1);
    await expect(
      client.call('files.delete', { deviceId: IPHONE_ID, root: 'app:com.firecore.infuse', paths: ['/Library Cache'], recursive: true }),
    ).rejects.toMatchObject({ code: 'permission_denied' });
  });

  it('deletes photos and updates album counts', async () => {
    const { client } = setup({ indexing: false });
    const page = await client.call('photos.list', { deviceId: ANDROID_ID, offset: 0, limit: 3 });
    const { deleted } = await client.call('photos.delete', { deviceId: ANDROID_ID, assetIds: page.items.map((a) => a.id) });
    expect(deleted).toBe(3);
    const albums = await client.call('photos.albums', { deviceId: ANDROID_ID });
    expect(albums.find((a) => a.kind === 'all')!.count).toBe(23_046 - 3);
  });

  it('starts an export job titled like the brief', async () => {
    const { client } = setup({ seedQueue: false, indexing: false });
    const page = await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 200 });
    const ids = page.items.filter((a) => !a.inCloudOnly).map((a) => a.id);
    const { jobId } = await client.call('photos.export', {
      deviceId: IPHONE_ID,
      assetIds: ids,
      destination: 'D:\\Photos\\2026',
      convertHeic: true,
      folderPattern: 'yyyy/MM',
    });
    const job = (await client.call('transfers.list')).find((j: TransferJob) => j.id === jobId)!;
    expect(job.title).toBe(`Exporting ${ids.length} photos to D:\\Photos\\2026`);
    expect(job.totalItems).toBe(ids.length);
  });

  it('simulates a new phone: connecting, the Trust prompt, then ready', async () => {
    const { client, backend, events } = setup({ scenario: 'empty' });
    expect(await client.call('device.list')).toEqual([]);
    const lastDeviceEvent = () => events.filter((e) => e.event.startsWith('device.')).at(-1);
    backend.connectNew(IPHONE_ID);
    expect(lastDeviceEvent()).toMatchObject({ event: 'device.added', params: { state: 'connecting' } });
    run(backend, 1500);
    expect(lastDeviceEvent()).toMatchObject({ event: 'device.changed', params: { state: 'awaitingTrust' } });
    // The sample queue's iPhone jobs wait until the phone is usable.
    const waiting = (await client.call('transfers.list')).find((j) => j.id === 'job-export-1284')!;
    expect(waiting.state).toBe('waitingForDevice');
    backend.approve(IPHONE_ID);
    run(backend, 1000);
    expect(lastDeviceEvent()).toMatchObject({ event: 'device.changed', params: { state: 'ready' } });
    const resumed = (await client.call('transfers.list')).find((j) => j.id === 'job-export-1284')!;
    expect(resumed.state).toBe('running');
  });

  it('keeps settings and hands back folders from the picker', async () => {
    const { client } = setup({ settings: { language: 'fa' } });
    expect((await client.call('settings.get')).language).toBe('fa');
    const next = await client.call('settings.set', { theme: 'dark', density: 'compact' });
    expect(next).toMatchObject({ language: 'fa', theme: 'dark', density: 'compact' });
    const picked = await client.call('shell.pickFolder', { title: 'Export to' });
    expect(picked.path).toMatch(/^[A-Z]:\\/);
  });

  it('answers unknown methods with -32601', async () => {
    const { client } = setup();
    // @ts-expect-error not a contract method
    await expect(client.call('device.rename', { deviceId: IPHONE_ID })).rejects.toMatchObject({ rpcCode: -32601, code: 'method_not_found' });
  });
});

describe('keepBothName', () => {
  it('numbers copies', () => {
    expect(keepBothName('Persepolis.mkv')).toBe('Persepolis (2).mkv');
    expect(keepBothName('Persepolis (2).mkv')).toBe('Persepolis (3).mkv');
    expect(keepBothName('README')).toBe('README (2)');
  });
});
