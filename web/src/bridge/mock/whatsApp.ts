// The mock backend's WhatsApp (round 4): made-up chats in the sample iPhone's backups (none in the old iPhone's),
// exports that run as backup jobs, and WhatsApp's folders on the sample Android phone, copied through the transfer
// engine. Same methods and shapes as docs/bridge-contract.md, "WhatsApp (round 4)".

import type { MethodParams, MethodResult } from '../methods';
import type {
  AppInfo,
  BackupInfo,
  BackupJob,
  DeviceCapability,
  DeviceSummary,
  TransferJob,
  WhatsAppAndroidApp,
  WhatsAppChat,
  WhatsAppMedia,
  WhatsAppMediaKind,
  WhatsAppMessage,
} from '../types';
import type { MockFileSystem } from './files';
import { thumbnailUrl } from './images';
import { IPHONE_ID, KB, MB } from './mock-data';
import type { MockAsset } from './photos';
import { createRandom, hashString } from './random';
import type { JobItem, NewJob } from './transfers';

export type WhatsAppMethodName =
  | 'whatsapp.status'
  | 'whatsapp.chats'
  | 'whatsapp.messages'
  | 'whatsapp.thumbnail'
  | 'whatsapp.media'
  | 'whatsapp.export'
  | 'whatsapp.exportMedia'
  | 'whatsapp.android'
  | 'whatsapp.backupAndroid';

type Handler<M extends WhatsAppMethodName> = (params: MethodParams<M>) => MethodResult<M>;

export interface MockWhatsAppContext {
  /** Every backup, as backup.list shows it (locked ones included). */
  backups(): BackupInfo[];
  /** Runs an export as a backup job (backup.updated events, like messages.export). */
  startExport(backup: BackupInfo, title: string, destination: string, files: number, bytes: number, ms: number): BackupJob;
  requireReady(deviceId: string, capability: DeviceCapability): DeviceSummary;
  fs(deviceId: string): MockFileSystem;
  apps(deviceId: string): AppInfo[];
  addJob(spec: NewJob): TransferJob;
  now(): number;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

interface ChatData {
  chat: WhatsAppChat;
  messages: WhatsAppMessage[];
}

const ANDROID_APPS: { package: WhatsAppAndroidApp['package']; account: WhatsAppAndroidApp['account']; name: string }[] = [
  { package: 'com.whatsapp', account: 'whatsApp', name: 'WhatsApp' },
  { package: 'com.whatsapp.w4b', account: 'business', name: 'WhatsApp Business' },
];

export class MockWhatsApp {
  private readonly used = new Set<string>();

  constructor(private readonly ctx: MockWhatsAppContext) {}

