import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import type { CleanupStatus } from '../../src/bridge/cleanupTypes';
import { BridgeError } from '../../src/bridge/errors';
import { METHOD_NAMES } from '../../src/bridge/methods';
import type { MockBackend } from '../../src/bridge/mock/backend';
import { OLD_PHONE_ID } from '../../src/bridge/mock/battery';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { niceRange } from '../../src/screens/battery/BatteryChart';

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const updates: CleanupStatus[] = [];
  client.on('cleanup.updated', (s) => updates.push(s));
  return { client, backend: handle.backend, updates };
}

function run(backend: MockBackend, ms: number) {
  for (let t = 0; t < ms; t += 250) backend.tick(250);
}

async function analyzed(client: BridgeClient, backend: MockBackend, deviceId: string) {
  await client.call('cleanup.analyze', { deviceId });
  run(backend, 3000);
  const status = await client.call('cleanup.status', { deviceId });
  expect(status.state).toBe('done');
  return status.report!;
}

async function rejection(promise: Promise<unknown>): Promise<BridgeError> {
  try {
    await promise;
  } catch (error) {
    return error as BridgeError;
  }
  throw new Error('expected the call to fail');
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('mock clean-up', () => {
  it('registers every cleanup and battery method', () => {
    for (const name of ['cleanup.status', 'cleanup.analyze', 'cleanup.cancel', 'cleanup.deleteMedia', 'cleanup.deleteFiles', 'cleanup.uninstallApps', 'cleanup.trimCaches', 'battery.devices', 'battery.history', 'battery.record', 'battery.export']) {
      expect(METHOD_NAMES).toContain(name);
    }
  });

  it('runs section by section and reports partial results on the way', async () => {
    const { client, backend, updates } = setup();
    const started = await client.call('cleanup.analyze', { deviceId: ANDROID_ID });
    expect(started.state).toBe('running');
    run(backend, 3000);
    const sections = [...new Set(updates.filter((u) => u.state === 'running').map((u) => u.progress?.section))];
    expect(sections).toEqual(['storage', 'apps', 'media', 'caches', 'appFiles', 'duplicates']);
    const mid = updates.find((u) => u.progress?.section === 'caches')!;
    expect(mid.report!.apps).not.toBeNull();
    expect(mid.report!.duplicates).toBeNull();

    const report = updates[updates.length - 1]!.report!;
    expect(updates[updates.length - 1]!.state).toBe('done');
    expect(report.media!.canDelete).toBe(true);
    expect(report.caches!.totalBytes).toBeGreaterThan(0);
    expect(report.appFiles!.files.map((f) => f.path)).toContain('/Download/Termux-app_v0.119.0.apk');
    expect(report.notes.map((n) => n.message).join()).toContain('Android/data');
    for (const group of report.duplicates!.groups) {
      expect(group.assets.length).toBeGreaterThan(1);
      expect(group.reclaimableBytes).toBe(group.size * (group.assets.length - 1));
    }
  });

  it('never deletes iPhone photos, only copies them', async () => {
    const { client, backend } = setup();
    const report = await analyzed(client, backend, IPHONE_ID);
    expect(report.media!.canDelete).toBe(false);
    expect(report.caches).toBeNull();
    const video = report.media!.largestVideos[0]!;
    const error = await rejection(client.call('cleanup.deleteMedia', { deviceId: IPHONE_ID, items: [{ id: video.id, size: video.size }], expectedBytes: video.size }));
    expect(error.code).toBe('not_supported');
    expect(error.message).toContain('Photos app');
    const page = await client.call('photos.list', { deviceId: IPHONE_ID, offset: 0, limit: 1 });
    expect(page.total).toBe(report.media!.videoCount + report.media!.photoCount + report.media!.cloudOnlyCount);
  });

  it('checks the confirmed total and that each item is one the analysis listed', async () => {
    const { client, backend } = setup();
    const report = await analyzed(client, backend, ANDROID_ID);
    const [a, b] = report.media!.largestVideos;
    const wrongTotal = await rejection(
      client.call('cleanup.deleteMedia', { deviceId: ANDROID_ID, items: [{ id: a!.id, size: a!.size }], expectedBytes: a!.size + 1 }),
    );
    expect(wrongTotal.code).toBe('invalid_params');
    const notListed = await rejection(client.call('cleanup.deleteMedia', { deviceId: ANDROID_ID, items: [{ id: 'nope', size: 1 }], expectedBytes: 1 }));
    expect(notListed.code).toBe('invalid_params');

    const before = (await client.call('photos.list', { deviceId: ANDROID_ID, offset: 0, limit: 1 })).total;
    const result = await client.call('cleanup.deleteMedia', {
      deviceId: ANDROID_ID,
      items: [a!, b!].map((v) => ({ id: v.id, size: v.size })),
      expectedBytes: a!.size + b!.size,
      copyFirstTo: 'D:\\Backup',
    });
    expect(result).toMatchObject({ deleted: 2, skipped: 0, freedBytes: a!.size + b!.size, copyIncomplete: false });
    expect(result.copyJobIds).toHaveLength(1);
    expect((await client.call('photos.list', { deviceId: ANDROID_ID, offset: 0, limit: 1 })).total).toBe(before - 2);
    const after = (await client.call('cleanup.status', { deviceId: ANDROID_ID })).report!;
    expect(after.media!.largestVideos.map((v) => v.id)).not.toContain(a!.id);
    expect(after.freeBytes).toBe(report.freeBytes! + a!.size + b!.size);
  });

  it('deletes app files, uninstalls apps and trims caches once', async () => {
    const { client, backend } = setup();
    const ios = await analyzed(client, backend, IPHONE_ID);
    const movie = ios.appFiles!.files[0]!;
    const files = await client.call('cleanup.deleteFiles', { deviceId: IPHONE_ID, items: [{ root: movie.root, path: movie.path, size: movie.size }], expectedBytes: movie.size });
    expect(files.deleted).toBe(1);
    expect(await client.call('files.list', { deviceId: IPHONE_ID, root: movie.root, path: '/' })).not.toContainEqual(expect.objectContaining({ path: movie.path }));

    const android = await analyzed(client, backend, ANDROID_ID);
    const app = android.apps!.largest[0]!;
    const size = app.appBytes + app.dataBytes;
    const removed = await client.call('cleanup.uninstallApps', { deviceId: ANDROID_ID, items: [{ id: app.id, size }], expectedBytes: size });
    expect(removed.deleted).toBe(1);
    expect((await client.call('apps.list', { deviceId: ANDROID_ID, includeSystem: true })).map((a) => a.id)).not.toContain(app.id);

    const trimmed = await client.call('cleanup.trimCaches', { deviceId: ANDROID_ID });
    expect(trimmed.freedBytes).toBeGreaterThan(0);
    expect((await client.call('cleanup.trimCaches', { deviceId: ANDROID_ID })).freedBytes).toBe(0);
  });

  it('refuses to act before an analysis and while one runs', async () => {
    const { client, backend } = setup();
    const none = await rejection(client.call('cleanup.trimCaches', { deviceId: ANDROID_ID }));
    expect(none.message).toContain('Analyse the phone first');
    await client.call('cleanup.analyze', { deviceId: ANDROID_ID });
    backend.tick(250);
    const busy = await rejection(client.call('cleanup.trimCaches', { deviceId: ANDROID_ID }));
    expect(busy.message).toContain('Wait for the analysis');
    await client.call('cleanup.cancel', { deviceId: ANDROID_ID });
    expect((await client.call('cleanup.status', { deviceId: ANDROID_ID })).state).toBe('cancelled');
  });
});

describe('mock battery history', () => {
  it('lists every phone with history, the old one included, newest first', async () => {
    const { client } = setup();
    const devices = await client.call('battery.devices');
    expect(devices.map((d) => d.deviceId)).toEqual([IPHONE_ID, ANDROID_ID, OLD_PHONE_ID]);
    const history = await client.call('battery.history', { deviceId: IPHONE_ID });
    expect(history.length).toBe(devices[0]!.count);
    expect([...history].sort((a, b) => a.at.localeCompare(b.at))).toEqual(history);
    expect(history.every((s) => s.healthEstimated)).toBe(true);
    expect(history[history.length - 1]!.healthPercent).toBe(87);
    expect((await client.call('battery.history', { deviceId: ANDROID_ID })).some((s) => s.healthEstimated)).toBe(false);
  });

  it('records now and exports a report path', async () => {
    const { client } = setup();
    const recorded: unknown[] = [];
    client.on('battery.recorded', (s) => recorded.push(s));
    const before = (await client.call('battery.history', { deviceId: ANDROID_ID })).length;
    const snapshot = await client.call('battery.record', { deviceId: ANDROID_ID });
    expect(snapshot.healthPercent).toBe(92);
    expect((await client.call('battery.history', { deviceId: ANDROID_ID })).length).toBe(before + 1);
    expect(recorded).toHaveLength(1);
    const { path } = await client.call('battery.export', { deviceId: ANDROID_ID, destination: 'D:\\Reports', language: 'en' });
    expect(path).toMatch(/^D:\\Reports\\Battery report - Galaxy S25 Ultra - \d{4}-\d{2}-\d{2}\.html$/);
  });

  it('picks chart ticks the same way the exported report does', () => {
    expect(niceRange(88, 99)).toEqual({ lo: 85, hi: 100, step: 5 });
    expect(niceRange(3000, 3561)).toEqual({ lo: 3000, hi: 3600, step: 200 });
    expect(niceRange(0, 530)).toEqual({ lo: 0, hi: 600, step: 200 });
    expect(niceRange(91, 91, 100)).toEqual({ lo: 90, hi: 92, step: 1 });
    expect(niceRange(96, 100, 100)).toEqual({ lo: 96, hi: 100, step: 1 });
  });
});
