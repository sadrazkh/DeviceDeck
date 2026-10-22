import { expect, test } from '@playwright/test';
import { settle, shot, watchConsole } from './helpers';

// Round 2, desktop: the PC pane in Files, Settings › Window and Updates, renaming the phone, clearing transfers.

test('Files: the PC pane browses folders, makes one, and copies a PC file to the phone', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto('/?latency=0&indexing=0&theme=light&device=android&screen=files');
  const pane = page.getByTestId('pc-pane');
  await expect(pane).toBeVisible();
  await expect(page.getByTestId('pc-location')).toContainText('Phone files');

  await page.getByTestId('pc-places').click();
  await page.getByRole('menuitem', { name: /Downloads/ }).click();
  await expect(page.getByTestId('pc-location')).toContainText('Downloads');
  await expect(pane.getByTestId('pc-row')).toHaveCount(2);

  await pane.getByRole('button', { name: 'New folder' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('textbox').fill('From the phone');
  await dialog.getByRole('button', { name: 'Create' }).click();
  await expect(pane.getByTestId('pc-row').filter({ hasText: 'From the phone' })).toBeVisible();

  await pane.getByTestId('pc-row').filter({ hasText: 'Statement October.pdf' }).click();
  const copy = page.getByTestId('pc-copy-to-phone');
  await expect(copy).toBeEnabled();
  await copy.click();
  await expect(page.getByTestId('transfer-pill')).toBeVisible();
  await settle(page);
  await shot(page, 'flow-pc-pane-light');
  consoleWatch.assertClean();
});

test('Settings: window options and "Check now" for updates', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto('/?latency=0&indexing=0&theme=light&screen=settings');
  const window = page.getByTestId('settings-window');
  await expect(window).toBeVisible();
  const tray = window.getByRole('switch').first();
  await expect(tray).toHaveAttribute('data-state', 'checked');
  await tray.click();
  await expect(tray).toHaveAttribute('data-state', 'unchecked');

  await page.getByTestId('check-updates').click();
  await expect(page.getByTestId('update-status')).toContainText('is up to date');
  await settle(page);
  await shot(page, 'flow-settings-desktop-light');
  consoleWatch.assertClean();
});

test('Overview: rename the phone', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto('/?latency=0&indexing=0&theme=light&screen=overview');
  await page.getByTestId('rename-device').click();
  const input = page.getByTestId('rename-input');
  await input.fill('Sara’s iPhone');
  await input.press('Enter');
  await expect(page.getByTestId('device-name')).toHaveText('Sara’s iPhone');
  await expect(page.getByTestId('device-switcher')).toContainText('Sara’s iPhone');
  consoleWatch.assertClean();
});

test('Transfers: retry a failed copy, then clear what is finished', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto('/?latency=0&indexing=0&theme=light');
  await page.getByTestId('transfer-pill').click();
  const drawer = page.getByTestId('transfer-drawer');
  await expect(drawer).toBeVisible();
  const failed = drawer.locator('[data-testid=transfer-row][data-state=failed]');
  await expect(failed).toHaveCount(1);
  await failed.getByTestId('transfer-retry').click();
  await expect(drawer.locator('[data-testid=transfer-row][data-state=failed]')).toHaveCount(0);

  await expect(drawer.getByTestId('transfers-clear')).toBeVisible();
  // The sample queue keeps working meanwhile: a job may finish just after Clear (seen 1 in 6 runs, on main too).
  // So check that what was finished when Clear was pressed is gone, not that nothing is finished any more.
  const done = drawer.locator('[data-testid=transfer-row][data-state=done]');
  const finished = await done.allTextContents();
  await drawer.getByTestId('transfers-clear').click();
  await expect.poll(async () => (await done.allTextContents()).filter((row) => finished.includes(row))).toEqual([]);
  consoleWatch.assertClean();
});
