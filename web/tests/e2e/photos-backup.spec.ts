import { expect, test } from '@playwright/test';
import { query, settle, shot, watchConsole } from './helpers';

// Photos round 2: the "Back up to PC" panel, adding photos to an iPhone app, and the Settings defaults.

for (const variant of ['light', 'dark', 'rtl'] as const) {
  test(`the backup panel shows what is new, where it goes and the last run (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    await page.goto(query(variant, { screen: 'photos', queue: '0' }));
    await expect(page.getByTestId('backup-badge')).toBeVisible();
    await page.getByTestId('backup-open').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByTestId('backup-count')).toBeVisible();
    await expect(dialog.getByTestId('backup-destination')).toContainText('Phone backups');
    await expect(dialog.getByTestId('backup-last-run')).toBeVisible();
    // The Hidden album is locked on the Photos screen, so its size isn't shown here either.
    await expect(dialog).toContainText(variant === 'rtl' ? 'آلبوم Hidden کنار گذاشته می‌شوند' : 'Photos in the Hidden album are left out.');
    await settle(page);
    await shot(page, `photos-backup-panel-${variant}`);
    consoleWatch.assertClean();
  });
}

test('backing up from the panel: progress on the Photos screen, then nothing new', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', device: 'android', queue: '0' }));
  const badge = page.getByTestId('backup-badge');
  await expect(badge).toHaveText('46');
  await page.getByTestId('backup-open').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('backup-count')).toContainText('46');

  // One folder instead of months, saved for this phone.
  await dialog.getByRole('radio', { name: 'One folder' }).click();
  await expect(dialog.getByTestId('backup-use-defaults')).toBeVisible();

  await dialog.getByTestId('backup-start').click();
  await expect(page.getByTestId('transfer-drawer')).toContainText('Backing up new photos');
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('photo-job-banner')).toBeVisible();
  await expect(page.getByTestId('photo-job-banner')).toHaveAttribute('data-kind', 'photosBackup');
  await settle(page);
  await shot(page, 'photos-backup-running-light');

  // Real speed would take minutes (the phone has GBs of new video): run the mock's clock ahead instead.
  await page.evaluate(() => {
    const mock = (window as unknown as { deviceDeckMock: { tick(ms: number): void } }).deviceDeckMock;
    for (let i = 0; i < 4 * 60 * 30; i++) mock.tick(250);
  });
  await expect(badge).toBeHidden({ timeout: 15_000 });
  await page.getByTestId('backup-open').click();
  await expect(page.getByRole('dialog')).toContainText('Everything is backed up');
  await expect(page.getByTestId('backup-last-run')).toContainText('46 copied');
  consoleWatch.assertClean();
});

test('forgetting the history asks first and deletes nothing', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', queue: '0' }));
  await page.getByTestId('backup-open').click();
  await page.getByTestId('backup-forget').click();
  const confirm = page.getByRole('alertdialog').or(page.getByRole('dialog').filter({ hasText: 'Nothing is deleted' }));
  await expect(confirm).toContainText('Nothing is deleted');
  await confirm.getByRole('button', { name: 'Forget history' }).click();
  await expect(page.getByText(/Forgot [\d,]+ photos/)).toBeVisible();
  consoleWatch.assertClean();
});

for (const variant of ['light', 'rtl'] as const) {
  test(`iPhone: photos go into an app that shares files, and it says where (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    await page.goto(query(variant, { screen: 'photos', queue: '0' }));
    await page.getByTestId('to-app-open').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByTestId('to-app-apps')).toBeVisible();
    // Documents by Readdle is suggested and comes first.
    await expect(dialog.getByRole('radio').first()).toContainText('Documents');
    await dialog.getByTestId('to-app-input').setInputFiles([
      { name: 'Trip 1.jpg', mimeType: 'image/jpeg', buffer: Buffer.alloc(4096) },
      { name: 'Trip 2.mov', mimeType: 'video/quicktime', buffer: Buffer.alloc(8192) },
    ]);
    await expect(dialog.getByTestId('to-app-files')).toContainText('Trip 2.mov');
    await settle(page);
    await shot(page, `photos-to-app-dialog-${variant}`);
    await dialog.getByTestId('to-app-start').click();
    await expect(page.getByTestId('transfer-drawer')).toBeVisible();
    if (variant === 'light') {
      await expect(page.getByTestId('transfer-drawer')).toContainText('Adding 2 items to Documents');
      await expect(page.getByText('They’ll be inside Documents', { exact: false }).first()).toBeVisible();
    }
    consoleWatch.assertClean();
  });
}

test('iPhone: dropping photos on the grid opens the app chooser with them', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', queue: '0' }));
  const grid = page.locator('[data-testid=photo-grid]');
  await expect(grid).toBeVisible();
  const dataTransfer = await page.evaluateHandle(() => {
    const dt = new DataTransfer();
    dt.items.add(new File([new Uint8Array(2048)], 'From PC.heic', { type: 'image/heic' }));
    return dt;
  });
  await grid.dispatchEvent('dragenter', { dataTransfer });
  await expect(page.getByText('Drop to choose an app on the iPhone for these')).toBeVisible();
  await grid.dispatchEvent('drop', { dataTransfer });
  await expect(page.getByRole('dialog').getByTestId('to-app-files')).toContainText('From PC.heic');
  consoleWatch.assertClean();
});

test('Settings has the photo backup defaults', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'settings', queue: '0' }));
  const card = page.getByTestId('settings-photo-backup');
  await expect(card.getByTestId('settings-backup-folder')).toHaveText('D:\\Phone backups');
  await card.getByRole('radio', { name: 'One folder' }).click();
  await expect(card.getByRole('radio', { name: 'One folder' })).toHaveAttribute('aria-checked', 'true');
  await settle(page);
  await shot(page, 'settings-photo-backup-light');
  consoleWatch.assertClean();
});
