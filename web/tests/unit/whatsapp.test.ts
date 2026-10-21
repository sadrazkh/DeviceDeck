import { afterEach, describe, expect, it } from 'vitest';
import { BridgeClient } from '../../src/bridge/client';
import { isBridgeError } from '../../src/bridge/errors';
import { MOCK_BACKUP_PASSWORD } from '../../src/bridge/mock/backup';
import { ANDROID_ID, IPHONE_ID } from '../../src/bridge/mock/mock-data';
import { createMockTransport, type MockHandle } from '../../src/bridge/mock/transport';
import type { BackupJob, TransferJob } from '../../src/bridge/types';
import { whatsAppEn, whatsAppFa } from '../../src/screens/contacts/whatsAppStrings';

// The mock backend's WhatsApp (round 4): same shapes and rules as docs/bridge-contract.md, "WhatsApp (round 4)".

let handle: MockHandle | null = null;

function setup() {
  handle = createMockTransport({ latencyMs: 0, autoTick: false, indexing: false, seedQueue: false });
  const client = new BridgeClient(handle.transport, { timeoutMs: 0 });
  const jobs: BackupJob[] = [];
  const transfers: TransferJob[] = [];
  client.on('backup.updated', (job) => jobs.push(job));
  client.on('transfer.updated', (job) => transfers.push(job));
  return { client, backend: handle.backend, jobs, transfers };
}

function run(backend: { tick(ms: number): void }, ms: number, step = 250) {
  for (let t = 0; t < ms; t += step) backend.tick(step);
}

async function failure(promise: Promise<unknown>): Promise<string> {
  try {
    await promise;
  } catch (error) {
    if (isBridgeError(error)) return error.code;
    throw error;
  }
  throw new Error('expected the call to fail');
}

afterEach(() => {
  handle?.dispose();
  handle = null;
});