  readonly handlers: { [M in WhatsAppMethodName]: Handler<M> } = {
    'whatsapp.status': ({ backupId }) => {
      const backup = this.open(backupId);
      const chats = chatsOf(backup);
      if (!chats) return { backupId, found: false, installed: false, accounts: [], missing: [] };
      const accounts = (['whatsApp', 'business'] as const)
        .map((account) => {
          const mine = chats.filter((c) => c.chat.account === account);
          const media = mine.flatMap((c) => c.messages.map((m) => m.media).filter((x): x is WhatsAppMedia => hasFile(x)));
          return {
            account,
            name: account === 'business' ? 'WhatsApp Business' : 'WhatsApp',
            chats: mine.length,
            messages: mine.reduce((n, c) => n + c.messages.length, 0),
            media: media.length,
            mediaInBackup: media.filter((m) => m.inBackup).length,
            mediaBytes: media.filter((m) => m.inBackup).reduce((n, m) => n + m.size, 0),
          };
        })
        .filter((a) => a.chats > 0);
      return { backupId, found: true, installed: true, accounts, missing: isOldest(backup, this.ctx.backups()) ? ['ZWAMESSAGE.ZSTARRED', 'ZWAPROFILEPUSHNAME'] : [] };
    },
    'whatsapp.chats': ({ backupId }) => (chatsOf(this.open(backupId)) ?? []).map((c) => c.chat),
    'whatsapp.messages': ({ backupId, chatId, offset, limit }) => {
      if (offset < 0 || limit < 1 || limit > 1000) throw this.ctx.badParams('Invalid params: offset must be 0 or more and limit between 1 and 1000.');
      const found = (chatsOf(this.open(backupId)) ?? []).find((c) => c.chat.id === chatId);
      if (!found) throw this.ctx.fault('path_not_found', 'That chat isn’t in this backup.');
      return { items: found.messages.slice(offset, offset + limit), total: found.messages.length };
    },
    'whatsapp.thumbnail': ({ backupId, mediaId }) => {
      const media = this.media(backupId, mediaId);
      if (!media?.hasThumbnail) return { dataUrl: null };
      const asset = { id: `wa-${mediaId}`, kind: media.kind === 'video' ? 'video' : 'photo', seed: hashString(mediaId), width: 1600, height: media.kind === 'sticker' ? 1600 : 1200 };
      return { dataUrl: thumbnailUrl(asset as MockAsset) };
    },
    'whatsapp.media': ({ backupId, mediaId }) => {
      const media = this.media(backupId, mediaId);
      if (!media || !media.inBackup) throw this.ctx.fault('backup_missing', 'That file isn’t in this backup.');
      if (media.size > 32 * MB) throw this.ctx.fault('not_supported', 'This file is too big to open here. Export the chat to keep it with its files.');
      return { dataUrl: tone(media.durationSeconds ?? 2), mimeType: 'audio/wav', size: media.size };
    },
    'whatsapp.export': ({ backupId, format, destination, chatIds }) => {
      const backup = this.open(backupId);
      requireFolder(destination, this.ctx);
      const all = chatsOf(backup) ?? [];
      const wanted = chatIds?.length ? all.filter((c) => chatIds.includes(c.chat.id)) : all;
      if (!wanted.length) throw this.ctx.fault('path_not_found', chatIds?.length ? 'Those chats aren’t in this backup.' : 'This backup has no WhatsApp chats.');
      const count = chatIds?.length ?? 0;
      const title =
        count === 0 ? `Saving all WhatsApp chats from the backup of ${backup.deviceName}`
        : count === 1 ? `Saving a WhatsApp chat from the backup of ${backup.deviceName}`
        : `Saving ${count} WhatsApp chats from the backup of ${backup.deviceName}`;
      const target = wanted.length === 1 ? `${destination}\\${wanted[0]!.chat.title}.${format === 'pdf' ? 'pdf' : 'html'}` : `${destination}\\WhatsApp - ${backup.deviceName} - ${day(backup)}`;
      const files = wanted.reduce((n, c) => n + 1 + c.chat.mediaCount, 0);
      return this.ctx.startExport(backup, title, target, files, files * 400 * KB, format === 'pdf' ? 3000 : 1800);
    },
    'whatsapp.exportMedia': ({ backupId, destination, chatIds }) => {
      const backup = this.open(backupId);
      requireFolder(destination, this.ctx);
      const wanted = (chatsOf(backup) ?? []).filter((c) => !chatIds?.length || chatIds.includes(c.chat.id));
      const media = wanted.flatMap((c) => c.messages.map((m) => m.media).filter((x): x is WhatsAppMedia => hasFile(x) && x.inBackup));
      return this.ctx.startExport(backup, `Saving WhatsApp media from the backup of ${backup.deviceName}`, `${destination}\\WhatsApp media - ${backup.deviceName} - ${day(backup)}`,
        media.length, media.reduce((n, m) => n + m.size, 0), 2500);
    },
    'whatsapp.android': ({ deviceId }) => {
      const device = this.android(deviceId);
      const fs = this.ctx.fs(device.id);
      const installed = new Set(this.ctx.apps(device.id).map((a) => a.id));
      const apps = ANDROID_APPS.flatMap<WhatsAppAndroidApp>(({ package: pkg, account, name }) => {
        const folder = [`/Android/media/${pkg}/${name}`, `/${name}`].find((f) => fs.stat('internal', f)?.isDirectory);
        if (!folder) return installed.has(pkg) ? [{ package: pkg, account, name, folder: `/Android/media/${pkg}/${name}`, hasFolder: false, installed: true, mediaFiles: null, mediaBytes: null, chatBackups: [] }] : [];
        const usage = fs.usage('internal', `${folder}/Media`);
        const databases = fs.stat('internal', `${folder}/Databases`)?.isDirectory ? fs.list('internal', `${folder}/Databases`) : [];
        return [{
          package: pkg,
          account,
          name,
          folder,
          hasFolder: true,
          installed: installed.has(pkg),
          mediaFiles: usage.files,
          mediaBytes: usage.bytes,
          chatBackups: databases
            .filter((f) => !f.isDirectory && f.name.startsWith('msgstore'))
            .map((f) => ({ name: f.name, size: f.size, modified: f.modified }))
            .sort((a, b) => (b.modified ?? '').localeCompare(a.modified ?? '')),
        }];
      });
      return { deviceId: device.id, apps };
    },
    'whatsapp.backupAndroid': ({ deviceId, destination }) => {
      const device = this.android(deviceId);
      requireFolder(destination, this.ctx);
      const fs = this.ctx.fs(device.id);
      const items: JobItem[] = [];
      for (const { package: pkg, name } of ANDROID_APPS) {
        const folder = [`/Android/media/${pkg}/${name}`, `/${name}`].find((f) => fs.stat('internal', f)?.isDirectory);
        if (!folder) continue;
        for (const part of ['Media', 'Databases', 'Backups']) walk(fs, `${folder}/${part}`, items);
      }
      const base = `${destination}\\WhatsApp - ${device.name} - ${new Date(this.ctx.now()).toISOString().slice(0, 10)}`;
      let folder = base;
      for (let n = 1; this.used.has(folder); n++) folder = `${base} (${n})`;
      this.used.add(folder);
      const job = this.ctx.addJob({
        deviceId: device.id,
        kind: 'whatsAppMedia',
        title: `Backing up WhatsApp media from ${device.name} to ${folder}`,
        items: items.length ? items : [{ name: 'WhatsApp', size: 1 }],
        failWith: items.length ? undefined : { code: 'path_not_found', message: 'WhatsApp’s folder isn’t on this phone (Android/media/com.whatsapp/WhatsApp). Open WhatsApp once, then try again.' },
      });
      return { jobId: job.id, folder };
    },
  };

