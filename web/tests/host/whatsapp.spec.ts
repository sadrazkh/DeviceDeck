import { expect, test, type Page } from '@playwright/test';
import { existsSync, mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { layoutProblems } from '../e2e/layout';
import { domClick, launch, openScreen, type Host } from './host';

// WhatsApp (round 4) in the real host, through DevTools only (never the mouse, the keyboard or the screen).
//
// Fake phones (default): backs the fake iPhone up into DD_TEST_ROOT\whatsapp-host-backups, opens Contacts and
// messages › WhatsApp, runs the layout check at 1024×680 and 1280×800 in English and Persian, reads a voice note's
// bytes, exports a chat to PDF with WebView2's printer and every chat's media, then reads and copies WhatsApp's
// folders on the fake Android phone. Everything it writes stays under DD_TEST_ROOT (E:\DeviceDeck-test).
//
// Final phase, the owner's iPhone, read-only, after his separate yes to a full backup (see the round 4 notes):
//   DD_PROVIDERS=ios DD_WA_BACKUPS=E:\DeviceDeck-test\owner-backup npm run test:host -- whatsapp
// It makes no backup and exports nothing: it opens the newest backup in that folder in the WhatsApp tab and runs the
// layout check. Problems are reported without their text (the check would otherwise quote chat titles and messages).
// Report: test-results/host/whatsapp.json.

const PROVIDERS = process.env.DD_PROVIDERS ?? 'fake';
const OWNER = PROVIDERS !== 'fake';
const ROOT = process.env.DD_TEST_ROOT ?? 'E:\\DeviceDeck-test';
const BACKUPS = process.env.DD_WA_BACKUPS ?? path.join(ROOT, 'whatsapp-host-backups');
const EXPORTS = path.join(ROOT, 'whatsapp-host-export');
const SIZES = [
  [1024, 680],
  [1280, 800],
] as const;
const LANGUAGES = [
  ['en', 'English', 'WhatsApp'],
  ['fa', 'فارسی', 'واتس‌اپ'],
] as const;

let host: Host;
const report: { view: string; problems: string[] }[] = [];

test.describe.configure({ mode: 'serial' });

test.beforeAll(async () => {
  // Round 4 (updates): "What's new" would open over the window on a first start; no update checks either.
  process.env.DEVICEDECK_WHATSNEW = '0';
  process.env.DEVICEDECK_UPDATE_FEED = 'off';
  host = await launch({ providers: PROVIDERS, data: path.join(ROOT, `whatsapp-host-data-${PROVIDERS}`) });
  await rpc(host.page, 'settings.set', OWNER ? { backupFolder: BACKUPS } : { backupFolder: BACKUPS, fileCopyFolder: EXPORTS });
});

test.afterAll(async () => {
  if (host) {
    await setLanguage(host.page, 'en', 'English').catch(() => undefined);
    await host.close();
  }
  mkdirSync('test-results/host', { recursive: true });
  writeFileSync('test-results/host/whatsapp.json', JSON.stringify(report, null, 2));
});

/** A bridge call from the page, as the page's own client makes it (JSON-RPC over the WebView2 channel). */
async function rpc<T = unknown>(page: Page, method: string, params: unknown = {}): Promise<T> {
  return page.evaluate(
    ({ method, params }) =>
      new Promise<T>((resolve, reject) => {
        const webview = (window as unknown as { chrome: { webview: { postMessage(m: string): void; addEventListener(t: string, l: (e: { data: unknown }) => void): void; removeEventListener(t: string, l: (e: { data: unknown }) => void): void } } }).chrome.webview;
        const id = 900_000_000 + Math.floor(Math.random() * 1_000_000);
        const timer = setTimeout(() => reject(new Error(`${method} never answered`)), 120_000);
        const listener = (e: { data: unknown }) => {
          const data = (typeof e.data === 'string' ? JSON.parse(e.data) : e.data) as { id?: number; result?: T; error?: unknown };
          if (data?.id !== id) return;
          clearTimeout(timer);
          webview.removeEventListener('message', listener);
          if (data.error) reject(new Error(`${method}: ${JSON.stringify(data.error)}`));
          else resolve(data.result as T);
        };
        webview.addEventListener('message', listener);
        webview.postMessage(JSON.stringify({ jsonrpc: '2.0', id, method, params }));
      }),
    { method, params },
  );
}

async function until<T>(what: string, read: () => Promise<T | null | undefined>, timeoutMs = 180_000): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await read();
    if (value) return value;
    if (Date.now() > deadline) throw new Error(`Timed out waiting for ${what}.`);
    await new Promise((r) => setTimeout(r, 500));
  }
}

