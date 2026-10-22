import { expect, test, type Locator, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { query, settle, shot, watchConsole, type Variant } from './helpers';
import { layoutProblems } from './layout';

// Deleting photos (round 4, photodelete; a transfer job since v0.4.1), against the mock backend: the iPhone's Delete
// with Clean-up's style of confirmation; once confirmed, the dialog closes and the delete runs in the Transfers drawer
// in stages (copying, reading the camera roll N of M, deleting N of M, checking), with Stop and a result per photo.
// When the phone can't delete, a visible reason with the fastest way on the phone. Every state check auto-waits.

const tiles = (page: Page) => page.locator('[data-testid=photo-grid] [role=gridcell]');
/** Dialogs other than the Transfers drawer (which is a non-modal dialog of its own). */
const dialogs = (page: Page) => page.locator('[role=dialog]:not(#transfer-drawer)');
const deleteRow = (page: Page) => page.getByTestId('transfer-drawer').locator('[data-kind=photosDelete]').first();

async function selectPhotos(page: Page, count: number, persian = false) {
  await expect(tiles(page).first()).toBeVisible();
  await tiles(page).nth(0).click();
  if (count > 1) await tiles(page).nth(count - 1).click({ modifiers: ['Shift'] });
  const digits = persian ? String(count).replace(/\d/g, (d) => '۰۱۲۳۴۵۶۷۸۹'[Number(d)]!) : String(count);
  await expect(page.getByTestId('selection-bar')).toContainText(persian ? `${digits} انتخاب‌شده` : `${count} selected`);
}

/** Confirms the open delete dialog: it closes at once, a toast says where the delete went, the drawer shows it. */
async function confirmAndHandOff(page: Page, confirm = 'photo-delete-confirm', persian = false): Promise<Locator> {
  // The toast stays 4 s; on a slow runner it can come and go before a visibility check runs, so it is recorded the moment
  // it appears (after the dialog's own text, which never says this) instead of being looked for afterwards.
  const toast = persian ? 'حذف در پس‌زمینه ادامه دارد' : 'Deleting in the background';
  await page.evaluate((text) => {
    const w = window as unknown as { __sawToast?: boolean };
    w.__sawToast = false;
    new MutationObserver((records, observer) => {
      if (records.some((r) => Array.from(r.addedNodes).some((n) => n.textContent?.includes(text)) || r.target.textContent?.includes(text))) {
        w.__sawToast = true;
        observer.disconnect();
      }
    }).observe(document.body, { subtree: true, childList: true, characterData: true });
  }, toast);
  await page.getByTestId(confirm).click();
  await expect(dialogs(page)).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => (window as unknown as { __sawToast?: boolean }).__sawToast)).toBe(true);
  await expect(page.getByTestId('transfer-drawer')).toBeVisible();
  const row = deleteRow(page);
  await expect(row).toBeVisible();
  return row;
}

/**
 * Records every stage a delete row passes through. On the mock (and on a slow runner) a stage can come and go before an
 * assertion looks, so tests check the order afterwards instead of catching a stage while it lasts.
 */
async function recordStages(page: Page) {
  await page.evaluate(() => {
    const w = window as unknown as { __stages?: string[] };
    const seen: string[] = (w.__stages = []);
    const note = (el: Element) => {
      const stage = el.getAttribute('data-stage');
      if (stage && seen[seen.length - 1] !== stage) seen.push(stage);
    };
    new MutationObserver((records) => {
      for (const r of records) {
        if (r.type === 'attributes' && r.target instanceof Element && r.target.matches('[data-kind=photosDelete]')) note(r.target);
        for (const n of Array.from(r.addedNodes)) {
          if (!(n instanceof Element)) continue;
          if (n.matches('[data-kind=photosDelete]')) note(n);
          n.querySelectorAll('[data-kind=photosDelete]').forEach(note);
        }
      }
    }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-stage'] });
  });
}

/** The stages recordStages saw, in order, as one list (a repeat in a row counted once). */
const recordedStages = (page: Page) => page.evaluate(() => (window as unknown as { __stages?: string[] }).__stages ?? []);

/** True when `expected` appears in `seen` in this order (other stages may come between). */
function passedInOrder(seen: string[], expected: string[]): boolean {
  let next = 0;
  for (const stage of seen) if (stage === expected[next]) next++;
  return next === expected.length;
}

/** Opens the results of the finished delete in this row. */
async function openResults(row: Locator) {
  await row.getByTestId('delete-job-results').click();
  const results = row.page().getByTestId('delete-results');
  await expect(results).toBeVisible();
  return results;
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
    await expect.poll(() => layoutProblems(page, '[role=dialog]')).toEqual([]);
    if (variant !== 'rtl') await expect.poll(() => contrastProblems(page)).toEqual([]);
    consoleWatch.assertClean();
  });
}

