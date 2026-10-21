import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import type { MockBackend } from '../../src/bridge/mock/backend';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { isValidPattern } from '../../src/bridge/mock/photosBackup';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { exampleFolder } from '../../src/screens/photos/backupQueries';

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false });
  return { client: new BridgeClient(handle.transport, { timeoutMs: 0 }), backend: handle.backend };
}

function run(backend: MockBackend, ms: number, step = 250) {
  for (let t = 0; t < ms; t += step) backend.tick(step);
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('photo backup (mock)', () => {
  it('counts what is new since the pretend last backup, per phone', async () => {
    const { client } = setup();
    const iphone = await client.call('photos.newCount', { deviceId: IPHONE_ID });
    expect(iphone.newCount).toBe(128 - iphone.cloudOnly);
    expect(iphone.newPhotos + iphone.newVideos).toBe(iphone.newCount);
    expect(iphone.hiddenLeftOut).toBeGreaterThan(0);
    expect((await client.call('photos.newCount', { deviceId: ANDROID_ID })).newCount).toBe(46);

    const status = await client.call('photos.backupStatus', { deviceId: IPHONE_ID });
    expect(Object.keys(status)).toEqual(['deviceId', 'count', 'options', 'customized', 'backedUp', 'lastRun', 'activeJobId', 'heicConversion', 'space', 'onConnect']);
    expect(status.space).toMatchObject({ root: 'D:\\', enough: true, nearlyFull: false, driveMissing: false });
    expect(status.space.neededBytes).toBeGreaterThan(status.count.newBytes);
    expect(status.options).toEqual({
      destination: 'D:\\Phone backups\\Nima’s iPhone',
      folderPattern: 'yyyy/MM',
      convertHeic: false,
      includeHidden: false,
      range: { kind: 'all', days: null, since: null },
      saveEdited: false,
    });
    expect(status.count.outOfRange).toBe(0);
    expect(status.lastRun?.state).toBe('done');
  });

  it('backs up the new photos through the queue; then nothing is new', async () => {
    const { client, backend } = setup();
    const before = await client.call('photos.newCount', { deviceId: ANDROID_ID });
    const { jobId } = await client.call('photos.backup', { deviceId: ANDROID_ID });
    expect((await client.call('photos.backup', { deviceId: ANDROID_ID })).jobId).toBe(jobId); // one at a time per phone

    const job = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(job.kind).toBe('photosBackup');
    expect(job.totalItems).toBe(before.newCount);
    expect(job.title).toMatch(/^Backing up new photos from .+ to D:\\Phone backups\\/);

    run(backend, 120_000);
    expect((await client.call('transfers.list')).find((j) => j.id === jobId)!.state).toBe('done');
    const after = await client.call('photos.backupStatus', { deviceId: ANDROID_ID });
    expect(after.count.newCount).toBe(0);
    expect(after.activeJobId).toBeNull();
    expect(after.lastRun).toMatchObject({ jobId, state: 'done', copied: before.newCount });
  });

  it('keeps options per phone, checks them and goes back to the defaults', async () => {
    const { client } = setup();
    const changed = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, folderPattern: '', includeHidden: true });
    expect(changed.customized).toBe(true);
    expect(changed.options.folderPattern).toBe('');
    expect(changed.count.hiddenLeftOut).toBe(0);
    await expect(client.call('photos.backupConfigure', { deviceId: IPHONE_ID, folderPattern: 'MMMM' })).rejects.toMatchObject({ rpcCode: -32602 });
    await expect(client.call('photos.backupConfigure', { deviceId: IPHONE_ID, destination: 'backups' })).rejects.toMatchObject({ rpcCode: -32602 });

    await client.call('settings.set', { photoBackupPattern: 'yyyy-MM' });
    const reset = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, useDefaults: true });
    expect(reset.customized).toBe(false);
    expect(reset.options.folderPattern).toBe('yyyy-MM');
  });

  it('leaves photos before the date range out, per phone, and checks the range', async () => {
    const { client, backend } = setup();
    const all = await client.call('photos.newCount', { deviceId: IPHONE_ID });
    const last30 = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, range: { kind: 'lastDays', days: 30, since: null } });
    expect(last30.options.range).toEqual({ kind: 'lastDays', days: 30, since: null });
    expect(last30.customized).toBe(true);
    expect(last30.count.newCount).toBeLessThan(all.newCount);
    expect(last30.count.outOfRange).toBeGreaterThan(0);
    // The other phone keeps everything.
    expect((await client.call('photos.backupStatus', { deviceId: ANDROID_ID })).options.range.kind).toBe('all');

    const { jobId } = await client.call('photos.backup', { deviceId: IPHONE_ID });
    expect((await client.call('transfers.list')).find((j) => j.id === jobId)!.totalItems).toBe(last30.count.newCount);
    run(backend, 120_000);
    const after = await client.call('photos.backupStatus', { deviceId: IPHONE_ID });
    expect(after.count.newCount).toBe(0);
    expect(after.count.outOfRange).toBe(last30.count.outOfRange);

    for (const range of [
      { kind: 'lastDays', days: 0, since: null },
      { kind: 'since', days: null, since: 'yesterday' },
      { kind: 'sometimes', days: null, since: null },
    ]) {
      await expect(client.call('photos.backupConfigure', { deviceId: IPHONE_ID, range: range as never })).rejects.toMatchObject({ rpcCode: -32602 });
    }
    const reset = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, useDefaults: true });
    expect(reset.options.range.kind).toBe('all');
  });

  it('refuses a backup that would not fit or whose drive is gone, and says when a drive is nearly full', async () => {
    const { client } = setup();
    const short = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, destination: 'F:\\Phone backups' });
    expect(short.space).toMatchObject({ root: 'F:\\', enough: false, nearlyFull: true, driveMissing: false });
    await expect(client.call('photos.backup', { deviceId: IPHONE_ID })).rejects.toMatchObject({ code: 'no_space' });

    const gone = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, destination: 'G:\\Phone backups' });
    expect(gone.space).toMatchObject({ driveMissing: true, enough: false, freeBytes: null });
    await expect(client.call('photos.backup', { deviceId: IPHONE_ID })).rejects.toMatchObject({ code: 'path_not_found' });
    expect(await client.call('transfers.list')).not.toContainEqual(expect.objectContaining({ kind: 'photosBackup', deviceId: IPHONE_ID }));

    expect(await client.call('photos.backupSpace', { folder: 'C:\\Users\\Nima\\Pictures\\DeviceDeck Backup' })).toMatchObject({
      root: 'C:\\',
      neededBytes: 0,
      enough: true,
      nearlyFull: true,
    });
    await expect(client.call('photos.backupSpace', { folder: 'Pictures' })).rejects.toMatchObject({ rpcCode: -32602 });
  });

  it('backs up by itself when a phone set to do so connects, and only then', async () => {
    const { client, backend } = setup();
    expect((await client.call('photos.backupStatus', { deviceId: ANDROID_ID })).onConnect).toBe('notify');

    // The default (a notification is the host's): plugging in starts nothing.
    backend.unplug(ANDROID_ID);
    backend.plugBack(ANDROID_ID);
    run(backend, 2_000);
    expect((await client.call('transfers.list')).filter((j) => j.kind === 'photosBackup')).toHaveLength(0);

    const set = await client.call('photos.backupConfigure', { deviceId: ANDROID_ID, onConnect: 'backUp' });
    expect(set.onConnect).toBe('backUp');
    expect(set.customized).toBe(false); // not one of the Settings defaults
    await expect(client.call('photos.backupConfigure', { deviceId: ANDROID_ID, onConnect: 'sometimes' as never })).rejects.toMatchObject({ rpcCode: -32602 });

    backend.unplug(ANDROID_ID);
    backend.plugBack(ANDROID_ID);
    run(backend, 2_000);
    const job = (await client.call('transfers.list')).find((j) => j.kind === 'photosBackup' && j.deviceId === ANDROID_ID);
    expect(job).toBeDefined();
    run(backend, 120_000);
    const after = await client.call('photos.backupStatus', { deviceId: ANDROID_ID });
    expect(after.lastRun).toMatchObject({ jobId: job!.id, state: 'done', trigger: 'automatic' });
    expect(after.count.newCount).toBe(0);
    expect((await client.call('photos.backupConfigure', { deviceId: ANDROID_ID, useDefaults: true })).onConnect).toBe('backUp');
  });

  it('saves the edited versions of an iPhone’s edited photos when asked, once per edit', async () => {
    const { client, backend } = setup();
    expect((await client.call('photos.newCount', { deviceId: IPHONE_ID })).newEdits).toBe(0);
    const on = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, saveEdited: true });
    expect(on.options.saveEdited).toBe(true);
    expect(on.count.newEdits).toBeGreaterThan(0);

    const { jobId } = await client.call('photos.backup', { deviceId: IPHONE_ID });
    run(backend, 1_200_000);
    const after = await client.call('photos.backupStatus', { deviceId: IPHONE_ID });
    expect(after.lastRun).toMatchObject({ jobId, state: 'done', edited: on.count.newEdits, editsMissing: 0 });
    expect(after.count.newEdits).toBe(0);
  });

  it('forgets the history without touching files', async () => {
    const { client } = setup();
    const status = await client.call('photos.backupStatus', { deviceId: ANDROID_ID });
    const { forgotten } = await client.call('photos.backupForget', { deviceId: ANDROID_ID });
    expect(forgotten).toBe(status.backedUp);
    expect((await client.call('photos.backupStatus', { deviceId: ANDROID_ID })).backedUp).toBe(0);
  });

  it('copies photos into an iPhone app that shares files, never over a file there', async () => {
    const { client, backend } = setup();
    const files = backend.registerFiles([new File([new Uint8Array(2048)], 'beach.jpg'), new File([new Uint8Array(1024)], 'beach.jpg')]);
    const { jobId } = await client.call('photos.importToApp', { deviceId: IPHONE_ID, appId: 'org.videolan.vlc-ios', files });
    const job = (await client.call('transfers.list')).find((j) => j.id === jobId)!;
    expect(job.kind).toBe('photosToApp');
    expect(job.title).toBe('Adding 2 items to VLC on Nima’s iPhone');
    run(backend, 10_000);
    const names = (await client.call('files.list', { deviceId: IPHONE_ID, root: 'app:org.videolan.vlc-ios', path: '/' })).map((e) => e.name);
    expect(names).toContain('beach.jpg');
    expect(names).toContain('beach (1).jpg');

    await expect(client.call('photos.importToApp', { deviceId: IPHONE_ID, appId: 'com.apple.Maps', files })).rejects.toMatchObject({ code: 'path_not_found' });
    await expect(client.call('photos.importToApp', { deviceId: ANDROID_ID, appId: 'x', files })).rejects.toMatchObject({ code: 'not_supported' });
  });

  it('folder patterns: what the host accepts, and the example the UI shows', () => {
    for (const ok of ['yyyy/MM', 'yyyy/MM/dd', 'yyyy-MM', 'yyyy', '', 'yyyy MM']) expect(isValidPattern(ok), ok).toBe(true);
    for (const bad of ['MMMM', 'yyyy/', '/MM', 'hh', 'yyyy/../MM']) expect(isValidPattern(bad), bad).toBe(false);
    const when = new Date(2026, 9, 7);
    expect(exampleFolder('yyyy/MM', when)).toBe('2026\\10');
    expect(exampleFolder('yyyy/MM/dd', when)).toBe('2026\\10\\07');
    expect(exampleFolder('yyyy-MM', when)).toBe('2026-10');
    expect(exampleFolder('', when)).toBe('');
  });
});
