import { expect, test, type Locator, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { query, settle, shot, watchConsole, type Variant } from './helpers';
import { layoutProblems } from './layout';

// Items this PC can't delete (v0.4.2, deletejob2), against the mock backend with ?reach=some: every fourth iPhone
// item is one Windows lists nowhere. Once the camera roll's view is read completely (read ahead in the background),
// Clean-up marks them "Delete on the iPhone" before anything is selected, and a delete leaves them out and lists them
// apart. Until then a quiet "Checking which items this PC can delete…" with progress, and nothing marked. In the mock
// data, the 3rd, 5th, 6th and 8th largest videos (rows 2, 4, 5, 7) and the 4th Photos tile are such items.
// Every state check auto-waits.

const tiles = (page: Page) => page.locator('[data-testid=photo-grid] [role=gridcell]');
const dialogs = (page: Page) => page.locator('[role=dialog]:not(#transfer-drawer)');
const deleteRow = (page: Page) => page.getByTestId('transfer-drawer').locator('[data-kind=photosDelete]').first();
/** A slow read ahead (about 15 s) shows the checking state; long enough to check it, short enough to wait out. */
const READ_AHEAD = { timeout: 40_000 };

async function analyse(page: Page) {
  await page.getByTestId('cleanup-analyze').click();
  await expect(page.getByTestId('cleanup-body')).toHaveAttribute('data-state', 'done', { timeout: 15_000 });
}

/** A row's file name (its name span; the detail and the badge sit beside it). */
const name = (row: Locator) => row.locator('span[class*=_name_]').first().innerText();

/** Confirms the open delete: the dialog closes at once and the drawer shows the job until it has finished. */
async function confirmAndFinish(page: Page, confirm: string): Promise<Locator> {
  await page.getByTestId(confirm).click();
  await expect(dialogs(page)).toHaveCount(0);
  const row = deleteRow(page);
  await expect(row).toBeVisible();
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 60_000 });
  await row.getByTestId('delete-job-results').click();
  return page.getByTestId('delete-results');
}

// ---------- Clean-up ----------

test('Clean-up: checks quietly, then marks what only the iPhone can delete without a reload; Delete leaves those out', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'cleanup', device: 'iphone', reach: 'some', deletespeed: 'slow' }));
  await analyse(page);
  const videos = page.getByTestId('cleanup-videos');
  const rows = videos.getByTestId('cleanup-media-row');

  // The analysis started the read ahead: a quiet line with progress, and nothing marked yet.
  const checking = videos.getByTestId('reach-checking');
  await expect(checking).toContainText('Checking which items this PC can delete…');
  await expect(checking).toHaveAttribute('role', 'status');
  await expect(page.getByTestId('reach-badge')).toHaveCount(0);
  await expect(videos).not.toContainText('can only be deleted on the iPhone');

  // One of each, confirmed before anything is known: both are listed, and the dialog says it is checking…
  const keep = await name(rows.nth(2));
  const goes = await name(rows.nth(0));
  await rows.nth(0).getByRole('checkbox').click();
  await rows.nth(2).getByRole('checkbox').click();
  await videos.getByTestId('cleanup-delete').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Delete 2 photos and videos from Nima’s iPhone?');
  await expect(dialog.getByTestId('cleanup-confirm-row')).toHaveCount(2);
  await expect(dialog.getByTestId('reach-checking')).toBeVisible();

  // …then, once the view is read completely, the dialog splits by itself: one to delete, the other listed apart.
  const leftOut = dialog.getByTestId('delete-left-out');
  await expect(leftOut.getByTestId('delete-left-out-row')).toHaveCount(1, READ_AHEAD);
  await expect(leftOut.getByTestId('delete-left-out-row')).toContainText(keep);
  await expect(leftOut).toContainText('1 item can’t be deleted from this PC — delete it in the Photos app');
  await expect(dialog).toContainText('Delete 1 photo or video from Nima’s iPhone?');
  await expect(dialog.getByTestId('cleanup-confirm-row')).toHaveCount(1);
  await expect(dialog.getByTestId('cleanup-confirm-row')).toContainText(goes);
  await expect(dialog.getByTestId('reach-checking')).toHaveCount(0);
  // The card behind it is marked too, without a reload.
  await expect(checking).toHaveCount(0);
  await expect(rows.nth(2)).toHaveAttribute('data-phone-only', 'true');
  await expect(rows.nth(0).getByTestId('reach-badge')).toHaveCount(0);
  await expect(videos.getByTestId('reach-badge')).toHaveCount(4); // rows 2, 4, 5 and 7 of the 8 shown
  await settle(page);
  await shot(page, 'delete-reach-cleanup-confirm-light');

  await dialog.getByRole('switch').click(); // no copy first: the test is about what is deleted
  const results = await confirmAndFinish(page, 'cleanup-confirm');
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(1);
  await expect(results.locator('[data-outcome=mismatch]')).toHaveCount(0);
  await expect(results).toContainText(goes);
  await page.getByTestId('delete-results-done').click();
  await expect(dialogs(page)).toHaveCount(0);
  await page.getByTestId('transfer-drawer').getByRole('button', { name: 'Close' }).click();

  // The summaries say how many, and the badge says why on hover.
  await expect(videos.locator('h2')).toContainText(/\d+ of 39 can only be deleted on the iPhone/);
  await expect(page.getByTestId('cleanup-photos').locator('h2')).toContainText(/\d+ of 40 can only be deleted on the iPhone/);
  await expect(page.getByTestId('cleanup-duplicates').getByTestId('reach-badge').first()).toBeVisible();
  const kept = rows.filter({ hasText: keep });
  await kept.getByTestId('reach-badge').hover();
  await expect(page.getByRole('tooltip')).toContainText('only the Photos app on the iPhone can delete it');

  // What went leaves the report; what only the iPhone can delete stays, marked, and still selected, alone now:
  // no Delete, the reason, and the copy.
  await expect(videos.getByText(goes, { exact: true })).toHaveCount(0);
  await expect(kept.getByTestId('reach-badge')).toBeVisible();
  await expect(kept.getByRole('checkbox')).toBeChecked();
  await expect(videos.getByTestId('cleanup-delete')).toHaveCount(0);
  await expect(videos.getByTestId('cleanup-phone-only')).toHaveText('Delete these in the Photos app on the iPhone');
  await expect(videos.getByTestId('cleanup-copy')).toBeEnabled();
  consoleWatch.assertClean();
});