test('Photos: deleting two iPhone photos confirms, hands off to Transfers at once, then shows each result there', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', queue: '0' }));
  await selectPhotos(page, 2);
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
  await expect.poll(() => layoutProblems(page, '[role=dialog]')).toEqual([]);

  const row = await confirmAndHandOff(page);
  await expect(page.getByTestId('selection-bar')).toHaveCount(0); // the selection is done with
  await expect(row.getByTestId('delete-job-title')).toHaveText('Deleting 2 photos and videos from Nima’s iPhone');
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  await expect(row.getByTestId('delete-job-line')).toContainText('2 deleted');
  await expect(page.getByText(/^2 deleted · .* freed$/).first()).toBeVisible(); // the toast at the end

  const results = await openResults(row);
  await expect(results.getByTestId('photo-delete-result')).toHaveCount(2);
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(2);
  await expect(results.getByTestId('photo-delete-summary')).toContainText('2 deleted');
  await expect(results.getByTestId('photo-delete-went')).toHaveText('They’re gone from the phone for good.');
  await settle(page);
  await shot(page, 'photo-delete-results-light');
  await page.getByTestId('delete-results-done').click();
  await expect(page.getByTestId('delete-results')).toHaveCount(0);
  consoleWatch.assertClean();
});

test('Photos: more than 20 photos copy to this PC first; the job shows copying, deleting and checking', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0' }));
  await selectPhotos(page, 21);
  await page.getByTestId('delete-selected').click();
  const dialog = page.getByTestId('photo-delete-dialog');
  await expect(dialog.getByRole('switch')).toBeChecked();
  await expect(dialog).toContainText('Nothing is deleted unless every copy finishes.');
  await recordStages(page);
  const row = await confirmAndHandOff(page);
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  await expect(row.getByTestId('delete-job-line')).toContainText('21 deleted');
  // Reading the camera roll was read ahead since the selection (v0.4.2), so it may take no time at all here.
  const seen = await recordedStages(page);
  expect(passedInOrder(seen, ['copying', 'deleting', 'checking']), `stages seen: ${seen.join(' → ')}`).toBe(true);
  // The copy ran as its own job beside it.
  await expect(page.getByTestId('transfer-drawer').locator('[data-testid=transfer-row]:not([data-kind])')).toHaveCount(1);
});

test('Photos: a refused photo stops the delete; the results say which went, which failed and which weren’t tried', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', photodelete: 'fails' }));
  await selectPhotos(page, 7);
  await page.getByTestId('delete-selected').click();
  const row = await confirmAndHandOff(page);
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  await expect(row.getByTestId('delete-job-line')).toContainText('4 deleted');
  await expect(row.getByTestId('delete-job-line')).toContainText('1 not deleted');
  await expect(row.getByTestId('delete-job-line')).toContainText('2 not tried');
  const results = await openResults(row);
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(4);
  await expect(results.locator('[data-outcome=failed]')).toHaveCount(1);
  await expect(results.locator('[data-outcome=notTried]')).toHaveCount(2);
  await expect(results.locator('[data-outcome=failed]')).toContainText('The phone refused to delete it.');
  // Problems first.
  await expect(results.getByTestId('photo-delete-result').first()).toHaveAttribute('data-outcome', 'failed');
});

