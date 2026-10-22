import { expect, test, type Locator, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// v0.6: every delete runs as a job in the Transfers drawer, the way photos do since v0.4.1. Files, App files, Apps and
// Clean-up close their confirmation at once; the drawer shows the stage with a bar, Stop works, Results lists every
// item, and several deletes on one phone wait their turn. Stages that come and go are recorded as they pass (a slow
// runner can miss them); every state check auto-waits.

/** Dialogs other than the Transfers drawer (which is a non-modal dialog of its own). */
const dialogs = (page: Page) => page.locator('[role=dialog]:not(#transfer-drawer)');
const jobRows = (page: Page, kind: string) => page.getByTestId('transfer-drawer').locator(`[data-kind=${kind}]`);

async function openVlc(page: Page, variant: Variant = 'light', extra: Record<string, string> = {}) {
  await page.goto(query(variant, { screen: 'appDocuments', queue: '0', ...extra }));
  await page.getByTestId('app-card-app:org.videolan.vlc-ios').click();
  await expect(page.getByTestId('file-table')).toContainText('Persepolis.mkv');
}

const fileRow = (page: Page, name: string) => page.getByTestId('file-table').getByRole('row', { name: new RegExp(name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) });

/** Records every stage a row of this kind passes through, to check the order afterwards. */
async function recordStages(page: Page, kind: string) {
  await page.evaluate((kind) => {
    const w = window as unknown as { __stages?: string[] };
    const seen: string[] = (w.__stages = []);
    const selector = `[data-kind=${kind}]`;
    const note = (el: Element) => {
      const stage = el.getAttribute('data-stage');
      if (stage && seen[seen.length - 1] !== stage) seen.push(stage);
    };
    new MutationObserver((records) => {
      for (const r of records) {
        if (r.type === 'attributes' && r.target instanceof Element && r.target.matches(selector)) note(r.target);
        for (const n of Array.from(r.addedNodes)) {
          if (!(n instanceof Element)) continue;
          if (n.matches(selector)) note(n);
          n.querySelectorAll(selector).forEach(note);
        }
      }
    }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['data-stage'] });
  }, kind);
}

const recordedStages = (page: Page) => page.evaluate(() => (window as unknown as { __stages?: string[] }).__stages ?? []);

async function openResults(page: Page, row: Locator) {
  await row.getByTestId('delete-job-results').click();
  const results = page.getByTestId('delete-results');
  await expect(results).toBeVisible();
  return results;
}

test('Files: Delete closes at once, the drawer deletes them, and Results lists each item', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await openVlc(page);
  await fileRow(page, 'Persepolis.mkv').click();
  await fileRow(page, 'Subtitles').click({ modifiers: ['Control'] });
  await page.getByTestId('delete-button').click();
  await expect(page.getByTestId('delete-list')).toContainText(/Subtitles\s*\d+ files · /);
  await recordStages(page, 'filesDelete');
  await page.getByTestId('delete-confirm').click();

  // The dialog doesn't wait for the phone: it closes as the job is queued.
  await expect(dialogs(page)).toHaveCount(0);
  await expect(page.getByTestId('transfer-drawer')).toBeVisible();
  const row = jobRows(page, 'filesDelete').first();
  await expect(row.getByTestId('delete-job-title')).toHaveText('Deleting 2 items from Nima’s iPhone');
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  await expect(row.getByTestId('delete-job-line')).toHaveText(/^2 deleted · .* freed$/);
  expect(await recordedStages(page)).toContain('deleting');
  await expect(page.getByTestId('file-table')).not.toContainText('Persepolis.mkv');
  await expect(page.getByTestId('file-table')).not.toContainText('Subtitles');

  const results = await openResults(page, row);
  await expect(results.getByTestId('delete-result')).toHaveCount(2);
  await expect(results.getByTestId('delete-results-summary')).toHaveText(/^2 deleted · .* freed$/);
  await expect(results).toContainText('Persepolis.mkv · Deleted');
  await settle(page);
  await shot(page, 'delete-jobs-files-results-light');
  consoleWatch.assertClean();
});

