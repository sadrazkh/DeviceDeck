import { expect, test, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { layoutProblems } from './layout';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// Updates you can see (round 4): the title-bar button, What's new (English and Persian), the banner, the portable zip's
// Download, What's new after an update, the release notes' safe subset, and Settings › Updates; then layout and contrast
// at 1024–1600 in light, dark and Persian. Mock states: ?update=… and ?notes=… (src/bridge/mock/updates.ts).

type MockWindow = { deviceDeckMock: { updates: { applied: number; opened: (string | null)[]; whatsNewShown: string[] } }; __pwned?: number };

const mockUpdates = (page: Page) => page.evaluate(() => {
  const { applied, opened, whatsNewShown } = (window as unknown as MockWindow).deviceDeckMock.updates;
  return { applied, opened: [...opened], whatsNewShown: [...whatsNewShown] };
});

test('Update ready: the title-bar button opens What’s new; Later keeps it; Restart and update restarts', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { update: 'ready', queue: '0' }));
  const button = page.getByTestId('update-button');
  await expect(button).toHaveText('Update ready');
  await expect(button).toHaveAttribute('aria-label', 'DeviceDeck 0.4.0 is ready to install. See what’s new');
  await expect(page.getByTestId('update-banner')).toContainText('DeviceDeck 0.4.0 is ready');
  await settle(page);
  await shot(page, 'updates-button-en');

  await button.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { level: 2 })).toHaveText('What’s new in v0.4.0');
  // Opening What's new counts as seeing the update: the banner steps aside.
  await expect(page.getByTestId('update-banner')).toHaveCount(0);
  await expect(dialog).toContainText('Version 0.4.0 is downloaded. Restarting DeviceDeck installs it in a few seconds.');
  const notes = dialog.getByTestId('release-notes');
  await expect(notes).toHaveAttribute('dir', 'ltr');
  await expect(notes.getByRole('heading', { level: 3 })).toContainText('DeviceDeck v0.4.0 — updates you can see');
  await expect(notes.getByRole('heading', { level: 4 })).toHaveText(['🪟 Windows', '⚙️ Settings', 'ℹ️ Good to know']);
  await expect(notes.locator('strong').first()).toHaveText('Updates you can see');
  await expect(notes.locator('a')).toHaveAttribute('href', 'https://github.com/sadrazkh/DeviceDeck/releases');
  await expect(notes).not.toContainText('فارسی');
  await settle(page);
  await shot(page, 'updates-dialog-en');

  await dialog.getByRole('button', { name: 'Later' }).click();
  await expect(dialog).toBeHidden();
  await expect(button).toBeVisible();

  await button.click();
  await page.getByTestId('whats-new-restart').click();
  await expect(page.getByTestId('whats-new-restart')).toHaveText('Restarting…');
  expect((await mockUpdates(page)).applied).toBe(1);
  consoleWatch.assertClean();
});

test('Persian: the button, What’s new with the Persian notes right-to-left', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('rtl', { update: 'ready', queue: '0' }));
  const button = page.getByTestId('update-button');
  await expect(button).toHaveText('به‌روزرسانی آماده است');
  await settle(page);
  await shot(page, 'updates-button-fa');

  await button.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { level: 2 })).toHaveText('تازه‌های نسخهٔ ۰٫۴٫۰');
  const notes = dialog.getByTestId('release-notes');
  await expect(notes).toHaveAttribute('dir', 'rtl');
  await expect(notes).toHaveAttribute('lang', 'fa');
  await expect(notes.getByRole('heading', { level: 3 })).toContainText('دیوایس‌دک v0.4.0');
  await expect(notes).not.toContainText('Updates you can see');
  await expect(dialog.getByRole('button', { name: 'راه‌اندازی دوباره و به‌روزرسانی' })).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'بعداً' })).toBeVisible();
  await settle(page);
  await shot(page, 'updates-dialog-fa');
  consoleWatch.assertClean();
});

test('Persian UI with English-only notes says so and keeps them left-to-right', async ({ page }) => {
  await page.goto(query('rtl', { update: 'ready', notes: 'english', queue: '0' }));
  await page.getByTestId('update-button').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('این یادداشت‌ها فقط به انگلیسی هستند.');
  await expect(dialog.getByTestId('release-notes')).toHaveAttribute('dir', 'ltr');
});

