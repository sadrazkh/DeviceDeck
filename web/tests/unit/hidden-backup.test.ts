import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { hiddenFolderProblem, hiddenRoot, wantsPhoto } from '../../src/bridge/hiddenBackup';
import type { MockBackend } from '../../src/bridge/mock/backend';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { HIDDEN_COUNT } from '../../src/bridge/mock/photos';
import type { MockPhotoBackup } from '../../src/bridge/mock/photosBackup';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { en } from '../../src/i18n/en';
import { fa } from '../../src/i18n/fa';
import { examplePath } from '../../src/screens/photos/backupQueries';

// Hidden photos in the photo backup (round 4, hiddenbackup): the rules the page shares with the host, and the mock.

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false });
  return { client: new BridgeClient(handle.transport, { timeoutMs: 0 }), backend: handle.backend };
}

const lastItems = (backend: MockBackend) => (backend as unknown as { photoBackup: MockPhotoBackup }).photoBackup.lastItems;

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('hidden folder names', () => {
  it('takes one folder name Windows accepts, Persian too', () => {
    for (const ok of ['Hidden', 'پنهان', 'عکس‌های پنهان', 'My hidden photos', 'Hidden 2026', '.hidden', '  Hidden  ']) {
      expect(hiddenFolderProblem(ok), ok).toBeNull();
    }
  });

  it('says why anything else is refused', () => {
    const cases: [string, string][] = [
      ['', 'empty'],
      ['   ', 'empty'],
      ['x'.repeat(65), 'tooLong'],
      ['a/b', 'separator'],
      ['a\\b', 'separator'],
      ['a:b', 'characters'],
      ['what?', 'characters'],
      ['<x>', 'characters'],
      ['a|b', 'characters'],
      ['tab\tname', 'characters'],
      ['abc\u202edef', 'characters'],
      ['.', 'dot'],
      ['..', 'dot'],
      ['Hidden.', 'dot'],
      ['CON', 'device'],
      ['con.txt', 'device'],
      ['com1', 'device'],
      ['LPT9', 'device'],
      ['Undated', 'date'],
      ['2026', 'date'],
      ['2026-10', 'date'],
      ['10.2026', 'date'],
    ];
    for (const [name, problem] of cases) expect(hiddenFolderProblem(name), JSON.stringify(name)).toBe(problem);
  });

  it('has a message for every problem in both languages', () => {
    for (const problem of ['empty', 'tooLong', 'separator', 'characters', 'dot', 'device', 'date']) {
      const key = `photos.backup.hidden.problem.${problem}` as keyof typeof en;
      expect(en[key], key).toBeTruthy();
      expect(fa[key], key).toBeTruthy();
    }
  });
});

describe('which photos and where', () => {
  it('backs up normal only, both, or hidden only', () => {
    expect([wantsPhoto('exclude', false), wantsPhoto('exclude', true)]).toEqual([true, false]);
    expect([wantsPhoto('include', false), wantsPhoto('include', true)]).toEqual([true, true]);
    expect([wantsPhoto('only', false), wantsPhoto('only', true)]).toEqual([false, true]);
  });

  it('puts hidden ones under their folder only when kept apart', () => {
    expect(hiddenRoot('E:\\Backup\\Sadal\\', 'include', 'separate', 'Hidden')).toBe('E:\\Backup\\Sadal\\Hidden');
    expect(hiddenRoot('E:\\Backup\\Sadal', 'only', 'separate', ' پنهان ')).toBe('E:\\Backup\\Sadal\\پنهان');
    expect(hiddenRoot('E:\\Backup\\Sadal', 'include', 'together', 'Hidden')).toBeNull();
    expect(hiddenRoot('E:\\Backup\\Sadal', 'exclude', 'separate', 'Hidden')).toBeNull();
  });

  it('shows an example path like the host writes it', () => {
    const when = new Date(2026, 9, 7);
    expect(examplePath('E:\\Backup\\Sadal', 'yyyy/MM', when, false, 'Hidden')).toBe('E:\\Backup\\Sadal\\Hidden\\2026\\10\\IMG_1234.HEIC');
    expect(examplePath('E:\\Backup\\Sadal\\', 'yyyy/MM/dd', when, true, null)).toBe('E:\\Backup\\Sadal\\2026\\10\\07\\IMG_1234.jpg');
    expect(examplePath('E:\\Backup\\Sadal', '', when, false, 'پنهان')).toBe('E:\\Backup\\Sadal\\پنهان\\IMG_1234.HEIC');
  });
});