describe('mock WhatsApp', () => {
  it('reads chats and pages messages from the sample iPhone’s backups', async () => {
    const { client } = setup();
    const backup = (await client.call('backup.list', { deviceId: IPHONE_ID }))[0]!;

    const status = await client.call('whatsapp.status', { backupId: backup.id });
    expect(status).toMatchObject({ found: true, installed: true, missing: [] });
    expect(status.accounts.map((a) => a.account)).toEqual(['whatsApp', 'business']);

    const chats = await client.call('whatsapp.chats', { backupId: backup.id });
    expect(chats).toHaveLength(7);
    expect(chats.map((c) => c.lastMessageAt)).toEqual([...chats.map((c) => c.lastMessageAt)].sort().reverse());
    const family = chats.find((c) => c.title === 'خانواده 🏡')!;
    expect(family).toMatchObject({ isGroup: true, phone: null, messageCount: 321 });

    const last = await client.call('whatsapp.messages', { backupId: backup.id, chatId: family.id, offset: 121, limit: 200 });
    const first = await client.call('whatsapp.messages', { backupId: backup.id, chatId: family.id, offset: 0, limit: 121 });
    expect(last.total).toBe(321);
    expect(new Set([...first.items, ...last.items].map((m) => m.id)).size).toBe(321);
    expect(first.items[0]).toMatchObject({ kind: 'event', text: 'خانواده 🏡' });
    expect(await failure(client.call('whatsapp.messages', { backupId: backup.id, chatId: family.id, offset: 0, limit: 5000 }))).toBe('invalid_params');

    const media = [...first.items, ...last.items].map((m) => m.media).filter((m) => m !== null);
    const picture = media.find((m) => m.kind === 'image' && m.hasThumbnail)!;
    expect((await client.call('whatsapp.thumbnail', { backupId: backup.id, mediaId: picture.id })).dataUrl).toMatch(/^data:image\//);
    const voice = media.find((m) => m.kind === 'voice' && m.inBackup)!;
    expect((await client.call('whatsapp.media', { backupId: backup.id, mediaId: voice.id })).dataUrl).toMatch(/^data:audio\/wav;base64,UklGR/);
    const gone = media.find((m) => !m.inBackup && m.kind !== 'location')!;
    expect(await failure(client.call('whatsapp.media', { backupId: backup.id, mediaId: gone.id }))).toBe('backup_missing');
  });

  it('says what an older WhatsApp lacks, and when a backup has none', async () => {
    const { client } = setup();
    const backups = await client.call('backup.list', {});
    const oldest = backups.filter((b) => b.deviceId === IPHONE_ID).at(-1)!;
    expect((await client.call('whatsapp.status', { backupId: oldest.id })).missing).toContain('ZWAMESSAGE.ZSTARRED');

    const old = backups.find((b) => b.source === 'apple')!;
    expect(await failure(client.call('whatsapp.status', { backupId: old.id }))).toBe('password_required');
    await client.call('backup.unlock', { backupId: old.id, password: MOCK_BACKUP_PASSWORD });
    expect(await client.call('whatsapp.status', { backupId: old.id })).toMatchObject({ found: false, installed: false, accounts: [] });
    expect(await client.call('whatsapp.chats', { backupId: old.id })).toEqual([]);

    const android = backups.find((b) => b.platform === 'android' && b.state === 'complete')!;
    expect(await failure(client.call('whatsapp.chats', { backupId: android.id }))).toBe('not_supported');
  });

  it('exports chats and media as backup jobs', async () => {
    const { client, backend, jobs } = setup();
    const backup = (await client.call('backup.list', { deviceId: IPHONE_ID }))[0]!;
    const chats = await client.call('whatsapp.chats', { backupId: backup.id });

    const one = await client.call('whatsapp.export', { backupId: backup.id, chatIds: [chats[0]!.id], format: 'html', destination: 'D:\\Chats' });
    expect(one).toMatchObject({ kind: 'export', destination: `D:\\Chats\\${chats[0]!.title}.html`, title: `Saving a WhatsApp chat from the backup of ${backup.deviceName}` });
    const all = await client.call('whatsapp.export', { backupId: backup.id, format: 'pdf', destination: 'D:\\Chats' });
    expect(all.destination).toMatch(/^D:\\Chats\\WhatsApp - Nima’s iPhone - \d{4}-\d{2}-\d{2}$/);
    const media = await client.call('whatsapp.exportMedia', { backupId: backup.id, destination: 'D:\\Chats' });
    expect(media.title).toBe(`Saving WhatsApp media from the backup of ${backup.deviceName}`);
    run(backend, 4000);
    expect(jobs.filter((j) => j.state === 'done').map((j) => j.id).sort()).toEqual([one.id, all.id, media.id].sort());

    expect(await failure(client.call('whatsapp.export', { backupId: backup.id, format: 'html', destination: 'relative' }))).toBe('invalid_params');
  });

  it('copies WhatsApp’s folders off the Android phone through the transfer engine', async () => {
    const { client, backend, transfers } = setup();
    const info = await client.call('whatsapp.android', { deviceId: ANDROID_ID });
    expect(info.apps).toHaveLength(1);
    expect(info.apps[0]).toMatchObject({ package: 'com.whatsapp', hasFolder: true, installed: true, mediaFiles: 4 });
    expect(info.apps[0]!.chatBackups[0]!.name).toBe('msgstore.db.crypt14');

    const started = await client.call('whatsapp.backupAndroid', { deviceId: ANDROID_ID, destination: 'D:\\Phone' });
    expect(started.folder).toMatch(/^D:\\Phone\\WhatsApp - Galaxy S25 Ultra - \d{4}-\d{2}-\d{2}$/);
    const again = await client.call('whatsapp.backupAndroid', { deviceId: ANDROID_ID, destination: 'D:\\Phone' });
    expect(again.folder).toBe(`${started.folder} (1)`);
    run(backend, 10_000);
    const job = transfers.filter((j) => j.id === started.jobId).at(-1)!;
    expect(job).toMatchObject({ kind: 'whatsAppMedia', state: 'done', totalItems: 7 });

    expect(await failure(client.call('whatsapp.android', { deviceId: IPHONE_ID }))).toBe('not_supported');
  });

  it('has every string in Persian too', () => {
    expect(Object.keys(whatsAppFa).sort()).toEqual(Object.keys(whatsAppEn).sort());
    for (const value of Object.values(whatsAppFa)) expect(value).not.toMatch(/[A-Za-z]{4,} [a-z]{3,} [a-z]{3,}/);
  });
});