test('While it downloads the title bar says how far, quietly; then the button and the banner come', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { update: 'downloading' }));
  await expect(page.getByTestId('update-downloading')).toHaveText('Downloading 42%');
  await expect(page.getByTestId('update-button')).toHaveCount(0);
  await expect(page.getByTestId('update-banner')).toHaveCount(0);
  await settle(page);
  await shot(page, 'updates-downloading-en');

  await page.goto(query('light', { update: 'downloadThenReady' }));
  await expect(page.getByTestId('update-downloading')).toBeVisible();
  await expect(page.getByTestId('update-button')).toHaveText('Update ready', { timeout: 20_000 });
  await expect(page.getByTestId('update-downloading')).toHaveCount(0);
  await expect(page.getByTestId('update-banner')).toBeVisible();
  consoleWatch.assertClean();
});

test('Nothing in the title bar when up to date, checking or failing', async ({ page }) => {
  for (const update of ['idle', 'upToDate', 'checking', 'error', 'portable']) {
    await page.goto(query('light', { update }));
    await expect(page.getByTestId('device-switcher')).toBeVisible();
    await expect(page.getByTestId('update-button'), update).toHaveCount(0);
    await expect(page.getByTestId('update-downloading'), update).toHaveCount(0);
  }
});

test('The banner shows once per version: closed, it stays closed', async ({ page }) => {
  await page.goto(query('light', { update: 'ready', queue: '0' }));
  const banner = page.getByTestId('update-banner');
  await expect(banner).toBeVisible();
  await banner.getByRole('button', { name: 'Close' }).click();
  await expect(banner).toHaveCount(0);
  await page.reload();
  await expect(page.getByTestId('update-button')).toBeVisible();
  await expect(page.getByTestId('update-banner')).toHaveCount(0);
});

test('The banner’s What’s new opens the dialog; with a copy running, Restart asks there first', async ({ page }) => {
  await page.goto(query('light', { update: 'ready' }));
  await page.getByTestId('update-banner').getByRole('button', { name: 'What’s new' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  // The sample queue has copies running: the dialog says a restart would stop them.
  await expect(dialog.getByRole('status').filter({ hasText: 'still running' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Later' }).click();
  await expect(page.getByTestId('update-banner')).toHaveCount(0);
});

test('Portable zip: “v0.4.0 on GitHub”, Download opens only its release page', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { update: 'portableNew', queue: '0' }));
  const button = page.getByTestId('update-button');
  await expect(button).toHaveText('v0.4.0 on GitHub');
  await expect(page.getByTestId('update-banner')).toContainText('DeviceDeck 0.4.0 is on GitHub');
  await button.click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('This copy runs from the zip and can’t update itself.');
  await expect(dialog).toContainText('DeviceDeck-0.4.0-win-x64-Setup.exe installs a copy that updates itself');
  await expect(dialog.getByRole('button', { name: 'Restart and update' })).toHaveCount(0);
  await settle(page);
  await shot(page, 'updates-portable-dialog-en');
  await page.getByTestId('whats-new-download').click();
  await expect(dialog).toBeHidden();
  expect((await mockUpdates(page)).opened).toEqual(['0.4.0']);
  consoleWatch.assertClean();
});

test('After an update, What’s new opens by itself once', async ({ page }) => {
  await page.goto(query('light', { update: 'whatsNew', queue: '0' }));
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { level: 2 })).toHaveText('What’s new in v0.4.0');
  await expect(dialog).toContainText('DeviceDeck is now version 0.4.0.');
  await expect.poll(async () => (await mockUpdates(page)).whatsNewShown).toEqual(['0.4.0']);
  await dialog.getByRole('button', { name: 'Close' }).last().click();
  await expect(dialog).toBeHidden();
  await expect(page.getByTestId('update-button')).toHaveCount(0);
});

test('Hostile release notes: no script, image, frame or style; links to GitHub only', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const requests: string[] = [];
  page.on('request', (r) => requests.push(r.url()));
  await page.goto(query('light', { update: 'ready', notes: 'hostile', queue: '0' }));
  await page.getByTestId('update-button').click();
  const notes = page.getByRole('dialog').getByTestId('release-notes');
  await expect(notes).toContainText('real bold');
  expect(await notes.locator('script, img, iframe, style, object, embed').count()).toBe(0);
  expect(await notes.evaluate((el) => Array.from(el.querySelectorAll('*')).flatMap((e) => Array.from(e.attributes).map((a) => a.name)).filter((n) => n.startsWith('on')))).toEqual([]);
  expect(await notes.locator('a').evaluateAll((as) => as.map((a) => a.getAttribute('href')))).toEqual([
    'https://github.com/sadrazkh/DeviceDeck/issues/1',
    'https://github.com/sadrazkh/DeviceDeck/releases',
  ]);
  await expect(notes).toContainText('other site');
  await expect(notes).toContainText('<script> written as text');
  expect(await page.evaluate(() => (window as unknown as MockWindow).__pwned)).toBeUndefined();
  expect(requests.filter((url) => url.includes('evil.example'))).toEqual([]);
  consoleWatch.assertClean();
});

