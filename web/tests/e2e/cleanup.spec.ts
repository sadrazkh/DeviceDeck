import { expect, test, type Page } from '@playwright/test';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// Clean-up and battery history, against the mock backend: each screen in light, dark and right-to-left Persian,
// then the flows that matter: every delete goes through a dialog listing what goes and its total.

async function analyse(page: Page) {
  await page.getByTestId('cleanup-analyze').click();
  await expect(page.getByTestId('cleanup-body')).toHaveAttribute('data-state', 'done', { timeout: 15_000 });
}

const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];

for (const variant of VARIANTS) {
  for (const device of ['iphone', 'android'] as const) {
    test(`cleanup ${device} (${variant}) renders cleanly`, async ({ page }) => {
      const consoleWatch = watchConsole(page);
      await page.goto(query(variant, { screen: 'cleanup', device }));
      await expect(page.getByTestId('screen-cleanup')).toBeVisible();
      await expect(page.locator('html')).toHaveAttribute('dir', variant === 'rtl' ? 'rtl' : 'ltr');
      await settle(page);
      await shot(page, `cleanup-${device}-idle-${variant}`);
      await analyse(page);
      await expect(page.getByTestId('cleanup-duplicate-group').first()).toBeVisible();
      await settle(page);
      await shot(page, `cleanup-${device}-${variant}`);
      consoleWatch.assertClean();
    });

    test(`battery ${device} (${variant}) renders cleanly`, async ({ page }) => {
      const consoleWatch = watchConsole(page);
      await page.goto(query(variant, { screen: 'battery', device }));
      await expect(page.getByTestId('battery-chart')).toBeVisible();
      await expect(page.locator('html')).toHaveAttribute('data-theme', variant === 'dark' ? 'dark' : 'light');
      await settle(page);
      await shot(page, `battery-${device}-${variant}`);
      consoleWatch.assertClean();
    });
  }
}

test('Android: deleting the largest videos lists them with their total, copies first, then removes them', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'cleanup', device: 'android' }));
  await analyse(page);
  const videos = page.getByTestId('cleanup-videos');
  const rows = videos.getByTestId('cleanup-media-row');
  const names = [await rows.nth(0).locator('span').first().innerText(), await rows.nth(1).locator('span').first().innerText()];
  await rows.nth(0).getByRole('checkbox').click();
  await rows.nth(1).getByRole('checkbox').click();
  await videos.getByTestId('cleanup-delete').click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Delete 2 photos and videos from Galaxy S25 Ultra?');
  await expect(dialog.getByTestId('cleanup-confirm-row')).toHaveCount(2);
  await expect(dialog.getByTestId('cleanup-confirm-total')).toContainText('2 items');
  await expect(dialog.getByRole('switch')).toBeChecked(); // copy to this PC first, on by default
  await expect(dialog).toContainText('Nothing is deleted unless every copy finishes.');
  await shot(page, 'cleanup-confirm-android-light');
  await dialog.getByTestId('cleanup-confirm').click();

  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByText(/2 photos and videos deleted/)).toBeVisible();
  for (const name of names) await expect(videos.getByText(name, { exact: true })).toHaveCount(0);
  consoleWatch.assertClean();
});

test('Cancel in the dialog deletes nothing', async ({ page }) => {
  await page.goto(query('light', { screen: 'cleanup', device: 'android' }));
  await analyse(page);
  const files = page.getByTestId('cleanup-files');
  const row = files.getByTestId('cleanup-file-row').first();
  await row.getByRole('checkbox').click();
  await files.getByTestId('cleanup-delete-files').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(row).toBeVisible();
});

