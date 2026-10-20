import { expect, test, type Page } from '@playwright/test';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// App files (round 2): App documents on both phones and the file browser it shares with Files.

async function dropFiles(page: Page, names: string[]) {
  return page.evaluateHandle((list) => {
    const dt = new DataTransfer();
    for (const name of list) dt.items.add(new File([new Uint8Array(2048)], name));
    return dt;
  }, names);
}

async function openVlc(page: Page, variant: Variant = 'light') {
  await page.goto(query(variant, { screen: 'appDocuments', queue: '0' }));
  await page.getByTestId('app-card-app:org.videolan.vlc-ios').click();
  await expect(page.getByTestId('file-table')).toContainText('Persepolis.mkv');
  await settle(page);
}

test('App documents on Android: app folders by name, shared folders, and why Android/data is missing', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'appDocuments', device: 'android', queue: '0' }));
  const telegram = page.getByTestId('app-card-appmedia:org.telegram.messenger');
  await expect(telegram).toContainText('Telegram');
  await expect(telegram).toContainText('2 files');
  await expect(page.getByText('This phone keeps Android/data private')).toBeVisible();
  await expect(page.getByTestId('app-card-shared:Download')).toContainText('Download');
  await expect(page.getByTestId('app-card-shared:Documents')).toBeVisible();

  // An app whose folder is empty waits behind the switch.
  const spotify = page.getByTestId('app-card-appmedia:com.spotify.music');
  await expect(spotify).toHaveCount(0);
  await expect(page.getByText('1 app with an empty folder is hidden.')).toBeVisible();
  await page.getByText('Show apps with empty folders').click();
  await expect(spotify).toContainText('Empty');

  await page.getByTestId('app-search').fill('whats');
  await expect(page.getByTestId('app-card-appmedia:com.whatsapp')).toBeVisible();
  await expect(telegram).toHaveCount(0);
  await page.getByTestId('app-search').fill('');
  await settle(page);
  await shot(page, 'app-documents-android-light');

  await telegram.click();
  await expect(page.getByTestId('file-table')).toContainText('Telegram');
  await expect(page.getByTestId('crumb-exit')).toContainText('App documents');
  consoleWatch.assertClean();
});

test('a card offers its folders while files are dragged over it; a drop on one copies into it', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'appDocuments', queue: '0' }));
  const infuse = page.getByTestId('app-card-app:com.firecore.infuse');
  await expect(infuse).toContainText('4 files');
  await settle(page); // the screen's enter transition has finished, so the card under the drag is the one that stays
  const dt = await dropFiles(page, ['Taste of Cherry (1997).mkv']);
  await infuse.dispatchEvent('dragenter', { dataTransfer: dt });
  const movies = infuse.getByTestId('subfolder-chip').filter({ hasText: 'Movies' });
  await expect(movies).toBeVisible();
  await expect(infuse.getByTestId('subfolder-chip').filter({ hasText: 'Library Cache' })).toHaveCount(0); // read-only
  await settle(page);
  await shot(page, 'app-documents-drop-chips-light');
  await movies.dispatchEvent('dragenter', { dataTransfer: dt });
  await movies.dispatchEvent('drop', { dataTransfer: dt });
  await expect(infuse.getByTestId('app-card-copy')).toContainText('Taste of Cherry (1997).mkv into Infuse/Movies');
  consoleWatch.assertClean();
});