describe('hidden photos in the mock backup', () => {
  it('counts each combination and plans hidden ones into their own dated folder', async () => {
    const { client } = setup();
    const before = await client.call('photos.backupStatus', { deviceId: IPHONE_ID });
    expect(before.count).toMatchObject({ hiddenLeftOut: HIDDEN_COUNT, newHidden: 0, normalLeftOut: 0 });

    const both = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, hidden: 'include' });
    expect(both.options).toMatchObject({ hidden: 'include', hiddenLayout: 'separate', hiddenFolder: 'Hidden' });
    expect(both.count).toMatchObject({ newCount: before.count.newCount + HIDDEN_COUNT, newHidden: HIDDEN_COUNT, hiddenLeftOut: 0, normalLeftOut: 0 });
    expect(both.space.neededBytes).toBeGreaterThan(before.space.neededBytes); // the free-space check counts them

    const only = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, hidden: 'only' });
    expect(only.count).toMatchObject({ newCount: HIDDEN_COUNT, newHidden: HIDDEN_COUNT, normalLeftOut: before.count.newCount + before.count.cloudOnly, cloudOnly: 0 });

    await client.call('photos.backup', { deviceId: IPHONE_ID });
    const items = lastItems(handle!.backend);
    expect(items).toHaveLength(HIDDEN_COUNT);
    expect(items.every((name) => /^Hidden\\\d{4}\\\d{2}\\IMG_H\d{4}\.(HEIC|MOV)$/.test(name))).toBe(true);
  });

  it('puts them with the others when asked, under a renamed folder otherwise, and checks the name', async () => {
    const { client, backend } = setup();
    await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, hidden: 'include', hiddenLayout: 'together' });
    await client.call('photos.backup', { deviceId: IPHONE_ID });
    expect(lastItems(backend).some((name) => name.startsWith('Hidden'))).toBe(false);
    expect(lastItems(backend).some((name) => /IMG_H\d{4}/.test(name))).toBe(true);

    const renamed = await client.call('photos.backupConfigure', { deviceId: IPHONE_ID, hiddenLayout: 'separate', hiddenFolder: ' پنهان ' });
    expect(renamed.options.hiddenFolder).toBe('پنهان');
    for (const bad of ['a/b', 'CON', '2026', '']) {
      await expect(client.call('photos.backupConfigure', { deviceId: IPHONE_ID, hiddenFolder: bad })).rejects.toMatchObject({ rpcCode: -32602 });
    }
    await expect(client.call('photos.backupConfigure', { deviceId: IPHONE_ID, hidden: 'some' as never })).rejects.toMatchObject({ rpcCode: -32602 });
  });

  it('takes the Settings defaults for an iPhone and never for an Android phone', async () => {
    const { client } = setup();
    await expect(client.call('settings.set', { photoBackupHiddenFolder: 'a/b' })).rejects.toMatchObject({ rpcCode: -32602 });
    await expect(client.call('settings.set', { photoBackupHidden: 'all' as never })).rejects.toMatchObject({ rpcCode: -32602 });
    const settings = await client.call('settings.set', { photoBackupHidden: 'only', photoBackupHiddenFolder: 'Private' });
    expect(settings).toMatchObject({ photoBackupHidden: 'only', photoBackupHiddenLayout: 'separate', photoBackupHiddenFolder: 'Private' });

    const iphone = await client.call('photos.backupStatus', { deviceId: IPHONE_ID });
    expect(iphone.options).toMatchObject({ hidden: 'only', hiddenFolder: 'Private' });
    expect(iphone.customized).toBe(false);
    const android = await client.call('photos.backupStatus', { deviceId: ANDROID_ID });
    expect(android.options.hidden).toBe('exclude');
    expect(android.count.newCount).toBe(46);
  });
});