test('iPhone: photos can be copied but never deleted from the PC', async ({ page }) => {
  await page.goto(query('light', { screen: 'cleanup', device: 'iphone' }));
  await analyse(page);
  await expect(page.getByText('On an iPhone, delete photos in the Photos app')).toBeVisible();
  const videos = page.getByTestId('cleanup-videos');
  await videos.getByTestId('cleanup-media-row').first().getByRole('checkbox').click();
  await expect(videos.getByTestId('cleanup-delete')).toHaveCount(0);
  await videos.getByTestId('cleanup-copy').click();
  await expect(page.getByText('Copying to the PC')).toBeVisible();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByTestId('cleanup-caches')).toHaveCount(0);
});

test('Duplicates: "Select extra copies" keeps the oldest, and selecting every copy is blocked', async ({ page }) => {
  await page.goto(query('light', { screen: 'cleanup', device: 'android' }));
  await analyse(page);
  const card = page.getByTestId('cleanup-duplicates');
  await card.getByRole('button', { name: 'Select extra copies' }).click();
  const group = card.getByTestId('cleanup-duplicate-group').first();
  const boxes = group.getByRole('checkbox');
  await expect(boxes.first()).toHaveAttribute('data-state', 'unchecked');
  await expect(boxes.nth(1)).toHaveAttribute('data-state', 'checked');
  await expect(card.getByTestId('cleanup-delete')).toBeEnabled();
  await boxes.first().click();
  await expect(card.getByText('Every copy of a photo is selected. Leave at least one.')).toBeVisible();
  await expect(card.getByTestId('cleanup-delete')).toBeDisabled();
});

test('Android caches: the dialog lists each app’s cache, then the card says what was freed', async ({ page }) => {
  await page.goto(query('light', { screen: 'cleanup', device: 'android' }));
  await analyse(page);
  await page.getByTestId('cleanup-trim').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Clear every app’s cache on Galaxy S25 Ultra?');
  await expect(dialog).toContainText('Frees about');
  expect(await dialog.getByTestId('cleanup-confirm-row').count()).toBeGreaterThan(3);
  await expect(dialog.getByRole('switch')).toHaveCount(0); // nothing to copy: caches hold nothing personal
  await dialog.getByTestId('cleanup-confirm').click();
  await expect(page.getByTestId('cleanup-caches')).toContainText('freed');
});

test('Battery: the chart answers the keyboard, iPhone health is labelled estimated, the report exports', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'battery', device: 'iphone' }));
  await expect(page.getByTestId('battery-tile-health')).toContainText('Estimated health');
  await expect(page.getByText('iPhone health is an estimate')).toBeVisible();
  const chart = page.getByTestId('battery-chart');
  await chart.focus();
  await page.keyboard.press('End');
  await expect(chart).toContainText('87%');
  await page.getByRole('radio', { name: 'Charge cycles' }).click();
  await expect(page.getByTestId('battery-chart')).toContainText('412');
  await expect(page.getByTestId('battery-row').first()).toContainText('412');

  await page.getByTestId('battery-export').click();
  await expect(page.getByText('Battery report saved')).toBeVisible();

  // An old phone that isn't connected still has its history.
  await page.getByRole('radio', { name: /Pixel 6a/ }).click();
  await expect(page.getByTestId('battery-tile-health')).toContainText('78%');
  await expect(page.getByTestId('battery-record')).toHaveCount(0);

  await page.goto(query('light', { screen: 'battery', device: 'android' }));
  await expect(page.getByTestId('battery-tile-health')).not.toContainText('Estimated');
  await expect(page.getByText('iPhone health is an estimate')).toHaveCount(0);
  consoleWatch.assertClean();
});

test('Battery in Persian: time runs right to left and numbers are Persian', async ({ page }) => {
  await page.goto(query('rtl', { screen: 'battery', device: 'iphone' }));
  const chart = page.getByTestId('battery-chart');
  await expect(chart).toBeVisible();
  const dots = chart.locator('circle');
  const first = await dots.first().boundingBox();
  const last = await dots.last().boundingBox();
  expect(first!.x).toBeGreaterThan(last!.x); // the oldest reading is on the right
  await expect(page.getByTestId('battery-tile-health')).toContainText('۸۷٪');
});