test('the file browser: search, drop on a folder row, export with a folder picker, delete with what goes listed', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await openVlc(page);
  const browser = page.getByTestId('file-browser');
  const table = page.getByTestId('file-table');

  await page.getByTestId('file-search').fill('budapest');
  await expect(table).toContainText('The Grand Budapest Hotel.mp4');
  await expect(table).not.toContainText('Persepolis.mkv');
  await expect(browser).toContainText('1 of 3 items');
  await page.getByTestId('file-search').fill('');

  // Files dropped on a folder row go into that folder.
  const dt = await dropFiles(page, ['Persepolis.en.srt']);
  const subtitles = table.getByRole('row', { name: /Subtitles/ });
  await browser.dispatchEvent('dragenter', { dataTransfer: dt });
  await subtitles.dispatchEvent('dragover', { dataTransfer: dt });
  await expect(page.getByText('Drop to copy into VLC/Subtitles')).toBeVisible();
  await expect(subtitles).toHaveAttribute('data-drop', 'true');
  await settle(page);
  await shot(page, 'file-browser-drop-on-folder-light');
  await subtitles.dispatchEvent('drop', { dataTransfer: dt });
  await page.getByTestId('transfer-pill').click();
  await expect(page.getByTestId('transfer-drawer')).toContainText('Persepolis.en.srt into VLC/Subtitles');
  await page.keyboard.press('Escape');

  // Export asks where to put the files, then copies through the queue.
  await table.getByRole('row', { name: /Persepolis\.mkv/ }).click();
  await expect(page.getByTestId('file-status')).toContainText('1 selected · 1.2 GB');
  await page.getByTestId('export-button').click();
  await page.getByTestId('transfer-pill').click();
  await expect(page.getByTestId('transfer-drawer')).toContainText('Copying Persepolis.mkv to');
  await page.keyboard.press('Escape');

  // Delete lists each item, what a folder holds, and the space it frees.
  await subtitles.click({ modifiers: ['Control'] });
  await page.getByTestId('delete-button').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Delete 2 items from Nima’s iPhone?');
  await expect(page.getByTestId('delete-list')).toContainText('Persepolis.mkv');
  await expect(page.getByTestId('delete-list')).toContainText(/Subtitles\s*\d+ files · /);
  await expect(dialog).toContainText(/\d+ files and [\d.]+ GB will be removed\. This can’t be undone\./);
  await settle(page);
  await shot(page, 'file-browser-delete-light');
  await page.getByTestId('delete-confirm').click();
  await expect(page.getByText('2 items deleted')).toBeVisible();
  await expect(table).not.toContainText('Persepolis.mkv');
  await expect(table).not.toContainText('Subtitles');
  consoleWatch.assertClean();
});

test('new folder and rename keep working, and Ctrl+F searches the folder', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await openVlc(page);
  await page.getByTestId('new-folder').click();
  await page.getByRole('dialog').getByRole('textbox').fill('Season 1');
  await page.getByTestId('edit-ok').click();
  await expect(page.getByTestId('file-table')).toContainText('Season 1');
  await page.getByTestId('file-table').focus();
  await page.keyboard.press('Control+f');
  await expect(page.getByTestId('file-search')).toBeFocused();
  await page.keyboard.type('season');
  await expect(page.getByTestId('file-browser')).toContainText('1 of 4 items');
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('file-search')).toHaveValue('');
  consoleWatch.assertClean();
});

for (const variant of ['dark', 'rtl'] as const) {
  test(`the app files browser and delete dialog (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    await openVlc(page, variant);
    await expect(page.locator('html')).toHaveAttribute('dir', variant === 'rtl' ? 'rtl' : 'ltr');
    await settle(page);
    await shot(page, `file-browser-${variant}`);
    await page.getByTestId('file-table').getByRole('row', { name: /Subtitles/ }).click();
    await page.getByTestId('delete-button').click();
    await expect(page.getByTestId('delete-list')).toContainText(variant === 'rtl' ? 'فایل' : 'files');
    await settle(page);
    await shot(page, `file-browser-delete-${variant}`);
    await page.keyboard.press('Escape');
    await page.goto(query(variant, { screen: 'appDocuments', device: 'android', queue: '0' }));
    await expect(page.getByTestId('app-card-appmedia:org.telegram.messenger')).toBeVisible();
    await settle(page);
    await shot(page, `app-documents-android-${variant}`);
    consoleWatch.assertClean();
  });
}
