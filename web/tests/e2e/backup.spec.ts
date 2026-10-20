import { expect as baseExpect, test, type Page } from '@playwright/test';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// Backup, restore, the backup browser, contacts and messages, against the mock backend.
// Simulated time is moved on explicitly (fastForward), so the waits only cover a slow machine.
const expect = baseExpect.configure({ timeout: 15_000 });

const ANDROID = 'R5CY10QWERT';

/**
 * Loads a screen and waits until it has settled before anything is clicked: the shell remounts the first screen once
 * just after it appears, and a dialog opened in that moment would be dropped with it.
 */
async function open(page: Page, params: Record<string, string>, ready: string, variant: Variant = 'light') {
  await page.goto(query(variant, { queue: '0', ...params }));
  await expect(page.locator(ready).first()).toBeVisible();
  await settle(page);
}

/** Runs the mock's simulated time forward (backups take ~15 s of it). */
async function fastForward(page: Page, ms: number) {
  await page.evaluate((total) => {
    const mock = (window as unknown as { deviceDeckMock: { tick(ms: number): void } }).deviceDeckMock;
    for (let t = 0; t < total; t += 250) mock.tick(250);
  }, ms);
}

const SCREENS: { name: string; params: Record<string, string>; ready: string }[] = [
  { name: 'backup', params: { screen: 'backup' }, ready: '[data-testid=backup-row]' },
  { name: 'backup-android', params: { screen: 'backup', device: 'android' }, ready: '[data-testid=backup-row]' },
  { name: 'contacts', params: { screen: 'contacts' }, ready: '[data-testid=contact-details]' },
  { name: 'contacts-android', params: { screen: 'contacts', device: 'android' }, ready: '[data-testid=contacts-android]' },
];

for (const screen of SCREENS) {
  for (const variant of ['light', 'dark', 'rtl'] as Variant[]) {
    test(`${screen.name} (${variant}) renders cleanly`, async ({ page }) => {
      const consoleWatch = watchConsole(page);
      await page.goto(query(variant, { queue: '0', ...screen.params }));
      await expect(page.locator(screen.ready).first()).toBeVisible();
      await settle(page);
      await shot(page, `${screen.name}-${variant}`);
      consoleWatch.assertClean();
    });
  }
}

test('messages tab shows a conversation with the newest message at the bottom', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  for (const variant of ['light', 'rtl'] as const) {
    await open(page, { screen: 'contacts' }, '[data-testid=contact-details]', variant);
    await page.getByRole('radio', { name: variant === 'rtl' ? 'پیام‌ها' : 'Messages' }).click();
    await expect(page.getByTestId('message').first()).toBeVisible();
    const thread = page.getByTestId('message-thread');
    await expect.poll(() => thread.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight)).toBeLessThan(4);
    await settle(page);
    await shot(page, `messages-${variant}`);
  }
  consoleWatch.assertClean();
});

test('Back up now → progress → done → older backup offered → deleted after confirming', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await open(page, { screen: 'backup' }, '[data-testid=backup-row]');
  await expect(page.getByTestId('backup-list').getByTestId('backup-row')).toHaveCount(3);

  await page.getByTestId('backup-now').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Only what changed since');
  await settle(page);
  await shot(page, 'flow-backup-wizard-light');
  await dialog.getByTestId('backup-start').click();
  await expect(dialog.getByTestId('backup-job')).toBeVisible();
  await fastForward(page, 6000);
  await expect(dialog.getByTestId('backup-job')).toHaveAttribute('data-phase', 'transferring');
  await settle(page);
  await shot(page, 'flow-backup-progress-light');

  await fastForward(page, 12_000);
  await expect(dialog.getByTestId('backup-done')).toBeVisible();
  await expect(dialog.getByTestId('backup-prune')).toContainText('Delete older backups?');
  await settle(page);
  await shot(page, 'flow-backup-done-light');
  await dialog.getByTestId('backup-prune-review').click();

  const confirm = page.getByRole('dialog');
  await expect(confirm.getByTestId('doomed-backups').locator('li')).toHaveCount(1);
  await confirm.getByTestId('delete-backups-confirm').click();
  await expect(page.getByRole('dialog')).toBeHidden();
  await expect(page.getByTestId('backup-list').getByTestId('backup-row')).toHaveCount(3);
  consoleWatch.assertClean();
});

test('a pulled cable pauses the backup and it carries on when the phone is back', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await open(page, { screen: 'backup', device: 'android' }, '[data-testid=backup-now]');
  await page.getByTestId('backup-now').click();
  await page.getByRole('dialog').getByTestId('backup-start').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Keep running in the background' }).click();
  await fastForward(page, 5000);
  await page.evaluate((id) => (window as unknown as { deviceDeckMock: { unplug(id: string): void } }).deviceDeckMock.unplug(id), ANDROID);
  await fastForward(page, 500);
  // The screen goes back to Welcome while the phone is away; the job waits.
  await page.evaluate((id) => (window as unknown as { deviceDeckMock: { plugBack(id: string): void } }).deviceDeckMock.plugBack(id), ANDROID);
  await fastForward(page, 1500);
  await expect(page.getByTestId('backup-job-card')).toBeVisible();
  await fastForward(page, 15_000);
  await expect(page.getByTestId('backup-job-card')).toBeHidden();
  await expect(page.getByTestId('backup-row').first()).toHaveAttribute('data-state', 'complete');
  consoleWatch.assertClean();
});

