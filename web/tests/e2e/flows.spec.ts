import { expect, test, type Page } from '@playwright/test';
import { query, settle, shot, watchConsole } from './helpers';

// The brief's clickable flows, against the mock backend.

const IPHONE = '00008150-001A2C3E0E91002E';

async function mock(page: Page, call: string) {
  await page.evaluate(call);
}

test('connect → Trust → Overview → Photos → select → Export → transfer drawer', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { mock: 'empty' }));
  await expect(page.getByTestId('screen-welcome')).toBeVisible();

  await page.getByTestId('sim-iphone').click();
  await expect(page.getByTestId('welcome-state-awaitingTrust')).toBeVisible({ timeout: 5000 });
  await page.getByTestId('sim-approve').click();
  await expect(page.getByTestId('screen-overview')).toBeVisible({ timeout: 5000 });

  await page.getByTestId('nav-photos').click();
  const tiles = page.locator('[data-testid=photo-grid] [role=gridcell]');
  await expect(tiles.first()).toBeVisible();
  await tiles.nth(0).click();
  await tiles.nth(9).click({ modifiers: ['Shift'] });
  await expect(page.getByTestId('selection-bar')).toContainText('10 selected');
  await settle(page);
  await shot(page, 'flow-photos-selected-light');

  await page.getByTestId('export-selected').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Keep HEIC');
  await settle(page);
  await shot(page, 'flow-export-dialog-light');
  await page.getByTestId('export-start').click();

  await expect(page.getByTestId('transfer-drawer')).toBeVisible();
  await expect(page.getByTestId('transfer-drawer')).toContainText('Exporting');
  await settle(page);
  await shot(page, 'flow-transfer-drawer-light');
  consoleWatch.assertClean();
});

test('photo viewer opens on double-click and moves with the arrow keys', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('dark', { screen: 'photos' }));
  const tiles = page.locator('[data-testid=photo-grid] [role=gridcell]');
  await tiles.nth(2).dblclick();
  const viewer = page.getByTestId('lightbox');
  await expect(viewer).toBeVisible();
  await expect(viewer).toContainText('3 / 13,213');
  await page.keyboard.press('ArrowRight');
  await expect(viewer).toContainText('4 / 13,213');
  await settle(page);
  await shot(page, 'flow-viewer-dark');
  await page.keyboard.press('Escape');
  await expect(viewer).toBeHidden();
  consoleWatch.assertClean();
});

test('a pulled cable pauses transfers; plugging back resumes them', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'overview' }));
  await expect(page.getByTestId('screen-overview')).toBeVisible();
  await mock(page, `window.deviceDeckMock.unplug('${IPHONE}')`);
  const card = page.getByTestId('welcome-state-disconnected');
  await expect(card).toBeVisible();
  await expect(card).toContainText('transfers are paused');
  await settle(page);
  await shot(page, 'flow-disconnected-light');

  await page.getByTestId('transfer-pill').click();
  await expect(page.getByTestId('transfer-drawer')).toContainText('Waiting for Nima’s iPhone');
  await page.keyboard.press('Escape');

  await mock(page, `window.deviceDeckMock.plugBack('${IPHONE}')`);
  await expect(page.getByTestId('screen-overview')).toBeVisible({ timeout: 5000 });
  await expect(page.getByText('is back. Transfers carry on.')).toBeVisible();
  consoleWatch.assertClean();
});

test('a name conflict asks once, then the copy finishes', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'files', queue: '0' }));
  await page.getByTestId('root-app:org.videolan.vlc-ios').click();
  await expect(page.getByTestId('file-table')).toContainText('Persepolis.mkv');
  await mock(
    page,
    `(() => {
      const m = window.deviceDeckMock;
      const file = m.registerLocalFile('Persepolis.mkv', 4000000);
      m.handlers['files.copyToDevice']({ deviceId: '${IPHONE}', root: 'app:org.videolan.vlc-ios', path: '/', files: [file] });
    })()`,
  );
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('“Persepolis.mkv” is already there', { timeout: 5000 });
  await settle(page);
  await shot(page, 'flow-conflict-light');
  await dialog.getByRole('button', { name: 'Keep both' }).click();
  await expect(page.getByTestId('file-table')).toContainText('Persepolis (2).mkv', { timeout: 20_000 });
  consoleWatch.assertClean();
});

test('dropping an .apk on Apps installs it', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'apps', device: 'android', queue: '0' }));
  await expect(page.getByTestId('app-row').first()).toBeVisible();
  const target = page.getByTestId('screen-apps');
  const dataTransfer = await page.evaluateHandle(() => {
    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array(2048)], 'NewsReader_2.4.apk', { type: 'application/vnd.android.package-archive' }));
    return dt;
  });
  await target.locator('table').dispatchEvent('dragenter', { dataTransfer });
  await expect(page.getByText('Drop to install on Galaxy S25 Ultra')).toBeVisible();
  await settle(page);
  await shot(page, 'flow-apps-drop-light');
  await target.locator('table').dispatchEvent('drop', { dataTransfer });
  await expect(page.getByTestId('app-row').filter({ hasText: 'NewsReader' })).toBeVisible({ timeout: 20_000 });
  consoleWatch.assertClean();
});

test('Ctrl+K opens the command palette and switches the theme', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'overview' }));
  await expect(page.getByTestId('screen-overview')).toBeVisible();
  await page.keyboard.press('Control+k');
  const palette = page.getByTestId('command-palette');
  await expect(palette).toBeVisible();
  await settle(page);
  await shot(page, 'flow-palette-light');
  await page.keyboard.type('dark theme');
  await page.keyboard.press('Enter');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  consoleWatch.assertClean();
});
