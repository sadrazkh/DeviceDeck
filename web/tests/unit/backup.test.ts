import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { isBridgeError } from '../../src/bridge/errors';
import { MOCK_BACKUP_PASSWORD } from '../../src/bridge/mock/backup';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import type { BackupJob, DeviceSummary } from '../../src/bridge/types';
import { backupEn, backupFa } from '../../src/i18n/backup';
import { nameMatches } from '../../src/screens/backup/RestoreDialog';

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const jobs: BackupJob[] = [];
  const devices: DeviceSummary[] = [];
  client.on('backup.updated', (job) => jobs.push(job));
  client.on('device.changed', (device) => devices.push(device));
  return { client, backend: handle.backend, jobs, devices };
}

function run(backend: { tick(ms: number): void }, ms: number, step = 250) {
  for (let t = 0; t < ms; t += step) backend.tick(step);
}

async function failure(promise: Promise<unknown>): Promise<{ code: string; rpcCode: number }> {
  try {
    await promise;
  } catch (error) {
    if (isBridgeError(error)) return { code: error.code, rpcCode: error.rpcCode };
    throw error;
  }
  throw new Error('expected the call to fail');
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('mock backups', () => {
  it('lists the sample backups newest first, with iTunes’ encrypted one locked', async () => {
    const { client } = setup();
    const list = await client.call('backup.list', {});
    expect(list.map((b) => [b.deviceName, b.state, b.source])).toEqual([
      ['Galaxy S25 Ultra', 'incomplete', 'deviceDeck'],
      ['Nima’s iPhone', 'complete', 'deviceDeck'],
      ['Galaxy S25 Ultra', 'complete', 'deviceDeck'],
      ['Nima’s iPhone', 'complete', 'deviceDeck'],
      ['Nima’s iPhone', 'complete', 'deviceDeck'],
      ['iPhone 12 (old)', 'complete', 'apple'],
    ]);
    expect(list.at(-1)).toMatchObject({ encrypted: true, locked: true });
    const mine = await client.call('backup.list', { deviceId: IPHONE_ID });
    expect(mine).toHaveLength(3);
    const device = await client.call('backup.device', { deviceId: ANDROID_ID });
    expect(device).toMatchObject({ platform: 'android', needsHelperApp: true, encrypted: null, keep: 3, keepsHistory: true });
  });

  it('runs a backup through its phases, lists it, and offers the oldest beyond the keep count', async () => {
    const { client, backend, jobs } = setup();
    const job = await client.call('backup.start', { deviceId: IPHONE_ID });
    expect(job).toMatchObject({ kind: 'backup', state: 'running', phase: 'starting' });
    expect(await failure(client.call('backup.start', { deviceId: IPHONE_ID }))).toMatchObject({ code: 'busy' });

    run(backend, 20_000);
    const phases = [...new Set(jobs.map((j) => j.phase).filter(Boolean))];
    expect(phases).toEqual(['starting', 'preparing', 'transferring', 'finishing']);
    const done = jobs.at(-1)!;
    expect(done).toMatchObject({ state: 'done', percent: 100 });
    expect(done.prune?.backups).toHaveLength(1);
    expect(done.prune!.freedBytes).toBeGreaterThan(0);

    const mine = await client.call('backup.list', { deviceId: IPHONE_ID });
    expect(mine).toHaveLength(4);
    expect(mine[0]!.id).toBe(done.backupId);

    const deleted = await client.call('backup.delete', { backupIds: done.prune!.backups.map((b) => b.id) });
    expect(deleted.deleted).toBe(1);
    expect(await client.call('backup.list', { deviceId: IPHONE_ID })).toHaveLength(3);
  });

  it('waits for an unplugged phone and finishes when it is back', async () => {
    const { client, backend, jobs } = setup();
    await client.call('backup.start', { deviceId: ANDROID_ID });
    run(backend, 4000);
    backend.unplug(ANDROID_ID);
    run(backend, 1000);
    expect(jobs.at(-1)!.state).toBe('waitingForDevice');
    const during = await client.call('backup.list', { deviceId: ANDROID_ID });
    expect(during.some((b) => b.state === 'inProgress')).toBe(true);

    backend.plugBack(ANDROID_ID);
    run(backend, 20_000);
    expect(jobs.at(-1)!.state).toBe('done');
    const after = await client.call('backup.list', { deviceId: ANDROID_ID });
    expect(after.map((b) => b.state)).toEqual(['complete', 'complete']);
  });

  it('restores only with the phone’s name typed, then the phone restarts', async () => {
    const { client, backend, jobs, devices } = setup();
    const [backup] = await client.call('backup.list', { deviceId: IPHONE_ID });
    expect(await failure(client.call('backup.restore', { deviceId: IPHONE_ID, backupId: backup!.id, confirmName: 'iPhone' }))).toMatchObject({ rpcCode: -32602 });
    expect(await failure(client.call('backup.restore', { deviceId: ANDROID_ID, backupId: backup!.id, confirmName: 'Galaxy S25 Ultra' }))).toMatchObject({ code: 'not_supported' });

    await client.call('backup.restore', { deviceId: IPHONE_ID, backupId: backup!.id, confirmName: '  nima’s iphone ' });
    run(backend, 15_000);
    expect(jobs.at(-1)).toMatchObject({ kind: 'restore', state: 'done' });
    run(backend, 5_000);
    expect(devices.map((d) => d.state)).toEqual(expect.arrayContaining(['disconnected', 'ready']));
  });

  it('turns encryption on and refuses to turn it off with the wrong password', async () => {
    const { client, backend, jobs } = setup();
    await client.call('backup.setEncryption', { deviceId: IPHONE_ID, enable: true, password: 'pw' });
    expect(jobs.at(-1)!.phase).toBe('starting');
    run(backend, 4000);
    expect(jobs.at(-1)).toMatchObject({ kind: 'encryption', state: 'done' });
    expect((await client.call('backup.device', { deviceId: IPHONE_ID })).encrypted).toBe(true);

    await client.call('backup.setEncryption', { deviceId: IPHONE_ID, enable: false, oldPassword: 'nope' });
    run(backend, 4000);
    expect(jobs.at(-1)).toMatchObject({ state: 'failed', error: { code: 'wrong_password' } });
    expect(await failure(client.call('backup.setEncryption', { deviceId: IPHONE_ID, enable: true }))).toMatchObject({ rpcCode: -32602 });
  });

  it('opens an encrypted backup with its password and reads its contacts and conversations', async () => {
    const { client } = setup();
    const list = await client.call('backup.list', {});
    const old = list.find((b) => b.source === 'apple')!;
    expect(await failure(client.call('contacts.list', { backupId: old.id }))).toMatchObject({ code: 'password_required' });
    expect(await failure(client.call('backup.unlock', { backupId: old.id, password: 'guess' }))).toMatchObject({ code: 'wrong_password' });
    await client.call('backup.unlock', { backupId: old.id, password: MOCK_BACKUP_PASSWORD });
    expect((await client.call('backup.list', {})).find((b) => b.id === old.id)!.locked).toBe(false);

    const contacts = await client.call('contacts.list', { backupId: old.id });
    expect(contacts.length).toBeGreaterThan(10);
    const conversations = await client.call('messages.conversations', { backupId: old.id });
    const first = conversations.reduce((a, b) => (b.messageCount > a.messageCount ? b : a));
    const page = await client.call('messages.list', { backupId: old.id, conversationId: first.id, offset: first.messageCount - 10, limit: 10 });
    expect(page.total).toBe(first.messageCount);
    expect(page.items).toHaveLength(10);
    expect(await failure(client.call('messages.list', { backupId: old.id, conversationId: first.id, offset: 0, limit: 5000 }))).toMatchObject({ rpcCode: -32602 });

    await client.call('backup.lock', { backupId: old.id });
    expect(await failure(client.call('backup.domains', { backupId: old.id }))).toMatchObject({ code: 'password_required' });
  });

  it('browses domains and folders, and explains that Android backups hold no contacts', async () => {
    const { client } = setup();
    const list = await client.call('backup.list', {});
    const iphone = list.find((b) => b.deviceId === IPHONE_ID)!;
    const domains = await client.call('backup.domains', { backupId: iphone.id });
    expect(domains.find((d) => d.kind === 'appGroup')).toMatchObject({ appId: 'net.whatsapp.WhatsApp', name: 'WhatsApp' });
    const top = await client.call('backup.files', { backupId: iphone.id, domain: 'HomeDomain', path: '' });
    expect(top.map((e) => e.name)).toEqual(['Library']);
    const library = await client.call('backup.files', { backupId: iphone.id, domain: 'HomeDomain', path: 'Library' });
    expect(library.every((e) => e.isDirectory)).toBe(true);

    const galaxy = list.find((b) => b.deviceId === ANDROID_ID && b.state === 'complete')!;
    expect((await client.call('backup.domains', { backupId: galaxy.id })).map((d) => d.domain)).toEqual(['apps', 'media']);
    expect(await failure(client.call('contacts.list', { backupId: galaxy.id }))).toMatchObject({ code: 'not_supported' });
    const unfinished = list.find((b) => b.state === 'incomplete')!;
    expect(await failure(client.call('backup.domains', { backupId: unfinished.id }))).toMatchObject({ code: 'backup_corrupt' });
    expect(await failure(client.call('backup.delete', { backupIds: [list.find((b) => b.source === 'apple')!.id] }))).toMatchObject({ code: 'permission_denied' });
  });

  it('exports contacts to a file named after the phone and date', async () => {
    const { client, backend, jobs } = setup();
    const [backup] = await client.call('backup.list', { deviceId: IPHONE_ID });
    const job = await client.call('contacts.export', { backupId: backup!.id, format: 'csv', destination: 'D:\\Out' });
    expect(job.destination).toMatch(/^D:\\Out\\Contacts - Nima’s iPhone - \d{4}-\d{2}-\d{2}\.csv$/);
    run(backend, 2000);
    expect(jobs.at(-1)).toMatchObject({ kind: 'export', state: 'done' });
  });
});

describe('restore confirmation', () => {
  it('matches the typed name like the host does', () => {
    expect(nameMatches('  Nima’s iPhone ', 'Nima’s iPhone')).toBe(true);
    expect(nameMatches('nima’s iphone', 'Nima’s iPhone')).toBe(true);
    expect(nameMatches('Nima', 'Nima’s iPhone')).toBe(false);
    expect(nameMatches('', 'Nima’s iPhone')).toBe(false);
  });
});

describe('backup strings', () => {
  it('has Persian for every English key', () => {
    expect(Object.keys(backupFa).sort()).toEqual(Object.keys(backupEn).sort());
  });
});