test('Clean-up: an iPhone Windows shows everything to marks nothing, and Android has nothing to check', async ({ page }) => {
  await page.goto(query('light', { screen: 'cleanup', device: 'iphone' }));
  await analyse(page);
  const videos = page.getByTestId('cleanup-videos');
  await expect(videos.getByTestId('reach-checking')).toHaveCount(0, READ_AHEAD);
  await expect(page.getByTestId('reach-badge')).toHaveCount(0);
  await expect(videos.locator('h2')).not.toContainText('can only be deleted on the iPhone');

  await page.goto(query('light', { screen: 'cleanup', device: 'android', reach: 'some' }));
  await analyse(page);
  await expect(page.getByTestId('cleanup-videos').getByTestId('cleanup-media-row').first()).toBeVisible();
  await expect(page.getByTestId('reach-checking')).toHaveCount(0);
  await expect(page.getByTestId('reach-badge')).toHaveCount(0);
});

// ---------- Photos ----------

test('Photos: the delete lists what only the iPhone can delete apart, and the marks arrive while it is open', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', queue: '0', reach: 'some', deletespeed: 'slow' }));
  await expect(tiles(page).first()).toBeVisible();
  await tiles(page).nth(0).click();
  await tiles(page).nth(3).click({ modifiers: ['Shift'] });
  const bar = page.getByTestId('selection-bar');
  await expect(bar).toContainText('4 selected');
  // Selecting started the read ahead: the bar says it is checking; nothing is held back yet.
  await expect(bar.getByTestId('reach-checking')).toContainText('Checking which items this PC can delete…');
  await expect(bar.getByTestId('delete-selected')).toBeEnabled();
  await bar.getByTestId('delete-selected').click();

  const dialog = page.getByTestId('photo-delete-dialog');
  await expect(dialog.getByTestId('reach-checking')).toBeVisible();
  await expect(dialog.getByTestId('photo-delete-row')).toHaveCount(4);
  await expect(dialog.getByTestId('delete-left-out')).toHaveCount(0);

  // Once Windows' view is read, the 4th leaves the delete for its own list, without a reload.
  await expect(dialog.getByTestId('delete-left-out-row')).toHaveCount(1, READ_AHEAD);
  await expect(dialog.getByTestId('reach-checking')).toHaveCount(0);
  await expect(dialog.getByTestId('photo-delete-row')).toHaveCount(3);
  await expect(page.getByRole('dialog')).toContainText('Delete 3 photos from Nima’s iPhone?');
  await expect(dialog.getByTestId('delete-left-out')).toContainText('1 item can’t be deleted from this PC — delete it in the Photos app');
  await expect(dialog.getByTestId('photo-delete-total')).toContainText('3 items');
  await settle(page);
  await shot(page, 'delete-reach-photos-dialog-light');

  const results = await confirmAndFinish(page, 'photo-delete-confirm');
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(3);
  await expect(results.locator('[data-outcome=mismatch]')).toHaveCount(0);
  consoleWatch.assertClean();
});