async function setLanguage(page: Page, lang: string, label: string) {
  await openScreen(page, 'settings');
  await domClick(page, `[data-testid=screen-settings] [role=radio]:text-is("${label}")`);
  await page.waitForFunction((lang) => document.documentElement.lang === lang, lang);
}

/** Picks a phone in the title bar's menu with DOM events only (Radix opens it on pointerdown). */
async function selectDevice(page: Page, name: string) {
  const switcher = page.getByTestId('device-switcher');
  if (((await switcher.textContent()) ?? '').includes(name)) return;
  await switcher.dispatchEvent('pointerdown', { button: 0, pointerType: 'mouse', isPrimary: true });
  await page.locator('[role=menuitem]').filter({ hasText: name }).first().evaluate((el) => (el as HTMLElement).click());
  await expect(switcher).toContainText(name);
}

/** The layout check; for the owner's phone without the text it quotes. */
async function check(page: Page, view: string) {
  await page.waitForFunction(() => !document.querySelector('[data-testid=content] [class*=_skeleton]'), undefined, { timeout: 120_000 });
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(500);
  const problems = (await layoutProblems(page)).map((p) => (OWNER ? p.replace(/"[^"]*"/g, (q) => `"<${q.length - 2} chars>"`) : p));
  report.push({ view, problems });
}

interface Device {
  id: string;
  name: string;
  platform: 'ios' | 'android';
  state: string;
}

test('WhatsApp from an iPhone backup, in the real window', async () => {
  test.setTimeout(20 * 60_000);
  const { page } = host;
  const devices = await until('a ready iPhone', async () => (await rpc<Device[]>(page, 'device.list')).filter((d) => d.platform === 'ios' && d.state === 'ready'));
  const iphone = devices[0]!;
  let backups = await rpc<{ id: string; deviceId: string; state: string; createdAt: string }[]>(page, 'backup.list', { deviceId: iphone.id });
  if (!backups.some((b) => b.state === 'complete')) {
    if (OWNER) throw new Error(`No complete backup of the iPhone in ${BACKUPS}. This check never makes one; back up first (with the owner's yes).`);
    const job = await rpc<{ id: string }>(page, 'backup.start', { deviceId: iphone.id });
    await until('the fake backup', async () => {
      const jobs = await rpc<{ id: string; state: string; error: unknown }[]>(page, 'backup.jobs');
      const mine = jobs.find((j) => j.id === job.id);
      if (mine && ['failed', 'cancelled'].includes(mine.state)) throw new Error(`backup ${mine.state}: ${JSON.stringify(mine.error)}`);
      return mine?.state === 'done';
    });
    backups = await rpc(page, 'backup.list', { deviceId: iphone.id });
  }
  const backup = backups.find((b) => b.state === 'complete')!;

  const status = await rpc<{ found: boolean; accounts: { chats: number; messages: number; media: number; mediaInBackup: number }[]; missing: string[] }>(page, 'whatsapp.status', { backupId: backup.id });
  console.log(`WhatsApp: found ${status.found}; ${status.accounts.map((a) => `${a.chats} chats, ${a.messages} messages, ${a.media} media (${a.mediaInBackup} in the backup)`).join('; ')}; missing ${status.missing.join(', ') || '-'}`);
  expect(status.found).toBe(true);

  await selectDevice(page, iphone.name);
  for (const [lang, label, tab] of LANGUAGES) {
    await setLanguage(page, lang, label);
    for (const [width, height] of SIZES) {
      await page.setViewportSize({ width, height });
      expect(await page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
      await openScreen(page, 'contacts');
      await domClick(page, `[data-testid=screen-contacts] [role=radio]:text-is("${tab}")`);
      await expect(page.getByTestId('whatsapp-message').first()).toBeVisible({ timeout: 120_000 });
      await check(page, `${width}×${height} ${lang}: WhatsApp`);
    }
  }
  await setLanguage(page, 'en', 'English');

  // Thumbnails come from the backup as data: URLs; a voice note's bytes too.
  await openScreen(page, 'contacts');
  await domClick(page, '[data-testid=screen-contacts] [role=radio]:text-is("WhatsApp")');
  const chats = await rpc<{ id: string; title: string; mediaCount: number }[]>(page, 'whatsapp.chats', { backupId: backup.id });
  const withMedia = chats.find((c) => c.mediaCount > 0);
  if (withMedia) {
    const page1 = await rpc<{ items: { media: { id: string; kind: string; inBackup: boolean; hasThumbnail: boolean } | null }[] }>(page, 'whatsapp.messages', { backupId: backup.id, chatId: withMedia.id, offset: 0, limit: 1000 });
    const picture = page1.items.map((m) => m.media).find((m) => m?.hasThumbnail);
    if (picture) expect((await rpc<{ dataUrl: string | null }>(page, 'whatsapp.thumbnail', { backupId: backup.id, mediaId: picture.id })).dataUrl).toMatch(/^data:image\//);
    const voice = page1.items.map((m) => m.media).find((m) => m?.kind === 'voice' && m.inBackup);
    if (voice) expect((await rpc<{ dataUrl: string }>(page, 'whatsapp.media', { backupId: backup.id, mediaId: voice.id })).dataUrl).toMatch(/^data:audio\//);
  }
  if (OWNER) return;

  // Exports (fake phones only): one chat to PDF through WebView2's printer, then every chat's media.
  mkdirSync(EXPORTS, { recursive: true });
  const finished = async (jobId: string) =>
    until(`export ${jobId}`, async () => {
      const job = (await rpc<{ id: string; state: string; destination: string | null; error: unknown }[]>(page, 'backup.jobs')).find((j) => j.id === jobId);
      if (job && ['failed', 'cancelled'].includes(job.state)) throw new Error(`export ${job.state}: ${JSON.stringify(job.error)}`);
      return job?.state === 'done' ? job : null;
    });
  const pdf = await finished((await rpc<{ id: string }>(page, 'whatsapp.export', { backupId: backup.id, chatIds: [withMedia!.id], format: 'pdf', destination: EXPORTS })).id);
  expect(pdf.destination).toMatch(/\.pdf$/);
  expect(readFileSync(pdf.destination!).subarray(0, 5).toString()).toBe('%PDF-');
  console.log(`PDF: ${pdf.destination} (${statSync(pdf.destination!).size} bytes)`);
  const media = await finished((await rpc<{ id: string }>(page, 'whatsapp.exportMedia', { backupId: backup.id, destination: EXPORTS })).id);
  const folders = readdirSync(media.destination!);
  console.log(`media: ${media.destination}: ${folders.length} chat folders`);
  expect(folders.length).toBeGreaterThan(0);
});

test('WhatsApp on the Android phone, copied to the PC', async () => {
  test.skip(OWNER, 'The owner has no Android phone.');
  test.setTimeout(10 * 60_000);
  const { page } = host;
  const android = (await rpc<Device[]>(page, 'device.list')).find((d) => d.platform === 'android' && d.state === 'ready')!;
  const info = await rpc<{ apps: { package: string; hasFolder: boolean }[] }>(page, 'whatsapp.android', { deviceId: android.id });
  expect(info.apps.some((a) => a.hasFolder)).toBe(true);

  await selectDevice(page, android.name);
  await openScreen(page, 'contacts');
  await domClick(page, '[data-testid=screen-contacts] [role=radio]:text-is("WhatsApp")');
  await expect(page.getByTestId('whatsapp-android-app').first()).toBeVisible({ timeout: 60_000 });
  await check(page, 'Android: WhatsApp');

  const started = await rpc<{ jobId: string; folder: string }>(page, 'whatsapp.backupAndroid', { deviceId: android.id, destination: EXPORTS });
  await until('the Android copy', async () => {
    const job = (await rpc<{ id: string; state: string; error: unknown }[]>(page, 'transfers.list')).find((j) => j.id === started.jobId);
    if (job && ['failed', 'cancelled'].includes(job.state)) throw new Error(`copy ${job.state}: ${JSON.stringify(job.error)}`);
    return job?.state === 'done';
  });
  expect(existsSync(path.join(started.folder, 'WhatsApp', 'Media'))).toBe(true);
  console.log(`Android copy: ${started.folder}`);
});

test('every view passed the layout check', () => {
  const failing = report.filter((v) => v.problems.length > 0);
  console.log(`${report.length} views, ${failing.length} with problems`);
  expect(failing, failing.map((v) => `${v.view}\n  ${v.problems.join('\n  ')}`).join('\n')).toEqual([]);
});
