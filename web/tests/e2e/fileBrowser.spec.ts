import { expect, test, type Page } from '@playwright/test';
import { query, settle, watchConsole } from './helpers';

// Round 3 (ui): the file browser fits its pane, says where files can be dropped and why not, and plays the drops of
// File Explorer files that the host hands over (window.deviceDeckNativeDrop drives the same player in the mock).

async function openFolder(page: Page, name: string) {
  await page.getByTestId('file-table').getByRole('row').filter({ hasText: name }).first().dblclick();
  await expect(page.getByTestId('crumb-current')).toContainText(name);
}

async function dropFiles(page: Page, names: string[]) {
  return page.evaluateHandle((list) => {
    const dt = new DataTransfer();
    for (const name of list) dt.items.add(new File([new Uint8Array(1024)], name));
    return dt;
  }, names);
}

interface NativeDrop {
  play(message: unknown): Promise<void>;
  answers: boolean[];
}

/** Plays one host message (enter, over, leave, drop) at the centre of `selector`. */
async function play(page: Page, phase: 'enter' | 'over' | 'leave' | 'drop', selector: string, names: string[] = []) {
  const box = phase === 'leave' ? { x: 0, y: 0, width: 0, height: 0 } : (await page.locator(selector).first().boundingBox())!;
  await page.evaluate(
    ({ phase, x, y, names }) =>
      (window as unknown as { deviceDeckNativeDrop: NativeDrop }).deviceDeckNativeDrop.play({
        $dd: 'nativeDrag',
        phase,
        x,
        y,
        files: names.map((name) => ({ name, path: `E:\\Dropped\\${name}`, directory: false, handle: -1 })),
      }),
    { phase, x: box.x + box.width / 2, y: box.y + box.height / 2, names },
  );
}

const lastAnswer = (page: Page) => page.evaluate(() => (window as unknown as { deviceDeckNativeDrop: NativeDrop }).deviceDeckNativeDrop.answers.at(-1));

test('a narrow pane: icons, a More menu, folded breadcrumbs and a search that opens on demand', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.setViewportSize({ width: 1024, height: 680 });
  await page.goto(query('light', { screen: 'files', device: 'android', names: 'long', queue: '0' }));
  for (const folder of ['Android', 'media', 'ir.example.ketabkhan.reader', 'Work Projects 2026', 'گزارش‌های فصلی', 'Q3 2026']) await openFolder(page, folder);
  await settle(page);

  const toolbar = page.getByTestId('browser-toolbar');
  expect(Number(await toolbar.getAttribute('data-level'))).toBeGreaterThanOrEqual(2);
  await expect(page.getByTestId('new-folder')).toHaveCount(0);
  await expect(page.getByTestId('add-menu')).not.toContainText('Add');

  // The folders above are in the "…" menu, in order; one click goes back to any of them.
  await page.getByTestId('crumb-more').click();
  const menu = page.getByRole('menu');
  await expect(menu.getByRole('menuitem')).toHaveText(['Internal storage', 'Android', 'media', 'ir.example.ketabkhan.reader', /^Work Projects 2026/, /^گزارش‌های فصلی/]);
  await menu.getByRole('menuitem', { name: 'media', exact: true }).click();
  await expect(page.getByTestId('crumb-current')).toHaveText('media');
  await openFolder(page, 'ir.example.ketabkhan.reader');

  // New folder, Delete and Refresh are in More.
  await page.getByTestId('toolbar-more').click();
  await page.getByRole('menuitem', { name: 'New folder' }).click();
  await expect(page.getByRole('dialog')).toContainText('New folder');
  await page.getByRole('button', { name: 'Cancel' }).click();

  // The search box, folded into a button, opens over the breadcrumbs (Ctrl+F too) and closes with Escape.
  if (await page.getByTestId('file-search-open').isVisible()) {
    await page.getByTestId('file-search-open').click();
    await expect(page.getByTestId('file-search')).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('file-search-open')).toBeVisible();
    await page.getByTestId('file-table').focus();
  }
  await page.keyboard.press('Control+f');
  await expect(page.getByTestId('file-search')).toBeFocused();
  await page.keyboard.type('flac');
  await expect(page.getByTestId('file-table')).toContainText('Shajarian & Lotfi');
  await expect(page.getByTestId('file-table')).not.toContainText('Movie night');
  consoleWatch.assertClean();
});

test('a wide pane keeps the labels and the whole search box', async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.goto(query('light', { screen: 'appDocuments', queue: '0' }));
  await page.getByTestId('app-card-app:org.videolan.vlc-ios').click();
  await expect(page.getByTestId('file-table')).toContainText('Persepolis.mkv');
  await settle(page);
  await expect(page.getByTestId('browser-toolbar')).toHaveAttribute('data-level', '0');
  await expect(page.getByTestId('add-menu')).toHaveText('Add');
  await expect(page.getByTestId('export-button')).toHaveText('Export');
  await expect(page.getByTestId('file-search')).toBeVisible();
  await expect(page.getByTestId('crumb-exit')).toHaveText('App documents');
});