test('a slow delete shows its stage and a bar; Stop leaves what it hadn’t reached', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  // 30 s an item: still on the first one when Stop comes, however slow the runner.
  await openVlc(page, 'light', { deletespeed: 'crawl' });
  await fileRow(page, 'Persepolis.mkv').click();
  await fileRow(page, 'The Grand Budapest Hotel.mp4').click({ modifiers: ['Control'] });
  await fileRow(page, 'Subtitles').click({ modifiers: ['Control'] });
  await page.getByTestId('delete-button').click();
  await page.getByTestId('delete-confirm').click();
  await expect(dialogs(page)).toHaveCount(0);

  const row = jobRows(page, 'filesDelete').first();
  await expect(row).toHaveAttribute('data-stage', 'deleting');
  await expect(row.getByRole('progressbar')).toBeVisible();
  await expect(row.getByTestId('delete-job-line')).toHaveText(/^Deleting \d of 3$/);
  await settle(page);
  await shot(page, 'delete-jobs-files-running-light');
  await row.getByTestId('delete-job-stop').click();

  await expect(row).toHaveAttribute('data-state', 'cancelled');
  await expect(row.getByTestId('delete-job-line')).toContainText('Stopped');
  await expect(row.getByTestId('delete-job-line')).toContainText('not tried');
  const results = await openResults(page, row);
  await expect(results.locator('[data-state=queued]').first()).toContainText('Not tried');
  consoleWatch.assertClean();
});

test('several deletes on one phone wait their turn, then each runs', async ({ page }) => {
  await openVlc(page, 'light', { deletespeed: 'slow' });
  await page.getByTestId('transfer-pill').click();
  await expect(page.getByTestId('transfer-drawer')).toBeVisible();
  // The drawer's waiting row is recorded the moment it shows (on a slow runner it may be gone before a check looks).
  await page.evaluate(() => {
    const w = window as unknown as { __sawWaiting?: boolean };
    w.__sawWaiting = false;
    const look = () => {
      if (document.querySelector('[data-kind=filesDelete][data-state=queued]')?.textContent?.includes('Waiting for the delete before it')) w.__sawWaiting = true;
    };
    new MutationObserver(look).observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['data-state'] });
  });

  // Two deletes confirmed one right after the other (queued in one go here, so a slow runner can't let the first end
  // before the second exists): one delete at a time per phone, so the second waits for the first.
  const states = await page.evaluate(() => {
    type Handler = (params: Record<string, unknown>) => unknown;
    const m = (window as unknown as { deviceDeckMock: { handlers: Record<string, Handler> } }).deviceDeckMock;
    const deviceId = '00008150-001A2C3E0E91002E';
    const root = 'app:org.videolan.vlc-ios';
    const entries = m.handlers['files.list']!({ deviceId, root, path: '/' }) as { path: string; name: string; size: number; isDirectory: boolean }[];
    const item = (name: string) => {
      const e = entries.find((x) => x.name === name)!;
      return { root, path: e.path, size: e.isDirectory ? 0 : e.size, isDirectory: e.isDirectory };
    };
    const first = [item('Persepolis.mkv'), item('Subtitles')];
    const second = [item('The Grand Budapest Hotel.mp4')];
    const total = (items: { size: number }[]) => items.reduce((n, i) => n + i.size, 0);
    const a = m.handlers['files.deleteItems']!({ deviceId, items: first, expectedBytes: total(first) }) as { jobId: string };
    const b = m.handlers['files.deleteItems']!({ deviceId, items: second, expectedBytes: total(second) }) as { jobId: string };
    const jobs = m.handlers['transfers.list']!({}) as { id: string; state: string }[];
    return [a.jobId, b.jobId].map((id) => jobs.find((j) => j.id === id)!.state);
  });
  expect(states[1]).toBe('queued');

  const rows = jobRows(page, 'filesDelete');
  await expect(rows).toHaveCount(2);
  await expect.poll(() => page.evaluate(() => (window as unknown as { __sawWaiting?: boolean }).__sawWaiting)).toBe(true);
  await expect(rows.nth(0)).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  await expect(rows.nth(1)).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  // All three went: the folder is empty now.
  await expect(page.getByText('VLC has no files yet')).toBeVisible();
});