test('restore asks for the phone’s name before it starts', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await open(page, { screen: 'backup' }, '[data-testid=restore-open]');
  await page.getByTestId('restore-open').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('restore-choice')).toHaveCount(4);
  await settle(page);
  await shot(page, 'flow-restore-choose-light');
  await dialog.getByTestId('restore-next').click();
  await expect(dialog).toContainText('Turn off Find My');
  await dialog.getByTestId('restore-next').click();

  const start = dialog.getByTestId('restore-start');
  await expect(start).toBeDisabled();
  await dialog.getByRole('textbox').fill('Nima');
  await expect(start).toBeDisabled();
  await dialog.getByRole('textbox').fill('nima’s iphone');
  await expect(start).toBeEnabled();
  await settle(page);
  await shot(page, 'flow-restore-confirm-light');
  await start.click();
  await expect(dialog.getByTestId('backup-job')).toBeVisible();
  await fastForward(page, 14_000);
  // The phone restarts at once, so the page shows Welcome and says it in a toast.
  await expect(page.getByText('Nima’s iPhone was restored')).toBeVisible();
  consoleWatch.assertClean();
});

test('turning on backup encryption waits for the passcode on the phone', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await open(page, { screen: 'backup' }, '[data-testid=encryption-on]');
  await page.getByTestId('encryption-on').click();
  const dialog = page.getByRole('dialog');
  await dialog.getByTestId('encryption-password').fill('secret');
  await dialog.getByTestId('encryption-repeat').fill('secrex');
  await expect(dialog).toContainText('The passwords don’t match.');
  await expect(dialog.getByTestId('encryption-submit')).toBeDisabled();
  await dialog.getByTestId('encryption-repeat').fill('secret');
  await dialog.getByTestId('encryption-submit').click();
  await fastForward(page, 600);
  await expect(dialog).toContainText('Enter the passcode on Nima’s iPhone to confirm');
  await fastForward(page, 3500);
  await expect(dialog).toContainText('are encrypted from now on');
  await dialog.getByRole('button', { name: 'Close' }).last().click();
  await expect(page.getByTestId('encryption-off')).toBeVisible();
  consoleWatch.assertClean();
});

test('an encrypted backup opens with its password and its files extract to the PC', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await open(page, { screen: 'backup' }, '[data-testid=backup-others] [data-testid=backup-row]');
  const old = page.getByTestId('backup-others').getByTestId('backup-row').filter({ hasText: 'iPhone 12' });
  await old.getByTestId('backup-browse').click();
  const dialog = page.getByRole('dialog');
  await dialog.getByTestId('unlock-password').fill('wrong');
  await dialog.getByTestId('unlock-submit').click();
  await expect(dialog).toContainText('That isn’t the password of this backup.');
  await dialog.getByTestId('unlock-password').fill('1234');
  await dialog.getByTestId('unlock-submit').click();

  await expect(page.getByTestId('backup-browser')).toBeVisible();
  await page.locator('[data-testid=backup-domain][data-domain=HomeDomain]').click();
  await page.getByTestId('backup-files').getByRole('button', { name: 'Library' }).click();
  await expect(page.getByTestId('backup-file').first()).toBeVisible();
  await settle(page);
  await shot(page, 'flow-backup-browser-light');
  await page.getByTestId('backup-extract').click();
  await expect(page.getByText(/Extracting to/)).toBeVisible();
  await fastForward(page, 3000);
  await expect(page.getByText(/Files extracted from the backup of iPhone 12/)).toBeVisible();
  await page.getByTestId('browser-back').click();
  await expect(page.getByTestId('screen-backup')).toBeVisible();
  consoleWatch.assertClean();
});

test('contacts export as vCard from the selected contacts', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await open(page, { screen: 'contacts' }, '[data-testid=contact-row]');
  const rows = page.getByTestId('contact-row');
  await rows.nth(1).getByRole('checkbox').click();
  await rows.nth(2).getByRole('checkbox').click();
  await page.getByTestId('contacts-export').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('Export 2 contacts');
  await settle(page);
  await shot(page, 'flow-contacts-export-light');
  await dialog.getByTestId('contacts-export-start').click();
  await fastForward(page, 1500);
  await expect(page.getByText('Saved from the backup of Nima’s iPhone')).toBeVisible();
  consoleWatch.assertClean();
});

test('Overview’s Back up now opens the backup wizard', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await open(page, { screen: 'overview' }, '[data-testid=overview-backup]');
  await page.getByTestId('overview-backup').click();
  await expect(page.getByTestId('screen-backup')).toBeVisible();
  await expect(page.getByRole('dialog')).toContainText(`Back up Nima’s iPhone`);
  consoleWatch.assertClean();
});