test('Photos: Stop while it reads the camera roll deletes nothing', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', deletespeed: 'slow' }));
  await expect(page.getByText('12,481 photos · 732 videos')).toBeVisible();
  await selectPhotos(page, 12);
  await page.getByTestId('delete-selected').click();
  const row = await confirmAndHandOff(page);
  await expect(row).toHaveAttribute('data-stage', 'reading');
  await expect(row.getByTestId('delete-job-line')).toContainText(/of 3,258 items/);
  await expect(row.getByTestId('delete-job-stop')).toHaveAttribute('aria-label', 'Stop the delete');
  await expect(row.getByRole('button', { name: 'Pause' })).toHaveCount(0); // a delete can't be paused
  await row.getByTestId('delete-job-stop').click();
  await expect(row).toHaveAttribute('data-state', 'cancelled');
  await expect(row.getByTestId('delete-job-line')).toHaveText('Stopped · 12 not tried');
  await expect(row.getByTestId('transfer-retry')).toHaveCount(0); // never runs again by itself
  const results = await openResults(row);
  await expect(results.locator('[data-outcome=notTried]')).toHaveCount(12);
  await expect(dialogs(page)).toContainText('Delete stopped');
  await expect(page.getByText('12,481 photos · 732 videos')).toBeVisible(); // the library read again: all still there
});

test('Photos: selecting reads the camera roll ahead, so the delete’s reading starts well along', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', deletespeed: 'slow' }));
  await selectPhotos(page, 1); // selecting asks the iPhone to read ahead (about 220 objects a second here)
  await page.waitForTimeout(5000); // the user looks at what they picked
  await page.getByTestId('delete-selected').click();
  const row = await confirmAndHandOff(page);
  // Without the read ahead it would show a few hundred of 3,258 at this point.
  await expect(row.getByTestId('delete-job-line')).toContainText(/: [1-3],\d{3} of 3,258 items/, { timeout: 1500 });
});

test('Photos: Stop while it deletes stops after the batch on its way, and the results say which went', async ({ page }) => {
  test.setTimeout(120_000);
  await page.goto(query('light', { screen: 'photos', queue: '0', deletespeed: 'slow' }));
  await selectPhotos(page, 12);
  await page.getByTestId('delete-selected').click();
  const row = await confirmAndHandOff(page);
  await expect(row.getByTestId('delete-job-line')).toHaveText('Deleting 5 of 12', { timeout: 60_000 });
  await expect(row.getByTestId('delete-job-stop')).toHaveAttribute('aria-label', 'Stop after the five on their way');
  await row.getByTestId('delete-job-stop').click();
  await expect(row).toHaveAttribute('data-state', 'cancelled');
  await expect(row.getByTestId('delete-job-line')).toContainText(/^Stopped · 10 deleted · .* freed · 2 not tried$/);
  const results = await openResults(row);
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(10);
  await expect(results.locator('[data-outcome=notTried]')).toHaveCount(2);
});

test('Photos: when deletes go to Recently Deleted, the results and the next confirmation say so', async ({ page }) => {
  await page.goto(query('light', { screen: 'photos', queue: '0', photodelete: 'recentlyDeleted' }));
  // The first delete shows where they went; the second confirmation says it.
  await selectPhotos(page, 1);
  await page.getByTestId('delete-selected').click();
  const row = await confirmAndHandOff(page);
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  const results = await openResults(row);
  await expect(results.getByTestId('photo-delete-went')).toHaveText('They’re in Recently Deleted on the iPhone for 30 days.');
  await page.getByTestId('delete-results-done').click();
  await page.getByTestId('transfer-drawer').getByRole('button', { name: 'Close' }).click();
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

test('Photos in Persian: the confirmation, the drawer row and the results read right to left', async ({ page }) => {
  test.setTimeout(120_000);
  const consoleWatch = watchConsole(page);
  await page.goto(query('rtl', { screen: 'photos', queue: '0', deletespeed: 'slow' }));
  await selectPhotos(page, 2, true);
  await page.getByTestId('delete-selected').click();
  await expect(page.getByRole('dialog')).toContainText('۲ عکس از');
  await expect(page.getByRole('dialog')).toContainText('برای همیشه حذف می‌شوند');
  await settle(page);
  await shot(page, 'photo-delete-confirm-rtl');
  await expect.poll(() => layoutProblems(page, '[role=dialog]')).toEqual([]);
  const row = await confirmAndHandOff(page, 'photo-delete-confirm', true);
  await expect(row.getByTestId('delete-job-title')).toHaveText('حذف ۲ عکس و ویدیو از Nima’s iPhone');
  await expect(row.getByTestId('delete-job-line')).toContainText('خواندن فهرست عکس‌های آیفون');
  await settle(page);
  await shot(page, 'delete-job-reading-rtl');
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 60_000 });
  await expect(row.getByTestId('delete-job-line')).toContainText('۲ مورد حذف شد');
  const results = await openResults(row);
  await expect(results.getByTestId('photo-delete-summary')).toContainText('حذف شد');
  await settle(page);
  await shot(page, 'photo-delete-results-rtl');
  await expect.poll(() => layoutProblems(page, '[role=dialog]')).toEqual([]);
  consoleWatch.assertClean();
});

