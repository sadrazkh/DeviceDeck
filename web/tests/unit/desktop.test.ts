// Round 2, desktop: device.rename, transfers.retry / clear, this PC, notifications and updates in the mock,
// and the PC path helpers.

import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { METHOD_NAMES, EVENT_NAMES } from '../../src/bridge/methods';
import { MockBackend } from '../../src/bridge/mock/backend';
import { IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import { pcJoin, pcName, pcParent, rootOf } from '../../src/shell/pc/pcQueries';
import type { PcRoot } from '../../src/bridge/types';

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false });
  return { client: new BridgeClient(handle.transport, { timeoutMs: 0 }), backend: handle.backend };
}

function run(backend: MockBackend, ms: number, step = 250) {
  for (let t = 0; t < ms; t += step) backend.tick(step);
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

const DESKTOP_METHODS = [
  'device.rename',
  'transfers.retry',
  'transfers.clear',
  'notifications.show',
  'pc.roots',
  'pc.list',
  'pc.mkdir',
  'pc.rename',
  'shell.openFile',
  'shell.dragOut',
  'app.updateStatus',
  'app.checkForUpdates',
  'app.applyUpdate',
] as const;

describe('desktop methods in the mock', () => {
  it('are all in the contract and implemented', () => {
    const backend = new MockBackend(() => {});
    for (const method of DESKTOP_METHODS) {
      expect(METHOD_NAMES, method).toContain(method);
      expect(typeof backend.handlers[method], method).toBe('function');
    }
    expect(EVENT_NAMES).toEqual(expect.arrayContaining(['notification.activated', 'app.updateChanged']));
  });

  it('renames a phone and announces it', async () => {
    const { client } = setup();
    const changed: string[] = [];
    client.on('device.changed', (d) => changed.push(d.name));
    const renamed = await client.call('device.rename', { deviceId: IPHONE_ID, name: '  Sara’s iPhone ' });
    expect(renamed.name).toBe('Sara’s iPhone');
    expect(changed).toContain('Sara’s iPhone');
    expect((await client.call('device.list')).find((d) => d.id === IPHONE_ID)!.name).toBe('Sara’s iPhone');
    await expect(client.call('device.rename', { deviceId: IPHONE_ID, name: ' ' })).rejects.toMatchObject({ rpcCode: -32602 });
  });

  it('retries a cancelled copy and clears finished ones', async () => {
    const { client, backend } = setup();
    const { jobId } = await client.call('files.copyToPc', { deviceId: IPHONE_ID, root: 'media', paths: ['/DCIM'], destination: 'D:\\Out' });
    await client.call('transfers.cancel', { jobId });
    expect((await client.call('transfers.list')).find((j) => j.id === jobId)!.state).toBe('cancelled');

    await client.call('transfers.retry', { jobId });
    run(backend, 120_000);
    expect((await client.call('transfers.list')).find((j) => j.id === jobId)!.state).toBe('done');
    await expect(client.call('transfers.retry', { jobId })).rejects.toMatchObject({ code: 'internal' });

    const before = await client.call('transfers.list');
    const finished = before.filter((j) => ['done', 'failed', 'cancelled'].includes(j.state)).length;
    const { cleared } = await client.call('transfers.clear', {});
    expect(cleared).toBe(finished);
    expect((await client.call('transfers.list')).every((j) => !['done', 'failed', 'cancelled'].includes(j.state))).toBe(true);
  });

  it('browses this PC, makes and renames folders', async () => {
    const { client } = setup();
    const roots = await client.call('pc.roots');
    expect(roots.map((r) => r.kind)).toEqual(expect.arrayContaining(['home', 'downloads', 'drive']));

    const home = roots.find((r) => r.kind === 'home')!.path;
    const listing = await client.call('pc.list', { path: home });
    expect(listing.map((e) => e.name)).toEqual(expect.arrayContaining(['Desktop', 'Downloads', 'Pictures']));
    expect(listing.some((e) => e.hidden)).toBe(false);
    expect((await client.call('pc.list', { path: home, showHidden: true })).some((e) => e.hidden)).toBe(true);

    const made = await client.call('pc.mkdir', { path: `${home}\\Phone photos` });
    expect(made).toMatchObject({ name: 'Phone photos', isDirectory: true, path: `${home}\\Phone photos` });
    await expect(client.call('pc.mkdir', { path: `${home}\\Phone photos` })).rejects.toMatchObject({ code: 'already_exists' });
    const renamed = await client.call('pc.rename', { path: made.path, name: 'iPhone' });
    expect(renamed.path).toBe(`${home}\\iPhone`);
    await expect(client.call('pc.list', { path: 'C:\\Nope' })).rejects.toMatchObject({ code: 'path_not_found' });
    expect((await client.call('pc.list', { path: 'C:\\' })).map((e) => e.path)).toContain('C:\\Users');
  });

  it('records notifications and checks for updates', async () => {
    const { client, backend } = setup();
    await client.call('notifications.show', { title: '12 new photos', buttons: [{ label: 'Back up', command: 'photos.backup' }] });
    expect(backend.notifications).toHaveLength(1);
    await expect(client.call('notifications.show', { title: '' })).rejects.toMatchObject({ rpcCode: -32602 });

    const updates: string[] = [];
    client.on('app.updateChanged', (s) => updates.push(s.state));
    expect((await client.call('app.updateStatus')).state).toBe('idle');
    expect((await client.call('app.checkForUpdates')).state).toBe('upToDate');
    expect(updates).toEqual(['upToDate']);
    expect(await client.call('shell.dragOut', { source: 'pc', paths: ['C:\\a.txt'] })).toEqual({ effect: 'none', droppedInside: null });
  });
});

describe('PC paths', () => {
  it('go up, join and name like Explorer', () => {
    expect(pcParent('C:\\Users\\Nima')).toBe('C:\\Users');
    expect(pcParent('C:\\Users')).toBe('C:\\');
    expect(pcParent('C:\\')).toBeNull();
    expect(pcParent('\\\\nas\\share')).toBeNull();
    expect(pcParent('\\\\nas\\share\\photos')).toBe('\\\\nas\\share');
    expect(pcJoin('C:\\', 'Users')).toBe('C:\\Users');
    expect(pcJoin('C:\\Users', 'Nima')).toBe('C:\\Users\\Nima');
    expect(pcName('C:\\Users\\Nima\\')).toBe('Nima');
    expect(pcName('D:\\')).toBe('D:');
  });

  it('find the closest known folder', () => {
    const roots: PcRoot[] = [
      { path: 'C:\\Users\\Nima', label: 'Nima', kind: 'home', totalBytes: null, freeBytes: null },
      { path: 'C:\\Users\\Nima\\Pictures', label: 'Pictures', kind: 'pictures', totalBytes: null, freeBytes: null },
      { path: 'C:\\', label: '', kind: 'drive', totalBytes: 1, freeBytes: 1 },
    ];
    expect(rootOf('C:\\Users\\Nima\\Pictures\\2026', roots)?.kind).toBe('pictures');
    expect(rootOf('c:\\users\\nima\\Desktop', roots)?.kind).toBe('home');
    expect(rootOf('C:\\Windows', roots)?.kind).toBe('drive');
    expect(rootOf('D:\\x', roots)).toBeUndefined();
  });
});