test('Photos: with only such items selected, Delete is off and says to use the Photos app; with some, it says how many', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', reach: 'some' }));
  await expect(tiles(page).first()).toBeVisible();
  await tiles(page).nth(3).click();
  const bar = page.getByTestId('selection-bar');
  await expect(bar).toContainText('1 selected');
  await expect(bar.getByTestId('delete-reach-reason')).toHaveText('Delete these in the Photos app on the iPhone', READ_AHEAD);
  await expect(bar.getByTestId('delete-selected')).toBeDisabled();
  await page.keyboard.press('Delete'); // the grid's shortcut does nothing either
  await expect(dialogs(page)).toHaveCount(0);

  await tiles(page).nth(0).click({ modifiers: ['Shift'] });
  await expect(bar).toContainText('4 selected');
  await expect(bar.getByTestId('delete-reach-reason')).toHaveCount(0);
  await expect(bar.getByTestId('delete-reach-some')).toHaveText('1 of them can only be deleted on the iPhone');
  await expect(bar.getByTestId('delete-selected')).toBeEnabled();
});

// ---------- Layout and contrast: 1024–1600, light, dark and Persian ----------

for (const [width, height] of [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const) {
  for (const variant of ['light', 'dark', 'rtl'] as Variant[]) {
    test(`the marks, the checking line and the split delete fit and read clearly at ${width}×${height} (${variant})`, async ({ page }) => {
      const consoleWatch = watchConsole(page);
      await page.setViewportSize({ width, height });
      const persian = variant === 'rtl';

      // Photos while it checks: the bar's quiet line.
      await page.goto(query(variant, { screen: 'photos', queue: '0', reach: 'some', deletespeed: 'slow' }));
      await expect(tiles(page).first()).toBeVisible();
      await tiles(page).nth(3).click();
      const bar = page.getByTestId('selection-bar');
      await expect(bar.getByTestId('reach-checking')).toBeVisible();
      await settle(page);
      await shot(page, `delete-reach-photos-checking-${width}-${variant}`);
      await expect.poll(() => layoutProblems(page, '[data-testid=selection-bar]')).toEqual([]);
      await expect.poll(() => contrastProblems(page)).toEqual([]);
      // …and once it knows: the reason where Delete would act.
      await expect(bar.getByTestId('delete-reach-reason')).toBeVisible(READ_AHEAD);
      await settle(page);
      await shot(page, `delete-reach-photos-reason-${width}-${variant}`);
      await expect.poll(() => layoutProblems(page, '[data-testid=selection-bar]')).toEqual([]);
      await expect.poll(() => contrastProblems(page)).toEqual([]);

      // Clean-up: the badges and the summaries, then the split confirmation.
      await page.goto(query(variant, { screen: 'cleanup', device: 'iphone', reach: 'some' }));
      await analyse(page);
      const videos = page.getByTestId('cleanup-videos');
      const rows = videos.getByTestId('cleanup-media-row');
      await expect(rows.nth(2).getByTestId('reach-badge')).toBeVisible(READ_AHEAD);
      await expect(rows.nth(2).getByTestId('reach-badge')).toHaveText(persian ? 'حذف روی آیفون' : 'Delete on the iPhone');
      await rows.nth(2).getByRole('checkbox').click();
      await expect(videos.getByTestId('cleanup-phone-only')).toBeVisible();
      await settle(page);
      await shot(page, `delete-reach-cleanup-${width}-${variant}`);
      await expect.poll(() => layoutProblems(page)).toEqual([]);
      await expect.poll(() => contrastProblems(page)).toEqual([]);

      await rows.nth(0).getByRole('checkbox').click();
      await videos.getByTestId('cleanup-delete').click();
      const dialog = page.getByRole('dialog');
      await expect(dialog.getByTestId('delete-left-out-row')).toHaveCount(1);
      await settle(page);
      await shot(page, `delete-reach-cleanup-confirm-${width}-${variant}`);
      await expect.poll(() => layoutProblems(page, '[role=dialog]')).toEqual([]);
      await expect.poll(() => contrastProblems(page)).toEqual([]);
      consoleWatch.assertClean();
    });
  }
}
