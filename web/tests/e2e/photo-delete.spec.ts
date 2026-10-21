import { expect, test, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { query, settle, shot, watchConsole, type Variant } from './helpers';
import { layoutProblems } from './layout';

// Deleting photos (round 4, photodelete), against the mock backend: the iPhone's Delete with Clean-up's style of
// confirmation and a result per photo, and, when it can't delete, a visible reason with the fastest way on the phone.

const tiles = (page: Page) => page.locator('[data-testid=photo-grid] [role=gridcell]');

async function selectPhotos(page: Page, count: number, persian = false) {
  await expect(tiles(page).first()).toBeVisible();
  await tiles(page).nth(0).click();
  if (count > 1) await tiles(page).nth(count - 1).click({ modifiers: ['Shift'] });
  const digits = persian ? String(count).replace(/\d/g, (d) => '۰۱۲۳۴۵۶۷۸۹'[Number(d)]!) : String(count);
  await expect(page.getByTestId('selection-bar')).toContainText(persian ? `${digits} انتخاب‌شده` : `${count} selected`);
}

for (const variant of ['light', 'dark', 'rtl'] as Variant[]) {
  test(`Photos: an iPhone that can't delete says why, where Delete would be (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    await page.goto(query(variant, { screen: 'photos', queue: '0', photodelete: 'iCloudPhotos' }));
    await selectPhotos(page, 3, variant === 'rtl');
    const bar = page.getByTestId('selection-bar');
    await expect(bar.getByTestId('delete-selected')).toHaveCount(0);
    await expect(bar.getByTestId('delete-reason')).toHaveText(
      variant === 'rtl' ? 'عکس‌های iCloud آن‌ها را برای این رایانه فقط‌خواندنی نگه می‌دارد' : 'iCloud Photos keeps them read-only for this PC',
    );
    await bar.getByTestId('delete-why').click();
    const why = page.getByTestId('photo-delete-why');
    await expect(why).toHaveAttribute('data-reason', 'iCloudPhotos');
    await expect(why.getByTestId('photo-delete-fastest')).toContainText(variant === 'rtl' ? 'سریع‌ترین راه روی آیفون' : 'Fastest on the iPhone');
    await expect(why).toContainText('Recently Deleted');
    await settle(page);
    await shot(page, `photo-delete-why-${variant}`);
    expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
    if (variant !== 'rtl') expect(await contrastProblems(page)).toEqual([]);
    consoleWatch.assertClean();
  });
}

test('Photos: deleting two iPhone photos lists them, says they go for good, then shows each result', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', queue: '0' }));
  await selectPhotos(page, 2);
  const before = await page.getByTestId('selection-bar').innerText();
  expect(before).toContain('2 selected');
  await page.getByTestId('delete-selected').click();

  const dialog = page.getByTestId('photo-delete-dialog');
  await expect(page.getByRole('dialog')).toContainText('Delete 2 photos from Nima’s iPhone?');
  await expect(dialog.getByTestId('photo-delete-row')).toHaveCount(2);
  await expect(dialog.getByTestId('photo-delete-total')).toContainText('2 items');
  await expect(dialog.getByRole('switch')).not.toBeChecked(); // 20 or fewer: copy first starts off
  await expect(dialog.getByTestId('photo-delete-goes')).toHaveAttribute('data-goes', 'permanent');
  await expect(dialog).toContainText('They’re deleted for good: they don’t go to Recently Deleted.');
  await expect(dialog).toContainText('This can’t be undone.');
  await settle(page);
  await shot(page, 'photo-delete-confirm-light');
  expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
  await page.getByTestId('photo-delete-confirm').click();

  const results = page.getByTestId('photo-delete-results');
  await expect(results.getByTestId('photo-delete-result')).toHaveCount(2);
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(2);
  await expect(results.getByTestId('photo-delete-summary')).toContainText('2 deleted');
  await expect(results.getByTestId('photo-delete-went')).toHaveText('They’re gone from the phone for good.');
  await settle(page);
  await shot(page, 'photo-delete-results-light');
  await page.getByTestId('photo-delete-done').click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByTestId('selection-bar')).toHaveCount(0);
  consoleWatch.assertClean();
});

test('Photos: more than 20 photos start with "Copy to this PC first" on, and it runs before the delete', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0' }));
  await selectPhotos(page, 21);
  await page.getByTestId('delete-selected').click();
  const dialog = page.getByTestId('photo-delete-dialog');
  await expect(dialog.getByRole('switch')).toBeChecked();
  await expect(dialog).toContainText('Nothing is deleted unless every copy finishes.');
  await page.getByTestId('photo-delete-confirm').click();
  await expect(page.getByTestId('photo-delete-summary')).toContainText('21 deleted');
});

test('Photos: a refused photo stops the delete; the results say which went, which failed and which weren’t tried', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', photodelete: 'fails' }));
  await selectPhotos(page, 7);
  await page.getByTestId('delete-selected').click();
  await page.getByTestId('photo-delete-confirm').click();
  const results = page.getByTestId('photo-delete-results');
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(4);
  await expect(results.locator('[data-outcome=failed]')).toHaveCount(1);
  await expect(results.locator('[data-outcome=notTried]')).toHaveCount(2);
  await expect(results.locator('[data-outcome=failed]')).toContainText('The phone refused to delete it.');
  // Problems first.
  await expect(results.getByTestId('photo-delete-result').first()).toHaveAttribute('data-outcome', 'failed');
});

test('Photos: when deletes go to Recently Deleted, the dialog says so and drops "can’t be undone"', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', photodelete: 'recentlyDeleted' }));
  // The first delete shows where they went; the second confirmation says it.
  await selectPhotos(page, 1);
  await page.getByTestId('delete-selected').click();
  await page.getByTestId('photo-delete-confirm').click();
  await expect(page.getByTestId('photo-delete-went')).toHaveText('They’re in Recently Deleted on the iPhone for 30 days.');
  await page.getByTestId('photo-delete-done').click();
  await selectPhotos(page, 1);
  await page.getByTestId('delete-selected').click();
  const dialog = page.getByTestId('photo-delete-dialog');
  await expect(dialog.getByTestId('photo-delete-goes')).toHaveAttribute('data-goes', 'recentlyDeleted');
  await expect(dialog).toContainText('Seen on this iPhone after the last delete.');
  await expect(dialog).not.toContainText('This can’t be undone.');
});

test('Photos: a locked iPhone says unlock, and "Check again" brings Delete back', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', photodelete: 'locked' }));
  await selectPhotos(page, 1);
  await expect(page.getByTestId('delete-reason')).toHaveText('Unlock the iPhone to delete from this PC');
  await page.getByTestId('delete-why').click();
  await page.getByTestId('photo-delete-recheck').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Close' }).last().click();
  await expect(page.getByTestId('delete-selected')).toBeVisible();
});

test('Photos in Persian: the confirmation and the results read right to left', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('rtl', { screen: 'photos', queue: '0' }));
  await selectPhotos(page, 2, true);
  await page.getByTestId('delete-selected').click();
  await expect(page.getByRole('dialog')).toContainText('۲ عکس از');
  await expect(page.getByRole('dialog')).toContainText('برای همیشه حذف می‌شوند');
  await settle(page);
  await shot(page, 'photo-delete-confirm-rtl');
  expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
  await page.getByTestId('photo-delete-confirm').click();
  await expect(page.getByTestId('photo-delete-summary')).toContainText('حذف شد');
  await settle(page);
  await shot(page, 'photo-delete-results-rtl');
  expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
  consoleWatch.assertClean();
});

// ---------- Clean-up ----------

async function analyse(page: Page) {
  await page.getByTestId('cleanup-analyze').click();
  await expect(page.getByTestId('cleanup-body')).toHaveAttribute('data-state', 'done', { timeout: 15_000 });
}

test('Clean-up: an iPhone that allows it deletes its largest videos, copy first on, with a result per video', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'cleanup', device: 'iphone' }));
  await analyse(page);
  await expect(page.getByTestId('photo-delete-notice')).toHaveCount(0);
  const videos = page.getByTestId('cleanup-videos');
  await videos.getByTestId('cleanup-media-row').first().getByRole('checkbox').click();
  await videos.getByTestId('cleanup-delete').click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Delete 1 photo or video from Nima’s iPhone?');
  await expect(dialog.getByRole('switch')).toBeChecked(); // Clean-up keeps copy first on
  await expect(dialog).toContainText('They’re deleted for good: they don’t go to Recently Deleted.');
  await settle(page);
  await shot(page, 'cleanup-confirm-iphone-light');
  await dialog.getByTestId('cleanup-confirm').click();

  await expect(page.getByTestId('photo-delete-results').locator('[data-outcome=deleted]')).toHaveCount(1);
  await page.getByTestId('cleanup-results-done').click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  consoleWatch.assertClean();
});

test('Clean-up: an iPhone that can’t delete shows the reason and the fastest way, and only copies', async ({ page }) => {
  await page.goto(query('light', { screen: 'cleanup', device: 'iphone', photodelete: 'converted' }));
  await analyse(page);
  const notice = page.getByTestId('photo-delete-notice');
  await expect(notice).toHaveAttribute('data-reason', 'converted');
  await expect(page.getByText('The iPhone converts photos for this PC')).toBeVisible();
  await expect(notice).toContainText('Keep Originals');
  const videos = page.getByTestId('cleanup-videos');
  await videos.getByTestId('cleanup-media-row').first().getByRole('checkbox').click();
  await expect(videos.getByTestId('cleanup-delete')).toHaveCount(0);
  await expect(videos.getByTestId('cleanup-copy')).toBeVisible();
});
