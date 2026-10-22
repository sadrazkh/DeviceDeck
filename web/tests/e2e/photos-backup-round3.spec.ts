import { expect, test } from '@playwright/test';
import { query, settle, shot, watchConsole } from './helpers';

// Photo backup, round 3: the date range, "When this phone connects", edited versions, and room on the destination.

const IPHONE = '00008150-001A2C3E0E91002E';
const ANDROID = 'R5CY10QWERT';

type Mock = {
  handle(method: string, params: unknown): unknown;
  unplug(id: string): void;
  plugBack(id: string): void;
  tick(ms: number): void;
  emit(event: string, params: unknown): void;
};

declare global {
  interface Window {
    deviceDeckMock: Mock;
  }
}

for (const variant of ['light', 'dark', 'rtl'] as const) {
  test(`the panel has the date range, when the phone connects and edited versions (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    const fa = variant === 'rtl';
    await page.goto(query(variant, { screen: 'photos', queue: '0' }));
    await page.getByTestId('backup-open').click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByTestId('backup-count')).toBeVisible();

    // Last 30 days: older photos are counted apart, and the phone now has options of its own.
    const range = dialog.getByTestId('backup-range');
    await range.getByRole('radio', { name: fa ? '۳۰ روز گذشته' : 'Last 30 days' }).click();
    await expect(dialog.getByTestId('backup-out-of-range')).toBeVisible();
    await expect(dialog.getByTestId('backup-use-defaults')).toBeVisible();
    // Since a date shows a day to pick.
    await range.getByRole('radio', { name: fa ? 'از یک تاریخ' : 'Since a date' }).click();
    const since = dialog.getByTestId('backup-range-since');
    await expect(since).toBeVisible();
    await since.fill('2026-10-01');
    await expect(since).toHaveValue('2026-10-01');

    // When this phone connects: the default is a notification.
    const onConnect = dialog.getByTestId('backup-on-connect');
    await expect(onConnect.getByRole('radio', { name: fa ? /عکس‌های تازه را خبر بده/ : /Tell me about new photos/ })).toHaveAttribute('aria-checked', 'true');
    const auto = onConnect.getByRole('radio', { name: fa ? /خودکار پشتیبان بگیر/ : /Back up new photos automatically/ });
    await auto.click();
    await expect(auto).toHaveAttribute('aria-checked', 'true');

    // Edited versions (iPhone): counted in the hero once switched on.
    await dialog.getByTestId('backup-save-edited').getByRole('switch').click();
    await expect(dialog.getByTestId('backup-count')).toContainText(fa ? 'نسخهٔ ویرایش‌شده' : 'edited version');

    await settle(page);
    await shot(page, `photos-backup-round3-panel-${variant}`);
    consoleWatch.assertClean();
  });
}

test('a destination short of space or gone is said before the button, with Choose another folder', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', queue: '0' }));
  await page.getByTestId('backup-open').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('backup-count')).toBeVisible();
  // A nearly full USB stick as the destination (any change in the panel brings the phone's status back with it).
  await page.evaluate((id) => window.deviceDeckMock.handle('photos.backupConfigure', { deviceId: id, destination: 'F:\\Phone backups' }), IPHONE);
  await dialog.getByTestId('backup-hidden').getByRole('radio', { name: 'Both' }).click();
  await expect(dialog.getByTestId('backup-space')).toHaveAttribute('data-state', 'short');
  await expect(dialog.getByTestId('backup-space')).toContainText('Not enough space on');
  await expect(dialog.getByTestId('backup-start')).toBeDisabled();
  await settle(page);
  await shot(page, 'photos-backup-round3-no-space-light');

  // The mock's folder picker offers D:\Photos\2026 first: plenty of room there.
  await dialog.getByTestId('backup-space-choose').click();
  await expect(dialog.getByTestId('backup-destination')).toHaveText('D:\\Photos\\2026');
  await expect(dialog.getByTestId('backup-space')).toHaveCount(0);
  await expect(dialog.getByTestId('backup-start')).toBeEnabled();

  // A drive that isn't there (any change in the panel brings the phone's status back with it).
  await page.evaluate((id) => window.deviceDeckMock.handle('photos.backupConfigure', { deviceId: id, destination: 'G:\\Phone backups' }), IPHONE);
  await dialog.getByRole('switch', { name: 'Convert HEIC to JPG' }).click();
  await expect(dialog.getByTestId('backup-space')).toHaveAttribute('data-state', 'missing');
  await expect(dialog.getByTestId('backup-space')).toContainText('isn’t connected');
  await expect(dialog.getByTestId('backup-start')).toBeDisabled();
  consoleWatch.assertClean();
});

for (const variant of ['light', 'rtl'] as const) {
  test(`Settings warns when the default backup folder's drive is nearly full (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    await page.goto(query(variant, { screen: 'settings', queue: '0' }));
    const card = page.getByTestId('settings-photo-backup');
    await expect(card.getByTestId('settings-backup-folder')).toBeVisible();
    await expect(card.getByTestId('settings-backup-space')).toHaveCount(0); // D: has room
    // The mock's folder picker offers D:\Photos\2026, E:\Backup\Phones, then C:\Users\Nima\Pictures (C: is as full as the owner's).
    const change = card.getByRole('button', { name: variant === 'rtl' ? 'تغییر' : 'Change' });
    for (const expected of ['D:\\Photos\\2026', 'E:\\Backup\\Phones', 'C:\\Users\\Nima\\Pictures']) {
      await change.click();
      await expect(card.getByTestId('settings-backup-folder')).toHaveText(expected);
      if (!expected.startsWith('C:')) await expect(card.getByTestId('settings-backup-space')).toHaveCount(0);
    }
    const warning = card.getByTestId('settings-backup-space');
    await expect(warning).toHaveAttribute('data-state', 'nearlyFull');
    await expect(warning).toContainText(variant === 'rtl' ? 'تقریباً پر است' : 'is nearly full');
    await settle(page);
    await shot(page, `settings-photo-backup-nearly-full-${variant}`);
    consoleWatch.assertClean();
  });
}

test('a phone set to back up automatically does so when it is plugged in, and the badge follows', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', device: 'android', queue: '0' }));
  const badge = page.getByTestId('backup-badge');
  await expect(badge).toHaveText('46');
  await page.evaluate((id) => {
    const mock = window.deviceDeckMock;
    mock.handle('photos.backupConfigure', { deviceId: id, onConnect: 'backUp' });
    mock.unplug(id);
    mock.plugBack(id);
    mock.tick(1_500);
  }, ANDROID);
  await expect(page.getByTestId('photo-job-banner')).toHaveAttribute('data-kind', 'photosBackup');

  // Real speed would take minutes: run the mock's clock ahead instead.
  await page.evaluate(() => {
    for (let i = 0; i < 4 * 60 * 30; i++) window.deviceDeckMock.tick(250);
  });
  await expect(badge).toBeHidden({ timeout: 15_000 });
  await page.getByTestId('backup-open').click();
  await expect(page.getByTestId('backup-last-run')).toContainText('started when the phone connected');
  consoleWatch.assertClean();
});

test('a click on the photo notification opens that phone’s Back up panel', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'overview', device: 'android', queue: '0' }));
  await expect(page.getByTestId('backup-open')).toHaveCount(0);
  await page.evaluate(
    (id) => window.deviceDeckMock.emit('notification.activated', { command: 'photos.openBackup', deviceId: id, tag: null, args: {} }),
    IPHONE,
  );
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('backup-count')).toBeVisible();
  consoleWatch.assertClean();
});
