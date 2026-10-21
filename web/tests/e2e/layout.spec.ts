import { expect, test, type Page } from '@playwright/test';
import { layoutProblems } from './layout';
import { query, settle, type Variant } from './helpers';

// Overview at the window sizes people use, in light, dark and right-to-left Persian: no text cut without a tooltip,
// no label broken over two lines, nothing drawn over anything else or out of its card.

const SIZES = [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const;
const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];
const PHONES: { name: string; params: Record<string, string> }[] = [
  { name: 'iphone', params: {} },
  { name: 'android', params: { device: 'android' } },
  { name: 'limited', params: { mock: 'limited' } },
];

for (const [width, height] of SIZES) {
  for (const variant of VARIANTS) {
    test(`overview fits at ${width}×${height} (${variant})`, async ({ page }) => {
      await page.setViewportSize({ width, height });
      for (const phone of PHONES) {
        await page.goto(query(variant, { screen: 'overview', ...phone.params }));
        await expect(page.getByTestId('screen-overview')).toBeVisible();
        await expect(page.getByTestId('details-card').locator('dd').first()).toBeVisible();
        await settle(page);
        expect(await layoutProblems(page), `${phone.name} at ${width}×${height} (${variant})`).toEqual([]);
      }
    });
  }
}

// The round-2 screens, the same way; clean-up after its analysis, when every card is filled.
const SCREENS: { name: string; params: Record<string, string>; ready: string; analyse?: boolean }[] = [
  { name: 'backup', params: { screen: 'backup' }, ready: '[data-testid=backup-row]' },
  { name: 'backup-android', params: { screen: 'backup', device: 'android' }, ready: '[data-testid=backup-row]' },
  { name: 'contacts', params: { screen: 'contacts' }, ready: '[data-testid=contact-details]' },
  { name: 'battery', params: { screen: 'battery' }, ready: '[data-testid=battery-chart]' },
  { name: 'battery-android', params: { screen: 'battery', device: 'android' }, ready: '[data-testid=battery-chart]' },
  { name: 'cleanup', params: { screen: 'cleanup', device: 'android' }, ready: '[data-testid=screen-cleanup]', analyse: true },
  { name: 'toolbox', params: { screen: 'toolbox', device: 'android' }, ready: '[data-testid=tool-tile-mirror]' },
  { name: 'tool-crash', params: { screen: 'toolbox', tool: 'crash' }, ready: '[data-testid=crash-row]' },
  { name: 'tool-log', params: { screen: 'toolbox', tool: 'log' }, ready: '[data-testid=log-row]' },
  { name: 'settings', params: { screen: 'settings' }, ready: '[data-testid=screen-settings]' },
];

for (const [width, height] of SIZES) {
  for (const variant of VARIANTS) {
    test(`round-2 screens fit at ${width}×${height} (${variant})`, async ({ page }) => {
      test.setTimeout(180_000);
      await page.setViewportSize({ width, height });
      for (const screen of SCREENS) {
        await page.goto(query(variant, { queue: '0', ...screen.params }));
        await expect(page.locator(screen.ready).first()).toBeVisible({ timeout: 15_000 });
        if (screen.analyse) {
          await page.getByTestId('cleanup-analyze').click();
          await expect(page.getByTestId('cleanup-body')).toHaveAttribute('data-state', 'done', { timeout: 15_000 });
        }
        await settle(page);
        expect(await layoutProblems(page), `${screen.name} at ${width}×${height} (${variant})`).toEqual([]);
      }
    });
  }
}

// Files and App documents (round 3): the file browser they share, with long, Persian and mixed-direction names
// (?names=long adds a long-named app on each phone with folders three deep), at four window sizes.
const BROWSER_SIZES = [
  [1024, 680],
  [1280, 800],
  [1366, 720],
  [1600, 1000],
] as const;
const LONG_IPHONE_APP = 'app:com.example.filemanager.pro';
const LONG_ANDROID_APP = 'appmedia:ir.example.ketabkhan.reader';
const DEEP = ['Work Projects 2026', 'گزارش‌های فصلی', 'Q3 2026'];