  private open(backupId: string): BackupInfo {
    const backup = this.ctx.backups().find((b) => b.id === backupId);
    if (!backup) throw this.ctx.fault('path_not_found', 'That backup isn’t on this PC any more.');
    if (backup.state !== 'complete') throw this.ctx.fault('backup_corrupt', 'This backup isn’t complete, so it can’t be opened. Back up again to finish it.');
    if (backup.locked) throw this.ctx.fault('password_required', 'This backup is encrypted. Enter its password to open it.');
    if (backup.platform === 'android') {
      throw this.ctx.fault('not_supported', 'Android backups don’t hold WhatsApp’s chats: WhatsApp keeps them encrypted with a key only the phone has. Back up WhatsApp’s media from the phone instead.');
    }
    return backup;
  }

  private media(backupId: string, mediaId: string): WhatsAppMedia | null {
    for (const c of chatsOf(this.open(backupId)) ?? []) {
      const found = c.messages.find((m) => m.media?.id === mediaId);
      if (found) return found.media;
    }
    return null;
  }

  private android(deviceId: string): DeviceSummary {
    const device = this.ctx.requireReady(deviceId, 'files');
    if (device.platform !== 'android') throw this.ctx.fault('not_supported', 'On an iPhone, WhatsApp is read from a backup: open Contacts and messages › WhatsApp.');
    return device;
  }
}

function requireFolder(destination: string, ctx: MockWhatsAppContext): void {
  if (!/^[A-Za-z]:\\|^\\\\/.test(destination ?? '')) throw ctx.badParams('Invalid params: destination must be a full path on this PC.');
}

function walk(fs: MockFileSystem, path: string, items: JobItem[]): void {
  const node = fs.stat('internal', path);
  if (!node?.isDirectory) return;
  for (const entry of fs.list('internal', path)) {
    if (entry.isDirectory) walk(fs, entry.path, items);
    else items.push({ name: entry.name, size: Math.max(1, entry.size) });
  }
}

const hasFile = (media: WhatsAppMedia | null): media is WhatsAppMedia => media !== null && media.kind !== 'location' && media.kind !== 'contact';

/** The backup's local day, as the host names folders after it. */
const day = (backup: BackupInfo) => {
  const d = new Date(backup.createdAt ?? 0);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

/** The oldest of the sample iPhone's backups reads like an older WhatsApp version: two details are missing. */
function isOldest(backup: BackupInfo, all: BackupInfo[]): boolean {
  const mine = all.filter((b) => b.deviceId === backup.deviceId && b.platform === 'ios').sort((a, b) => (a.createdAt ?? '').localeCompare(b.createdAt ?? ''));
  return mine.length > 1 && mine[0]!.id === backup.id;
}

/** A short, quiet tone as a WAV data: URL: something the page's player can really play. */
function tone(seconds: number): string {
  const rate = 8000;
  const length = Math.round(rate * Math.min(Math.max(seconds, 1), 3));
  const bytes = new Uint8Array(44 + length);
  const view = new DataView(bytes.buffer);
  const text = (at: number, s: string) => [...s].forEach((c, i) => view.setUint8(at + i, c.charCodeAt(0)));
  text(0, 'RIFF');
  view.setUint32(4, 36 + length, true);
  text(8, 'WAVEfmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, rate, true);
  view.setUint32(28, rate, true);
  view.setUint16(32, 1, true);
  view.setUint16(34, 8, true);
  text(36, 'data');
  view.setUint32(40, length, true);
  for (let i = 0; i < length; i++) bytes[44 + i] = 128 + Math.round(20 * Math.sin((2 * Math.PI * 440 * i) / rate) * Math.min(1, (length - i) / 800));
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return `data:audio/wav;base64,${btoa(binary)}`;
}

// ---------- sample chats ----------

const PERSIAN = ['سلام! خوبی؟', 'فردا وقت داری؟', 'ساعت ۵ کافه همیشگی؟', 'قبول 👍', 'عکس‌ها رو فرستادم', 'چقدر قشنگه! ❤️', 'رسیدی خبر بده', 'باشه، تو راهم', 'مرسی بابت امروز 🌷', 'شب بخیر'];
const ENGLISH = ['Hi! Are the slides ready for Monday?', 'Almost — sending them tonight.', 'Perfect, thanks!', 'Running 10 minutes late, sorry', 'No worries 🙂', 'Can you call me when you’re free?', 'Here’s the photo from yesterday', 'Looks great!'];
const DOCUMENTS = ['Invoice 0921.pdf', 'Thesis draft v7.docx', 'Timetable.pdf', 'Budget 2027.xlsx', 'Tickets.pdf'];

const cache = new Map<string, ChatData[]>();

/** The sample iPhone's backups hold WhatsApp; other backups don't (null). */
function chatsOf(backup: BackupInfo): ChatData[] | null {
  if (backup.platform !== 'ios' || backup.deviceId !== IPHONE_ID) return null;
  const cached = cache.get(backup.deviceId);
  if (cached) return cached;
  const random = createRandom(hashString('whatsapp' + backup.deviceId));
  const base = Date.UTC(2026, 9, 14, 17, 0);
  const result: ChatData[] = [];

  const add = (no: number, account: WhatsAppChat['account'], title: string, phone: string | null, members: string[], count: number, english: boolean, daysAgo: number, archived = false) => {
    const prefix = account === 'business' ? 'b' : 'w';
    const id = `${prefix}:${no}`;
    const group = members.length > 1;
    const lines = english ? ENGLISH : PERSIAN;
    const messages: WhatsAppMessage[] = [];
    let at = base - daysAgo * 86_400_000;
    const message = (i: number, patch: Partial<WhatsAppMessage>): WhatsAppMessage => ({
      id: `${prefix}:${no * 10_000 + i}`,
      kind: 'text',
      date: new Date(at).toISOString(),
      fromMe: false,
      senderJid: null,
      senderName: null,
      text: null,
      starred: false,
      quote: null,
      media: null,
      ...patch,
    });
    if (group) messages.push(message(0, { kind: 'event', text: title }));
    for (let i = 1; i <= count; i++) {
      at += random.int(2, 400) * 60_000;
      const fromMe = random.chance(0.45);
      const who = members[random.int(0, members.length - 1)]!;
      const sender = fromMe || !group ? {} : { senderName: who, senderJid: `98912${String(hashString(who) % 10_000_000).padStart(7, '0')}@s.whatsapp.net` };
      const roll = random.next();
      const media = (kind: WhatsAppMediaKind, name: string, size: number, extra: Partial<WhatsAppMedia> = {}): WhatsAppMedia => ({
        id: `${id}-m${i}`,
        kind,
        name,
        mimeType: null,
        size,
        durationSeconds: null,
        inBackup: random.chance(0.9),
        hasThumbnail: false,
        latitude: null,
        longitude: null,
        ...extra,
      });
      let next: WhatsAppMessage;
      if (roll < 0.08) {
        const photo = media('image', `${id.replace(':', '')}-${i}.jpg`, random.int(90, 400) * KB, { mimeType: 'image/jpeg' });
        photo.hasThumbnail = photo.inBackup;
        next = message(i, { kind: 'media', fromMe, ...sender, media: photo, text: random.chance(0.3) ? lines[random.int(0, lines.length - 1)]! : null });
      } else if (roll < 0.1) {
        next = message(i, { kind: 'media', fromMe, ...sender, media: media('video', `VID-${i}.mp4`, random.int(2, 40) * MB, { mimeType: 'video/mp4', durationSeconds: random.int(4, 90), hasThumbnail: true }) });
      } else if (roll < 0.14) {
        next = message(i, { kind: 'media', fromMe, ...sender, media: media('voice', `PTT-${i}.opus`, random.int(8, 300) * KB, { mimeType: 'audio/ogg', durationSeconds: random.int(2, 75) }) });
      } else if (roll < 0.155) {
        next = message(i, { kind: 'media', fromMe, ...sender, media: media('document', DOCUMENTS[random.int(0, DOCUMENTS.length - 1)]!, random.int(80, 4000) * KB, { mimeType: 'application/pdf' }) });
      } else if (roll < 0.165) {
        const sticker = media('sticker', `sticker-${i}.webp`, random.int(12, 60) * KB, { mimeType: 'image/webp' });
        sticker.hasThumbnail = sticker.inBackup;
        next = message(i, { kind: 'media', fromMe, ...sender, media: sticker });
      } else if (roll < 0.17) {
        next = message(i, { kind: 'media', fromMe, ...sender, media: media('location', english ? 'Hyde Park' : 'دریاچه چیتگر', 0, { latitude: 35.7456, longitude: 51.2069, inBackup: true }) });
      } else if (roll < 0.175) {
        next = message(i, { kind: 'deleted', fromMe, ...sender });
      } else if (roll < 0.177) {
        next = message(i, { kind: 'other', fromMe, ...sender });
      } else {
        const earlier = [...messages].reverse().find((m) => m.kind === 'text');
        const quote = earlier && random.chance(0.08) ? { messageId: earlier.id, fromMe: earlier.fromMe, senderName: earlier.fromMe ? null : (earlier.senderName ?? title), text: earlier.text, mediaKind: null } : null;
        next = message(i, { fromMe, ...sender, text: lines[random.int(0, lines.length - 1)]!, quote, starred: random.chance(0.03) });
      }
      messages.push(next);
    }
    const last = messages.at(-1)!;
    result.push({
      chat: {
        id,
        account,
        title,
        jid: group ? `1203630000000000${no}@g.us` : phone ? `${phone.slice(1)}@s.whatsapp.net` : null,
        phone: group ? null : phone,
        isGroup: group,
        messageCount: messages.length,
        mediaCount: messages.filter((m) => hasFile(m.media)).length,
        lastMessageAt: last.date,
        preview: last.kind === 'text' || (last.kind === 'media' && last.text) ? last.text : null,
        previewMedia: last.media?.kind ?? null,
        archived,
      },
      messages,
    });
  };

  add(1, 'whatsApp', 'سارا احمدی', '+989121234567', ['سارا احمدی'], 180, false, 120);
  add(2, 'whatsApp', 'خانواده 🏡', null, ['مامان', 'بابا', 'مریم کریمی'], 320, false, 200);
  add(3, 'whatsApp', 'Anna Schmidt', '+4915123456789', ['Anna Schmidt'], 60, true, 60);
  add(4, 'whatsApp', 'Team DeviceDeck', null, ['Anna Schmidt', 'Lucas Martin', 'Emma Wilson'], 140, true, 90);
  add(5, 'whatsApp', 'علی رضایی', '+989351112233', ['علی رضایی'], 45, false, 30);
  add(6, 'whatsApp', 'نیلوفر حسینی', '+989177654321', ['نیلوفر حسینی'], 90, false, 140, true);
  add(7, 'business', 'مشتری — مهدی', '+989350000001', ['مهدی'], 12, false, 10);
  result.sort((a, b) => (b.chat.lastMessageAt ?? '').localeCompare(a.chat.lastMessageAt ?? ''));
  cache.set(backup.deviceId, result);
  return result;
}
