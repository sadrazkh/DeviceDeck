import { expect, test, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { centre, domClick, dragEvents, hostDrop, launch, openScreen, recordDragEvents, type Host } from './host';

// Files dropped from File Explorer on the real window (WinUI + WebView2, the fake phones) reach the page and the phone.
//
// Which half each check covers:
// - "OLE → page → phone": the host's dev self-test calls the OLE drop target registered on the window (the object
//   Explorer's drag loop finds under the pointer) with Explorer's own data object for real files on disk; the page then
//   gets HTML drag events at that point, its drop zone starts the copy, and the files show up on the fake phone.
//   It does not run Explorer's drag loop itself (that needs the mouse): the owner's manual check covers that link.
// - "page → host": CDP's Input.dispatchDragEvent drops real paths on the page the browser's own way; the page sends
//   the File objects to the host (postMessageWithAdditionalObjects → CoreWebView2File.Path), as a file picker does.

const ROOT = process.env.DD_TEST_ROOT ?? 'E:\\DeviceDeck-test';
const FILES = path.join(ROOT, 'drop-selftest');
const stamp = Date.now().toString(36);
const NAMES = [`drop-test-${stamp}.txt`, `فایل آزمایشی ${stamp}.txt`];
const PATHS = NAMES.map((n) => path.join(FILES, n));
const CDP_NAME = `cdp-drop-${stamp}.txt`;
const CDP_PATH = path.join(FILES, CDP_NAME);

let host: Host;

test.beforeAll(async () => {
  mkdirSync(FILES, { recursive: true });
  for (const p of [...PATHS, CDP_PATH]) writeFileSync(p, `DeviceDeck drop test: ${path.basename(p)}\n`);
  host = await launch({ providers: 'fake', data: path.join(ROOT, 'ui-host-data') });
});

test.afterAll(async () => {
  await host?.close();
});

/** App documents' grid (the screen remembers an opened folder: leave it first). */
async function openGrid(page: Page) {
  await openScreen(page, 'appDocuments');
  await expect(page.locator('[data-testid=app-grid], [data-testid=crumb-exit]').first()).toBeVisible();
  if (await page.getByTestId('crumb-exit').isVisible()) await domClick(page, '[data-testid=crumb-exit]');
  await expect(page.getByTestId('app-grid').first()).toBeVisible();
}

async function openShared(page: Page, card: string) {
  await openGrid(page);
  await domClick(page, `[data-testid="app-card-${card}"]`);
  await expect(page.getByTestId('file-table')).toBeVisible();
}

test('OLE → page → phone: an Explorer drop on an open folder copies the files into it', async () => {
  const { page } = host;
  await openShared(page, 'shared:Documents');
  await recordDragEvents(page);
  const at = await centre(page, '[data-testid=file-table]');
  const report = await hostDrop(page, at.x, at.y, PATHS);
  console.log(`self-test: target ${report.target}, hit ${report.hit}, calls ${JSON.stringify(report.calls)}`);

  // The window has an OLE drop target where Explorer's drag loop looks, and it took the files as a copy.
  expect(report.error).toBeUndefined();
  expect(report.target).toBe('Microsoft.UI.Content.DesktopChildSiteBridge');
  expect(report.calls?.map((c) => [c.call, c.hr, c.effect])).toEqual([
    ['DragEnter', 0, 1],
    ['DragOver', 0, 1],
    ['DragOver', 0, 1],
    ['DragOver', 0, 1],
    ['Drop', 0, 1],
  ]);

  // The page saw a drag of files at that point, and the drop carried both files.
  const events = await dragEvents(page);
  expect(events.map((e) => e.type)).toContain('dragenter');
  const drop = events.find((e) => e.type === 'drop');
  expect(drop?.types).toContain('Files');
  expect(drop?.files.sort()).toEqual([...NAMES].sort());
  expect(Math.abs(drop!.x - at.x) <= 1 && Math.abs(drop!.y - at.y) <= 1).toBe(true);

  // …and the files reached the phone's folder.
  const table = page.getByTestId('file-table');
  for (const name of NAMES) await expect(table).toContainText(name);
});

test('OLE → page: the drop goes to the element under the pointer (an app card)', async () => {
  const { page } = host;
  await openGrid(page);
  const card = '[data-testid="app-card-shared:Download"]';
  await expect(page.locator(card)).toContainText('3 files');
  await recordDragEvents(page);
  const at = await centre(page, card);
  const report = await hostDrop(page, at.x, at.y, PATHS.slice(0, 1));
  expect(report.calls?.at(-1)).toMatchObject({ call: 'Drop', hr: 0, effect: 1 });
  const drop = (await dragEvents(page)).find((e) => e.type === 'drop');
  expect(drop?.target).toBe('app-card-shared:Download');
  await expect(page.locator(card)).toContainText('4 files');
});

test('OLE → page: hovering a place that takes no files is refused', async () => {
  const { page } = host;
  await openScreen(page, 'files');
  const table = page.getByTestId('file-table');
  await expect(table).toBeVisible();
  // Internal storage › Android: data and obb are private to their apps on this phone.
  await table.getByRole('row').filter({ hasText: 'Android' }).first().dispatchEvent('dblclick');
  const data = table.getByRole('row').filter({ hasText: /^data/ }).first();
  await expect(data).toBeVisible();
  const box = (await data.boundingBox())!;
  const report = await hostDrop(page, box.x + 60, box.y + box.height / 2, PATHS.slice(0, 1), true);
  expect(report.calls?.at(-2)).toMatchObject({ call: 'DragOver', hr: 0, effect: 0 });
  expect(report.calls?.at(-1)).toMatchObject({ call: 'DragLeave', hr: 0 });
});

test('page → host: a browser drop of real files (CDP) reaches the host as their paths', async () => {
  const { page } = host;
  await openShared(page, 'shared:Documents');
  const at = await centre(page, '[data-testid=file-table]');
  const cdp = await page.context().newCDPSession(page);
  const data = { items: [], files: [CDP_PATH], dragOperationsMask: 1 };
  for (const type of ['dragEnter', 'dragOver', 'drop'] as const) {
    await cdp.send('Input.dispatchDragEvent', { type, x: at.x, y: at.y, data });
  }
  await expect(page.getByTestId('file-table')).toContainText(CDP_NAME);
});