/** Opens a folder of the open listing; done when the breadcrumb's current folder is that one. */
async function openFolder(page: Page, name: string) {
  await page.getByTestId('file-table').getByRole('row').filter({ hasText: name }).first().dblclick();
  await expect(page.getByTestId('crumb-current')).toContainText(name);
}

async function openDeep(page: Page, check: (name: string) => Promise<void>, label: string) {
  for (const folder of DEEP) await openFolder(page, folder);
  await expect(page.getByTestId('file-table')).toContainText('Annual financial statement');
  await check(`${label}, three folders deep`);
}

for (const [width, height] of BROWSER_SIZES) {
  for (const variant of VARIANTS) {
    test(`files and app documents fit at ${width}×${height} (${variant})`, async ({ page }) => {
      test.setTimeout(240_000);
      await page.setViewportSize({ width, height });
      const check = async (name: string) => {
        await settle(page);
        expect(await layoutProblems(page), `${name} at ${width}×${height} (${variant})`).toEqual([]);
      };
      const table = page.getByTestId('file-table');

      // App documents on the iPhone: the grid, the long-named app at its root and three folders deep, an empty app.
      await page.goto(query(variant, { screen: 'appDocuments', names: 'long', queue: '0' }));
      await expect(page.getByTestId(`app-card-${LONG_IPHONE_APP}`)).toBeVisible();
      await expect(page.getByTestId('app-card-meta').filter({ hasText: /…|Counting/ })).toHaveCount(0);
      await check('App documents (iPhone)');
      await page.getByTestId(`app-card-${LONG_IPHONE_APP}`).click();
      await expect(table).toContainText('Movie night');
      await check('a long-named app');
      await openDeep(page, check, 'a long-named app');
      // Three folders deep the crumbs above are folded into "…": back to the grid by loading it again.
      await expect(page.getByTestId('crumb-more')).toBeVisible();
      await page.goto(query(variant, { screen: 'appDocuments', names: 'long', queue: '0' }));
      await page.getByTestId('app-card-app:com.goodiware.GoodReader').click();
      await expect(page.getByTestId('empty-folder')).toBeVisible();
      await check('an empty app');

      // App documents on Android: the grid, the long-named app's media folder, three folders deep.
      await page.goto(query(variant, { screen: 'appDocuments', device: 'android', names: 'long', queue: '0' }));
      await expect(page.getByTestId(`app-card-${LONG_ANDROID_APP}`)).toBeVisible();
      await expect(page.getByTestId('app-card-meta').filter({ hasText: /…|Counting/ })).toHaveCount(0);
      await check('App documents (Android)');
      await page.getByTestId(`app-card-${LONG_ANDROID_APP}`).click();
      await expect(table).toContainText('Movie night');
      await check('a long-named Android app');
      await openDeep(page, check, 'a long-named Android app');

      // Files on the iPhone (it opens at its App documents) and on Android, with the PC pane beside the phone.
      await page.goto(query(variant, { screen: 'files', names: 'long', queue: '0' }));
      await expect(page.getByTestId(`app-card-${LONG_IPHONE_APP}`)).toBeVisible();
      await check('Files (iPhone)');
      await page.getByTestId(`app-card-${LONG_IPHONE_APP}`).click();
      await expect(table).toContainText('Movie night');
      await check('Files, a long-named app');
      await openDeep(page, check, 'Files, a long-named app');

      await page.goto(query(variant, { screen: 'files', device: 'android', names: 'long', queue: '0' }));
      await expect(table).toContainText('Android');
      await check('Files (Android)');
      for (const folder of ['Android', 'media', 'ir.example.ketabkhan.reader']) await openFolder(page, folder);
      await openDeep(page, check, 'Files (Android), Android/media');
      await page.goto(query(variant, { screen: 'files', device: 'android', names: 'long', queue: '0' }));
      for (const folder of ['Android', 'media', 'ir.example.ketabkhan.reader', 'پوشهٔ خالی']) await openFolder(page, folder);
      await expect(page.getByTestId('empty-folder')).toBeVisible();
      await check('Files (Android), an empty folder');
    });
  }
}