test('Notes that can’t be had: a short line with a link to the release page', async ({ page }) => {
  await page.goto(query('light', { update: 'ready', notes: 'missing', queue: '0' }));
  await page.getByTestId('update-button').click();
  const missing = page.getByTestId('release-notes-missing');
  await expect(missing).toContainText('The release notes couldn’t be loaded.');
  await expect(missing.getByRole('link', { name: 'Read them on GitHub' })).toHaveAttribute('href', 'https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.4.0');
});

test('Settings › Updates: version, channel, kind of copy, last check, Check now, What’s new and the automatic checks', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'settings', update: 'upToDate', queue: '0' }));
  const card = page.getByTestId('settings-updates');
  await expect(card.getByTestId('update-version')).toHaveText('0.3.0');
  await expect(card).toContainText('Stable · x64');
  await expect(card).toContainText('Setup (updates itself)');
  await expect(card.getByTestId('update-last-check')).toContainText('up to date');
  await expect(card.getByTestId('update-auto-hint')).toContainText('every 6 hours');
  await card.getByTestId('settings-whats-new').click();
  await expect(page.getByRole('dialog')).toContainText('This is the version you have.');
  await page.getByRole('dialog').getByRole('button', { name: 'Close' }).last().click();
  await card.getByTestId('check-updates').click();
  await expect(card.getByTestId('update-status')).toContainText('DeviceDeck 0.3.0 is up to date.');
  await settle(page);
  await shot(page, 'updates-settings-en');

  await page.goto(query('light', { screen: 'settings', update: 'rateLimited' }));
  await expect(page.getByTestId('update-status')).toContainText('GitHub’s limit for update checks from this network is used up. Try again after');
  await expect(page.getByTestId('update-last-check')).toContainText('couldn’t reach GitHub');

  await page.goto(query('rtl', { screen: 'settings', update: 'portableNew' }));
  const fa = page.getByTestId('settings-updates');
  await expect(fa).toContainText('فایل zip قابل‌حمل (فقط بررسی می‌کند)');
  await expect(fa.getByTestId('update-auto-hint')).toContainText('هر ۱۲ ساعت');
  await expect(fa.getByTestId('settings-update-act')).toHaveText('دانلود');
  await fa.scrollIntoViewIfNeeded();
  await settle(page);
  await shot(page, 'updates-settings-fa');
  consoleWatch.assertClean();
});

// ---------- Layout and contrast at the window sizes people use, light, dark and Persian ----------

const SIZES = [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const;
const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];

for (const [width, height] of SIZES) {
  for (const variant of VARIANTS) {
    test(`updates fit and read at ${width}×${height} (${variant})`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.setViewportSize({ width, height });
      const check = async (what: string, scope: string, contrast = true) => {
        await settle(page);
        expect(await layoutProblems(page, scope), `${what} at ${width}×${height} (${variant})`).toEqual([]);
        if (contrast) expect(await contrastProblems(page), `${what} contrast at ${width}×${height} (${variant})`).toEqual([]);
      };

      // The title bar in each state that shows something, with the banner under it.
      for (const update of ['ready', 'portableNew', 'downloading']) {
        await page.goto(query(variant, { update, queue: '0' }));
        await expect(page.getByTestId(update === 'downloading' ? 'update-downloading' : 'update-button')).toBeVisible();
        await check(`title bar (${update})`, 'header');
        if (update !== 'downloading') {
          await expect(page.getByTestId('update-banner')).toBeVisible();
          expect(await layoutProblems(page, '[data-testid=update-banner]'), `banner (${update})`).toEqual([]);
        }
      }

      // What's new for a Setup copy (with a copy running) and for the zip.
      for (const update of ['ready', 'portableNew']) {
        await page.goto(query(variant, { update }));
        await page.getByTestId('update-button').click();
        await expect(page.getByRole('dialog').getByTestId('release-notes')).toBeVisible();
        await check(`What’s new (${update})`, '[role=dialog]');
      }

      // Settings › Updates in its states.
      for (const update of ['upToDate', 'downloading', 'ready', 'rateLimited', 'portableNew']) {
        await page.goto(query(variant, { screen: 'settings', update, queue: '0' }));
        await expect(page.getByTestId('settings-updates')).toBeVisible();
        await check(`settings (${update})`, '[data-testid=content]', update === 'ready' || update === 'portableNew');
      }
    });
  }
}