// ---------- The Transfers drawer with a delete: layout and contrast ----------

for (const [width, height] of [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const) {
  for (const variant of ['light', 'dark', 'rtl'] as Variant[]) {
    test(`the drawer's delete rows fit and read clearly at ${width}×${height} (${variant})`, async ({ page }) => {
      const consoleWatch = watchConsole(page);
      await page.setViewportSize({ width, height });
      await page.goto(query(variant, { screen: 'photos', deletespeed: 'slow' })); // with the sample queue beside it
      const persian = variant === 'rtl';
      // A delete stopped while it read (its row offers Results)…
      await selectPhotos(page, 1, persian);
      await page.getByTestId('delete-selected').click();
      const first = await confirmAndHandOff(page, 'photo-delete-confirm', persian);
      await expect(first).toHaveAttribute('data-stage', 'reading');
      await first.getByTestId('delete-job-stop').click();
      await expect(first).toHaveAttribute('data-state', 'cancelled');
      // …and one reading the camera roll, with its bar and Stop.
      await page.getByTestId('transfer-drawer').getByRole('button', { name: persian ? 'بستن' : 'Close' }).click();
      await selectPhotos(page, 3, persian);
      await page.getByTestId('delete-selected').click();
      await confirmAndHandOff(page, 'photo-delete-confirm', persian);
      const drawer = page.getByTestId('transfer-drawer');
      await expect(drawer.locator('[data-kind=photosDelete][data-stage=reading]')).toBeVisible();
      await expect(drawer.locator('[data-kind=photosDelete][data-state=cancelled]')).toBeVisible();
      await settle(page);
      await shot(page, `delete-job-drawer-${width}-${variant}`);
      await expect.poll(() => layoutProblems(page, '#transfer-drawer')).toEqual([]);
      await expect.poll(() => contrastProblems(page)).toEqual([]);
      consoleWatch.assertClean();
    });
  }
}

// ---------- Clean-up ----------

async function analyse(page: Page) {
  await page.getByTestId('cleanup-analyze').click();
  await expect(page.getByTestId('cleanup-body')).toHaveAttribute('data-state', 'done', { timeout: 15_000 });
}

test('Clean-up: an iPhone that allows it deletes its largest photos in Transfers, copy first on, with a result per photo', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'cleanup', device: 'iphone' }));
  await analyse(page);
  await expect(page.getByTestId('photo-delete-notice')).toHaveCount(0);
  // Photos rather than videos: the copy first runs at the mock's speed, and a photo copies in a moment.
  const photos = page.getByTestId('cleanup-photos');
  const first = photos.getByTestId('cleanup-media-row').first();
  const name = await first.locator('span').first().innerText();
  await first.getByRole('checkbox').click();
  await photos.getByTestId('cleanup-delete').click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Delete 1 photo or video from Nima’s iPhone?');
  await expect(dialog.getByRole('switch')).toBeChecked(); // Clean-up keeps copy first on
  await expect(dialog).toContainText('They’re deleted for good: they don’t go to Recently Deleted.');
  await settle(page);
  await shot(page, 'cleanup-confirm-iphone-light');

  await recordStages(page);
  const row = await confirmAndHandOff(page, 'cleanup-confirm');
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 60_000 });
  const seen = await recordedStages(page);
  expect(passedInOrder(seen, ['copying', 'deleting']), `stages seen: ${seen.join(' → ')}`).toBe(true);
  // What went leaves the report.
  await expect(photos.getByText(name, { exact: true })).toHaveCount(0);
  const results = await openResults(row);
  await expect(results.locator('[data-outcome=deleted]')).toHaveCount(1);
  await page.getByTestId('delete-results-done').click();
  await expect(dialogs(page)).toHaveCount(0);
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