test('Add says files can be dragged in; an empty app says drop here, or Add', async ({ page }) => {
  await page.goto(query('light', { screen: 'appDocuments', queue: '0' }));
  await expect(page.getByTestId('app-card-app:com.goodiware.GoodReader')).toContainText('Empty · drop files here');
  await page.getByTestId('app-card-app:com.goodiware.GoodReader').click();
  const empty = page.getByTestId('empty-folder');
  await expect(empty).toContainText('GoodReader has no files yet');
  await expect(empty).toContainText('Drag files or folders here from File Explorer, or use Add.');
  await expect(empty.getByRole('button', { name: 'Add files…' })).toBeVisible();
  await expect(empty.getByRole('button', { name: 'Add a folder…' })).toBeVisible();

  await page.getByTestId('add-menu').hover();
  await expect(page.getByRole('tooltip')).toContainText('You can also drag them here from File Explorer.');
});

test('dragging over a read-only or private folder says why, and a drop there copies nothing', async ({ page }) => {
  await page.goto(query('light', { screen: 'appDocuments', queue: '0' }));
  await page.getByTestId('app-card-app:com.firecore.infuse').click();
  await expect(page.getByTestId('file-table')).toContainText('Library Cache');
  await settle(page);
  const dt = await dropFiles(page, ['Close-Up (1990).mkv']);
  const browser = page.getByTestId('file-browser');
  const cache = page.getByTestId('file-table').getByRole('row').filter({ hasText: 'Library Cache' });
  await browser.dispatchEvent('dragenter', { dataTransfer: dt });
  await cache.dispatchEvent('dragover', { dataTransfer: dt });
  await expect(browser).toContainText('“Library Cache” is read-only, so nothing can be copied into it.');
  await expect(cache).toHaveAttribute('data-refuse', 'true');
  await cache.dispatchEvent('drop', { dataTransfer: dt });
  await page.getByTestId('transfer-pill').click();
  await expect(page.getByTestId('transfer-drawer')).not.toContainText('Close-Up (1990).mkv');
  await page.keyboard.press('Escape');

  // A writable folder row next to it takes them.
  const movies = page.getByTestId('file-table').getByRole('row').filter({ hasText: 'Movies' });
  await browser.dispatchEvent('dragenter', { dataTransfer: dt });
  await movies.dispatchEvent('dragover', { dataTransfer: dt });
  await expect(browser).toContainText('Drop to copy into Infuse/Movies');
  await expect(movies).toHaveAttribute('data-drop', 'true');

  // Android keeps Android/data for each app.
  await page.goto(query('light', { screen: 'files', device: 'android', queue: '0' }));
  await openFolder(page, 'Android');
  const data = page.getByTestId('file-table').getByRole('row').filter({ hasText: /^data/ });
  const again = await dropFiles(page, ['Close-Up (1990).mkv']);
  await page.getByTestId('file-browser').dispatchEvent('dragenter', { dataTransfer: again });
  await data.dispatchEvent('dragover', { dataTransfer: again });
  await expect(page.getByTestId('file-browser')).toContainText('Android keeps “data” for its app alone, so nothing can be copied into it.');
});

test('a drop the host plays lands on the element under the pointer, and a refusing place is answered no', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'appDocuments', queue: '0' }));
  const infuse = '[data-testid="app-card-app:com.firecore.infuse"]';
  await expect(page.locator(infuse)).toContainText('4 files');
  await settle(page);

  await play(page, 'enter', infuse, ['Taste of Cherry (1997).mkv']);
  await expect(page.locator(infuse)).toContainText('Drop to copy into Infuse');
  expect(await lastAnswer(page)).toBe(true);
  await play(page, 'over', infuse);
  await play(page, 'drop', infuse, ['Taste of Cherry (1997).mkv']);
  await page.getByTestId('transfer-pill').click();
  await expect(page.getByTestId('transfer-drawer')).toContainText('Taste of Cherry (1997).mkv into Infuse');
  await page.keyboard.press('Escape');

  // Over Android/data: the page answers no (File Explorer shows no-drop) and says why.
  await page.goto(query('light', { screen: 'files', device: 'android', queue: '0' }));
  await openFolder(page, 'Android');
  const data = 'role=row[name=/^data/]';
  await play(page, 'enter', data, ['notes.txt']);
  expect(await lastAnswer(page)).toBe(false);
  await expect(page.getByTestId('file-browser')).toContainText('Android keeps “data” for its app alone');
  await play(page, 'leave', data);
  await expect(page.getByTestId('file-browser')).not.toContainText('Android keeps “data” for its app alone');

  // Over a folder that takes files: yes.
  await play(page, 'enter', 'role=row[name=/^media/]', ['notes.txt']);
  expect(await lastAnswer(page)).toBe(true);
  await expect(page.getByTestId('file-browser')).toContainText('Drop to copy into Internal storage/Android/media');
  await play(page, 'leave', 'role=row[name=/^media/]');
  consoleWatch.assertClean();
});
