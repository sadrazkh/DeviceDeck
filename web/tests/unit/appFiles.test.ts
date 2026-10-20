import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { ANDROID_ID, IPHONE_ID, KB, MB } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { matchesSearch } from '../../src/screens/appDocuments/AppDocumentsScreen';
import { copiesInto } from '../../src/screens/appDocuments/AppGrid';
import { matchesName } from '../../src/screens/files/fileKinds';
import type { AppPlace } from '../../src/screens/appDocuments/appFiles';
import type { TransferJob } from '../../src/bridge/types';

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, seedQueue: false });
  return { client: new BridgeClient(handle.transport, { timeoutMs: 0 }), backend: handle.backend };
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('app files in the mock backend', () => {
  it('lists the iPhone apps with File Sharing, each with its Documents root', async () => {
    const { client } = setup();
    const overview = await client.call('appFiles.list', { deviceId: IPHONE_ID });
    expect(overview.privateData).toBe('notApplicable');
    expect(overview.entries.map((e) => e.name)).toEqual(['Documents', 'GoodReader', 'Infuse', 'VLC']);
    expect(overview.entries.find((e) => e.name === 'VLC')).toEqual({
      id: 'org.videolan.vlc-ios',
      name: 'VLC',
      kind: 'app',
      appId: 'org.videolan.vlc-ios',
      folders: [{ rootKey: 'app:org.videolan.vlc-ios', label: 'Documents', empty: false }],
    });
  });

  it('lists Android app folders by package, then Documents and Download, and says Android/data is closed', async () => {
    const { client } = setup();
    const overview = await client.call('appFiles.list', { deviceId: ANDROID_ID });
    expect(overview.privateData).toBe('blocked');
    expect(overview.entries.map((e) => [e.id, e.kind, e.folders.map((f) => [f.rootKey, f.empty])])).toEqual([
      ['com.spotify.music', 'app', [['appmedia:com.spotify.music', true]]],
      ['com.whatsapp', 'app', [['appmedia:com.whatsapp', false]]],
      ['org.telegram.messenger', 'app', [['appmedia:org.telegram.messenger', false]]],
      ['shared:Documents', 'sharedFolder', [['shared:Documents', false]]],
      ['shared:Download', 'sharedFolder', [['shared:Download', false]]],
    ]);
    // Names are package names, as the host sends them; the page labels them from apps.list.
    expect(overview.entries[1]!.name).toBe('com.whatsapp');

    // Each folder is a root of its own.
    const telegram = await client.call('files.list', { deviceId: ANDROID_ID, root: 'appmedia:org.telegram.messenger', path: '/Telegram' });
    expect(telegram.map((e) => e.name).sort()).toEqual(['Telegram Documents', 'Telegram Video']);
  });

  it('measures what is under each path, counting nested paths once in the totals', async () => {
    const { client } = setup();
    const usage = await client.call('appFiles.measure', { deviceId: IPHONE_ID, root: 'app:org.videolan.vlc-ios', paths: ['/', '/Subtitles', '/nope'] });
    expect(usage.items.map((i) => [i.path, i.files, i.folders])).toEqual([
      ['/', 4, 1],
      ['/Subtitles', 2, 0],
      ['/nope', 0, 0],
    ]);
    expect(usage.files).toBe(4);
    expect(usage.bytes).toBe(usage.items[0]!.bytes);
    expect(usage.items[1]!.bytes).toBe(Math.round(92 * KB) + Math.round(108 * KB));

    const android = await client.call('appFiles.measure', { deviceId: ANDROID_ID, root: 'appmedia:org.telegram.messenger', paths: ['/'] });
    expect(android).toMatchObject({ files: 2, folders: 3, bytes: Math.round(6.1 * MB) + Math.round(48 * MB) });
    await expect(client.call('appFiles.measure', { deviceId: IPHONE_ID, root: 'app:org.videolan.vlc-ios', paths: [] })).rejects.toMatchObject({ code: 'invalid_params' });
  });

  it('copies into an Android shared folder and exports a whole root under the app’s name', async () => {
    const { client, backend } = setup();
    const file = backend.registerLocalFile('Lecture 8.pdf', 2 * MB);
    const { jobId } = await client.call('files.copyToDevice', { deviceId: ANDROID_ID, root: 'shared:Download', files: [file], path: '/' });
    const jobs = await client.call('transfers.list');
    expect(jobs.find((j) => j.id === jobId)!.title).toBe('Lecture 8.pdf into Download');
    for (let i = 0; i < 40; i++) backend.tick(250);
    const listing = await client.call('files.list', { deviceId: ANDROID_ID, root: 'shared:Download', path: '/' });
    expect(listing.map((e) => e.name)).toContain('Lecture 8.pdf');

    const whole = await client.call('files.copyToPc', { deviceId: IPHONE_ID, root: 'app:org.videolan.vlc-ios', paths: ['/'], destination: 'D:\\Backup' });
    expect((await client.call('transfers.list')).find((j) => j.id === whole.jobId)!.title).toBe('Copying VLC to D:\\Backup');
  });
});

describe('app files on the page', () => {
  const place = (id: string, label: string, folders: string[]): AppPlace => ({
    id,
    name: id,
    label,
    labelPending: false,
    kind: 'app',
    appId: id,
    folders: folders.map((rootKey) => ({ rootKey, label: rootKey.startsWith('appdata:') ? 'Data' : 'Media', empty: false })),
  });

  it('searches apps by label or id, ignoring case', () => {
    const telegram = place('org.telegram.messenger', 'Telegram', ['appmedia:org.telegram.messenger']);
    expect(matchesSearch(telegram, 'tele')).toBe(true);
    expect(matchesSearch(telegram, 'MESSENGER')).toBe(true);
    expect(matchesSearch(telegram, '')).toBe(true);
    expect(matchesSearch(telegram, 'vlc')).toBe(false);
  });

  it('searches a folder by every word, with Persian digits and Arabic letters', () => {
    expect(matchesName('Season 2 Episode 10.mkv', 'episode 10')).toBe(true);
    expect(matchesName('Season 2 Episode 10.mkv', 'season ۲')).toBe(true);
    expect(matchesName('فیلم کوتاه.mp4', 'كوتاه')).toBe(true);
    expect(matchesName('فیلم کوتاه.mp4', 'بلند')).toBe(false);
  });

  it('shows a copy on the card of the app it goes into', () => {
    const whatsapp = place('com.whatsapp', 'WhatsApp', ['appmedia:com.whatsapp', 'appdata:com.whatsapp']);
    const job = (id: string, title: string, state: TransferJob['state'] = 'running'): TransferJob => ({
      id, deviceId: ANDROID_ID, kind: 'copyToDevice', title, state, totalItems: 1, doneItems: 0, failedItems: 0, totalBytes: 1, doneBytes: 0,
      bytesPerSecond: null, etaSeconds: null, conflict: null, error: null, createdAt: '2026-10-20T10:00:00Z', finishedAt: null,
    });
    const jobs = [job('a', 'x into Elsewhere'), job('b', 'y into Other'), job('c', 'z into com.whatsapp (Data)'), job('d', 'w into com.whatsapp', 'done')];
    expect(copiesInto(jobs, ANDROID_ID, whatsapp, { a: 'appdata:com.whatsapp' }).map((j) => j.id)).toEqual(['a', 'c']);
  });
});
