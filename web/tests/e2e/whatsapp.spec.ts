import { expect as baseExpect, test, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { query, settle, shot, watchConsole, type Variant } from './helpers';
import { layoutProblems } from './layout';

// WhatsApp (round 4) against the mock backend: the WhatsApp tab of Contacts and messages (a backup of the sample
// iPhone has made-up chats; the old iPhone's has none), WhatsApp on the sample Android phone, and the Telegram and
// WhatsApp guide on App documents. Simulated time is moved on explicitly (fastForward).
const expect = baseExpect.configure({ timeout: 15_000 });

const SIZES = [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const;
const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];

const tab = (variant: Variant) => (variant === 'rtl' ? 'واتس‌اپ' : 'WhatsApp');

async function fastForward(page: Page, ms: number) {
  await page.evaluate((total) => {
    const mock = (window as unknown as { deviceDeckMock: { tick(ms: number): void } }).deviceDeckMock;
    for (let t = 0; t < total; t += 250) mock.tick(250);
  }, ms);
}

/** Contacts and messages › WhatsApp on the sample iPhone, a chat open with its newest messages in view. */
async function openWhatsApp(page: Page, variant: Variant = 'light', chat?: string) {
  await page.goto(query(variant, { queue: '0', screen: 'contacts' }));
  await expect(page.getByTestId('contact-details')).toBeVisible();
  await settle(page);
  await page.getByRole('radio', { name: tab(variant) }).click();
  await expect(page.getByTestId('whatsapp-chat').first()).toBeVisible();
  if (chat) await page.getByTestId('whatsapp-chat').filter({ hasText: chat }).click();
  await expect(page.getByTestId('whatsapp-message').first()).toBeVisible();
}

async function openAndroid(page: Page, variant: Variant = 'light') {
  await page.goto(query(variant, { queue: '0', screen: 'contacts', device: 'android' }));
  await expect(page.getByTestId('contacts-android')).toBeVisible();
  await settle(page);
  await page.getByRole('radio', { name: tab(variant) }).click();
  await expect(page.getByTestId('whatsapp-android-app').first()).toBeVisible();
}

for (const variant of VARIANTS) {
  test(`WhatsApp screens render cleanly (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    await openWhatsApp(page, variant, variant === 'rtl' ? 'سارا احمدی' : 'Team DeviceDeck');
    await settle(page);
    await shot(page, `whatsapp-${variant}`);
    if (variant !== 'rtl') expect(await contrastProblems(page), 'iPhone backup').toEqual([]);

    await openAndroid(page, variant);
    await settle(page);
    await shot(page, `whatsapp-android-${variant}`);
    if (variant !== 'rtl') expect(await contrastProblems(page), 'Android').toEqual([]);

    await page.goto(query(variant, { queue: '0', screen: 'appDocuments' }));
    await expect(page.getByTestId('messenger-guide')).toBeVisible();
    await settle(page);
    await shot(page, `appdocs-guide-${variant}`);
    if (variant !== 'rtl') expect(await contrastProblems(page), 'App documents').toEqual([]);
    consoleWatch.assertClean();
  });
}

for (const [width, height] of SIZES) {
  for (const variant of VARIANTS) {
    test(`WhatsApp fits at ${width}×${height} (${variant})`, async ({ page }) => {
      await page.setViewportSize({ width, height });
      await openWhatsApp(page, variant, 'Team DeviceDeck');
      await settle(page);
      expect(await layoutProblems(page), `iPhone backup at ${width}×${height} (${variant})`).toEqual([]);

      await page.getByTestId('whatsapp-export').click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await settle(page);
      expect(await layoutProblems(page, '[role=dialog]'), `export dialog at ${width}×${height} (${variant})`).toEqual([]);
      await page.keyboard.press('Escape');

      await openAndroid(page, variant);
      await settle(page);
      expect(await layoutProblems(page), `Android at ${width}×${height} (${variant})`).toEqual([]);

      await page.goto(query(variant, { queue: '0', screen: 'appDocuments' }));
      await expect(page.getByTestId('messenger-guide')).toBeVisible();
      await settle(page);
      expect(await layoutProblems(page), `App documents at ${width}×${height} (${variant})`).toEqual([]);
    });
  }
}

test('a chat reads newest at the bottom, with replies, pictures, voice notes and earlier pages', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await openWhatsApp(page, 'light', 'سارا احمدی');

  const summary = page.getByTestId('whatsapp-summary');
  await expect(summary).toContainText('WhatsApp · WhatsApp Business');
  await expect(summary).toContainText('7 chats');
  const sara = page.getByTestId('whatsapp-chat').filter({ hasText: 'سارا احمدی' });
  await expect(sara).toContainText('180 messages');
  await expect(page.getByTestId('whatsapp-chat').filter({ hasText: 'مشتری — مهدی' })).toContainText('Business');
  await expect(page.getByTestId('whatsapp-chat').filter({ hasText: 'نیلوفر حسینی' })).toContainText('Archived');

  const thread = page.getByTestId('whatsapp-thread');
  await expect.poll(() => thread.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight)).toBeLessThan(4);
  await expect(page.getByTestId('whatsapp-quote').first()).toBeVisible();
  await thread.evaluate((el) => (el.scrollTop = 0));
  await expect(thread.locator('[data-testid=whatsapp-tile] img').first()).toHaveAttribute('src', /^data:image\//);

  // A voice note is read out of the backup when asked, then played in the page.
  const voice = thread.getByTestId('whatsapp-voice').first();
  await voice.scrollIntoViewIfNeeded();
  await voice.getByRole('button').click();
  await expect(voice.getByTestId('whatsapp-audio')).toHaveAttribute('src', /^data:audio\//);

  // 200 at a time: the first page holds the newest; earlier ones come on request (the family group has 321).
  await page.getByTestId('whatsapp-chat').filter({ hasText: 'خانواده' }).click();
  await expect(page.getByTestId('whatsapp-message')).toHaveCount(200);
  const before = await page.getByTestId('whatsapp-message').count();
  await page.getByRole('button', { name: 'Show earlier messages' }).click();
  await expect.poll(() => page.getByTestId('whatsapp-message').count()).toBeGreaterThan(before);

  // Search by name or number.
  await page.getByRole('searchbox', { name: 'Search chats' }).fill('anna');
  await expect(page.getByTestId('whatsapp-chat')).toHaveCount(1);
  await page.getByRole('searchbox', { name: 'Search chats' }).fill('+4915');
  await expect(page.getByTestId('whatsapp-chat')).toHaveText(/Anna Schmidt/);
  consoleWatch.assertClean();
});

test('one chat exports to HTML, and all the media into folders per chat', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await openWhatsApp(page, 'light', 'Anna Schmidt');

  await page.getByTestId('whatsapp-export').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Export WhatsApp chats');
  await expect(dialog.getByRole('radio', { name: 'This chat: Anna Schmidt' })).toBeChecked();
  await settle(page);
  await shot(page, 'flow-whatsapp-export-light');
  await dialog.getByTestId('whatsapp-export-start').click();
  await expect(dialog).toBeHidden();
  await fastForward(page, 2500);
  await expect(page.getByText('Saved from the backup of Nima’s iPhone').first()).toBeVisible();

  await page.getByTestId('whatsapp-export-media-all').click();
  await expect(dialog).toContainText('Export WhatsApp media');
  await expect(dialog.getByRole('radio', { name: 'All 7 chats' })).toBeChecked();
  await expect(dialog).toContainText('WhatsApp media - Nima’s iPhone');
  await dialog.getByTestId('whatsapp-export-start').click();
  await fastForward(page, 3000);
  await expect(page.getByText('Saved from the backup of Nima’s iPhone').first()).toBeVisible();
  consoleWatch.assertClean();
});

test('a backup without WhatsApp says so, and an older WhatsApp says what it can’t show', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await openWhatsApp(page);
  const source = page.getByTestId('contacts-source');

  // The oldest backup of the sample iPhone reads like an older WhatsApp version.
  const options = await source.locator('option').allTextContents();
  await source.selectOption({ index: options.findLastIndex((o) => o.startsWith('Nima’s iPhone')) });
  await expect(page.getByText('A few details aren’t shown')).toBeVisible();
  await page.getByText('What’s missing').click();
  await expect(page.getByText('ZWAMESSAGE.ZSTARRED')).toBeVisible();

  // The old iPhone's backup is encrypted: unlock it, then it has no WhatsApp.
  await source.selectOption({ index: options.findIndex((o) => o.startsWith('iPhone 12 (old)')) });
  await page.getByTestId('contacts-unlock').click();
  await page.getByRole('dialog').getByLabel(/password/i).fill('1234');
  await page.getByRole('dialog').getByRole('button', { name: /unlock/i }).click();
  await expect(page.getByText('No WhatsApp chats in this backup')).toBeVisible();
  await expect(page.getByText('WhatsApp wasn’t on iPhone 12 (old) when this backup was made.')).toBeVisible();
  consoleWatch.assertClean();
});

test('Android says the chats stay locked and copies WhatsApp’s folders through the transfer queue', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await openAndroid(page);

  const pane = page.getByTestId('whatsapp-android');
  await expect(pane).toContainText('WhatsApp’s chats on Android can’t be opened on a PC');
  await expect(pane).toContainText('msgstore.db.crypt14');
  const app = page.getByTestId('whatsapp-android-app');
  await expect(app).toHaveCount(1);
  await expect(app).toContainText('/Android/media/com.whatsapp/WhatsApp');
  await expect(app).toContainText('4 files');
  await expect(pane).toContainText('Only WhatsApp can restore its .crypt14 and .crypt15 backups');
  await expect(page.getByTestId('screen-contacts')).toContainText('WhatsApp on Galaxy S25 Ultra');

  await page.getByTestId('whatsapp-android-start').click();
  await expect(page.getByText(/^Copying WhatsApp into .*WhatsApp - Galaxy S25 Ultra - \d{4}-\d{2}-\d{2}$/).first()).toBeVisible();
  await fastForward(page, 8000);
  await expect(page.getByTestId('whatsapp-android-progress')).toContainText('Copied into');
  await expect(page.getByTestId('whatsapp-android-backup').getByRole('button', { name: 'Show in Explorer' })).toBeVisible();
  await shot(page, 'flow-whatsapp-android-light');

  // The other tab still explains contacts and messages on Android.
  await page.getByRole('radio', { name: 'Contacts and messages' }).click();
  await expect(page.getByTestId('contacts-android')).toBeVisible();
  consoleWatch.assertClean();
});

test('App documents on an iPhone shows the Telegram and WhatsApp guide; Android doesn’t', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { queue: '0', screen: 'appDocuments' }));
  const guide = page.getByTestId('messenger-guide');
  await expect(guide).toBeVisible();
  await expect(guide.getByTestId('messenger-guide-telegram')).toContainText('Export Telegram data');
  await expect(guide.getByTestId('messenger-guide-whatsapp')).toBeVisible();

  await guide.getByTestId('messenger-guide-open').click();
  await expect(page.getByTestId('screen-contacts')).toBeVisible();
  await expect(page.getByRole('radio', { name: 'WhatsApp' })).toBeChecked();
  await expect(page.getByTestId('whatsapp-pane')).toBeVisible();

  await page.goto(query('light', { queue: '0', screen: 'appDocuments' }));
  await page.getByTestId('messenger-guide').getByRole('button', { name: 'Hide this tip' }).click();
  await expect(page.getByTestId('messenger-guide')).toBeHidden();

  await page.goto(query('light', { queue: '0', screen: 'appDocuments', device: 'android' }));
  await expect(page.getByTestId('app-card-appmedia:org.telegram.messenger')).toBeVisible();
  await expect(page.getByTestId('messenger-guide')).toHaveCount(0);
  consoleWatch.assertClean();
});