test('Apps: uninstalling two apps is one job in the drawer, and the list drops them', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'apps', device: 'android', queue: '0' }));
  const appRows = page.getByTestId('app-row');
  await expect(appRows.first()).toBeVisible();
  const names = [await appRows.nth(0).getByRole('button', { name: /^Uninstall: / }).getAttribute('aria-label'), await appRows.nth(1).getByRole('button', { name: /^Uninstall: / }).getAttribute('aria-label')].map(
    (label) => label!.replace('Uninstall: ', ''),
  );
  await appRows.nth(0).getByRole('checkbox').click();
  await appRows.nth(1).getByRole('checkbox').click();
  await page.getByRole('button', { name: 'Uninstall', exact: true }).click();
  await expect(dialogs(page)).toContainText('Uninstall 2 apps from Galaxy S25 Ultra?');
  await dialogs(page).getByRole('button', { name: 'Uninstall', exact: true }).click();

  await expect(dialogs(page)).toHaveCount(0);
  const row = jobRows(page, 'appsUninstall').first();
  await expect(row.getByTestId('delete-job-title')).toHaveText('Removing 2 apps from Galaxy S25 Ultra');
  await expect(row).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  await expect(row.getByTestId('delete-job-line')).toHaveText(/^2 removed · .* freed$/);
  for (const name of names) await expect(page.getByRole('button', { name: `Uninstall: ${name}`, exact: true })).toHaveCount(0);

  const results = await openResults(page, row);
  await expect(results).toContainText(`${names[0]} · Removed`);
  await settle(page);
  await shot(page, 'delete-jobs-apps-results-light');
  consoleWatch.assertClean();
});

test('Clean-up: app files and apps go to the drawer too, and the cards drop what went', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'cleanup', device: 'android' }));
  await page.getByTestId('cleanup-analyze').click();
  await expect(page.getByTestId('cleanup-body')).toHaveAttribute('data-state', 'done', { timeout: 15_000 });

  const fileRowInCard = page.getByTestId('cleanup-file-row').first();
  const file = (await fileRowInCard.locator('[class*="_name_"]').first().innerText()).trim();
  expect(file).not.toBe('');
  await fileRowInCard.getByRole('checkbox').click();
  await page.getByTestId('cleanup-delete-files').click();
  const dialog = page.getByTestId('cleanup-confirm-dialog');
  if (await dialog.getByRole('switch').isChecked()) await dialog.getByRole('switch').click(); // gigabytes to copy at the mock's speed
  await recordStages(page, 'filesDelete');
  await page.getByTestId('cleanup-confirm').click();
  await expect(dialogs(page)).toHaveCount(0);
  const files = jobRows(page, 'filesDelete').first();
  await expect(files).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  // Clean-up's job first checks what changed since the analysis.
  expect(await recordedStages(page)).toEqual(expect.arrayContaining(['reading', 'deleting']));
  await expect(page.getByTestId('cleanup-files')).not.toContainText(file);
  await page.keyboard.press('Escape');

  const appRow = page.getByTestId('cleanup-app-row').first();
  const app = (await appRow.locator('[class*="_name_"]').first().innerText()).trim();
  expect(app).not.toBe('');
  await appRow.getByRole('checkbox').click();
  await page.getByTestId('cleanup-uninstall').click();
  const appDialog = page.getByTestId('cleanup-confirm-dialog');
  if ((await appDialog.getByRole('switch').count()) > 0 && (await appDialog.getByRole('switch').isChecked())) await appDialog.getByRole('switch').click();
  await page.getByTestId('cleanup-confirm').click();
  await expect(dialogs(page)).toHaveCount(0);
  const apps = jobRows(page, 'appsUninstall').first();
  await expect(apps).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
  await expect(apps.getByTestId('delete-job-line')).toHaveText(/^1 removed · .* freed$/);
  await expect(page.getByTestId('cleanup-app-row').filter({ hasText: app })).toHaveCount(0);
  consoleWatch.assertClean();
});

for (const variant of ['dark', 'rtl'] as const) {
  test(`delete rows read cleanly (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    await openVlc(page, variant);
    await fileRow(page, 'Persepolis.mkv').click();
    await page.getByTestId('delete-button').click();
    await page.getByTestId('delete-confirm').click();
    await expect(dialogs(page)).toHaveCount(0);
    const row = jobRows(page, 'filesDelete').first();
    await expect(row).toHaveAttribute('data-state', 'done', { timeout: 30_000 });
    if (variant === 'rtl') {
      await expect(row.getByTestId('delete-job-title')).toHaveText(/^حذف ۱ مورد از /);
      await expect(row.getByTestId('delete-job-line')).toContainText('حذف شد');
    }
    await settle(page);
    await shot(page, `delete-jobs-drawer-${variant}`);
    if (variant === 'dark') expect(await contrastProblems(page)).toEqual([]);
    consoleWatch.assertClean();
  });
}
