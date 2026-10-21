import { expect, test, type Page } from '@playwright/test';
import { query, settle, shot, watchConsole, type Variant } from './helpers';
import { layoutProblems } from './layout';

// "Add to iPhone" (round 3): straight into Photos with LocalSend first, over the cable or Wi-Fi; "into an app" second.
// The mock's LocalSend answers per ?localsend=… (see src/bridge/mock/localSend.ts).

async function openDialog(page: Page, variant: Variant, localsend?: string) {
  await page.goto(query(variant, { screen: 'photos', queue: '0', ...(localsend ? { localsend } : {}) }));
  await page.getByTestId('to-app-open').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  return dialog;
}

async function addFiles(dialog: ReturnType<Page['getByRole']>) {
  await dialog.getByTestId('to-app-input').setInputFiles([
    { name: 'DeviceDeck test 1-2.jpg', mimeType: 'image/jpeg', buffer: Buffer.alloc(4096) },
    { name: 'IMG_0002.HEIC', mimeType: 'image/heic', buffer: Buffer.alloc(8192) },
  ]);
  await expect(dialog.getByTestId('to-app-files')).toContainText('IMG_0002.HEIC');
}

for (const variant of ['light', 'dark', 'rtl'] as const) {
  test(`LocalSend is the first way, finds the iPhone over USB and sends straight into Photos (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    const dialog = await openDialog(page, variant);
    // The recommended way is chosen already.
    await expect(dialog.getByTestId('to-app-routes').getByRole('radio').first()).toHaveAttribute('aria-checked', 'true');
    await expect(dialog.getByTestId('localsend-status')).toHaveAttribute('data-state', 'found-usb');
    if (variant !== 'rtl') {
      await expect(dialog.getByTestId('localsend-status')).toContainText('Found «Clever Mango» over USB');
      await expect(dialog).toContainText('Install LocalSend from the App Store');
    } else {
      await expect(dialog.getByTestId('localsend-status')).toContainText('از راه USB پیدا شد');
      await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
    }
    await addFiles(dialog);
    await settle(page);
    expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
    await shot(page, `localsend-dialog-${variant}`);

    await dialog.getByTestId('to-app-start').click();
    const progress = dialog.getByTestId('localsend-progress');
    await expect(progress).toHaveAttribute('data-phase', 'waitingForAccept');
    if (variant === 'light') await expect(progress).toContainText('tap Accept in LocalSend');
    await settle(page);
    expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
    await shot(page, `localsend-waiting-${variant}`);

    await expect(progress).toHaveAttribute('data-state', 'done', { timeout: 20_000 });
    if (variant === 'light') await expect(progress).toContainText('Done: 2 files are in Photos now.');
    if (variant === 'rtl') await expect(progress).toContainText('اکنون در Photos است');
    await settle(page);
    await shot(page, `localsend-done-${variant}`);
    await dialog.getByTestId('localsend-close').click();
    await expect(dialog).toBeHidden();
    consoleWatch.assertClean();
  });
}

test('a PIN: the dialog asks for it, says when it is wrong, and sends with the right one', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const dialog = await openDialog(page, 'light', 'pin');
  await addFiles(dialog);
  await dialog.getByTestId('to-app-start').click();
  const progress = dialog.getByTestId('localsend-progress');
  await expect(progress).toContainText('LocalSend asks for a PIN');
  const pin = dialog.getByLabel('The PIN set in LocalSend’s settings');
  await pin.fill('0000');
  await dialog.getByTestId('localsend-pin-send').click();
  await expect(progress).toContainText('That PIN didn’t match');
  await pin.fill('123456');
  await settle(page);
  await shot(page, 'localsend-pin-light');
  await dialog.getByTestId('localsend-pin-send').click();
  await expect(progress).toHaveAttribute('data-state', 'done', { timeout: 20_000 });
  consoleWatch.assertClean();
});

test('declined on the iPhone: says so with what to do, and offers to try again', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const dialog = await openDialog(page, 'light', 'reject');
  await addFiles(dialog);
  await dialog.getByTestId('to-app-start').click();
  const progress = dialog.getByTestId('localsend-progress');
  await expect(progress).toHaveAttribute('data-state', 'failed', { timeout: 20_000 });
  await expect(progress).toContainText('It didn’t go');
  await expect(progress).toContainText('tap Accept, or turn on Quick Save');
  await expect(dialog.getByTestId('localsend-results')).toContainText('Didn’t go');
  await expect(dialog.getByTestId('localsend-retry')).toBeVisible();
  consoleWatch.assertClean();
});

test('busy the first time: Try again sends', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const dialog = await openDialog(page, 'light', 'busy');
  await addFiles(dialog);
  await dialog.getByTestId('to-app-start').click();
  const progress = dialog.getByTestId('localsend-progress');
  await expect(progress).toContainText('busy with another transfer');
  await dialog.getByTestId('localsend-retry').click();
  await expect(progress).toHaveAttribute('data-state', 'done', { timeout: 20_000 });
  consoleWatch.assertClean();
});

test('LocalSend not open yet: says to open it, then finds it once it is', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const dialog = await openDialog(page, 'light', 'opening');
  await expect(dialog.getByTestId('localsend-status')).toHaveAttribute('data-state', 'notListening');
  await expect(dialog.getByTestId('localsend-status')).toContainText('LocalSend isn’t open on Nima’s iPhone');
  await expect(dialog.getByTestId('to-app-start')).toBeDisabled();
  await expect(dialog.getByTestId('localsend-status')).toHaveAttribute('data-state', 'found-usb', { timeout: 20_000 });
  consoleWatch.assertClean();
});

for (const variant of ['light', 'rtl'] as const) {
  test(`not answering over the cable: Wi-Fi finds the iPhone and sends there (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    const dialog = await openDialog(page, variant, 'wifi');
    await expect(dialog.getByTestId('localsend-status')).toHaveAttribute('data-state', 'notAnswering');
    await dialog.getByTestId('localsend-wifi-look').click();
    const list = dialog.getByTestId('localsend-wifi-list');
    await expect(list).toBeVisible({ timeout: 20_000 });
    await expect(list.getByRole('radio').first()).toContainText('Nima’s iPhone');
    await expect(list.getByRole('radio').first()).toHaveAttribute('aria-checked', 'true');
    await expect(dialog.getByTestId('localsend-status')).toHaveAttribute('data-state', 'found-wifi');
    await addFiles(dialog);
    await settle(page);
    expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
    await shot(page, `localsend-wifi-${variant}`);
    await dialog.getByTestId('to-app-start').click();
    await expect(dialog.getByTestId('localsend-progress')).toHaveAttribute('data-state', 'done', { timeout: 20_000 });
    consoleWatch.assertClean();
  });
}

test('cancelling while it waits for Accept stops the send', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const dialog = await openDialog(page, 'light');
  await addFiles(dialog);
  await dialog.getByTestId('to-app-start').click();
  await expect(dialog.getByTestId('localsend-progress')).toHaveAttribute('data-phase', 'waitingForAccept');
  await dialog.getByTestId('localsend-cancel').click();
  await expect(dialog.getByTestId('localsend-progress')).toHaveAttribute('data-state', 'cancelled');
  await expect(dialog.getByTestId('localsend-progress')).toContainText('Cancelled');
  consoleWatch.assertClean();
});

for (const variant of ['light', 'rtl'] as const) {
  test(`into an app: a dated folder, and the steps to save them all at once (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    const dialog = await openDialog(page, variant);
    await dialog.getByTestId('to-app-routes').getByRole('radio').nth(1).click();
    await expect(dialog.getByTestId('to-app-apps')).toBeVisible();
    await addFiles(dialog);
    await expect(dialog.getByTestId('to-app-save-all')).toContainText('Select All');
    await settle(page);
    expect(await layoutProblems(page, '[role=dialog]')).toEqual([]);
    await shot(page, `to-app-steps-${variant}`);
    await dialog.getByTestId('to-app-start').click();
    await expect(dialog.getByTestId('to-app-sent')).toBeVisible();
    await expect(dialog.getByTestId('to-app-path').first()).toContainText(/Files › On My iPhone › Documents › DeviceDeck \d{4}-\d{2}-\d{2}/);
    await expect(dialog.getByTestId('to-app-path').first()).toHaveCSS('direction', 'ltr');
    await settle(page);
    await shot(page, `to-app-sent-${variant}`);
    await dialog.getByTestId('to-app-done').click();
    await expect(dialog).toBeHidden();
    consoleWatch.assertClean();
  });
}

test('dropping photos on the Photos screen opens the same dialog, LocalSend first', async ({ page }) => {
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
  await expect(page.getByText('Drop to add these to the iPhone')).toBeVisible();
  await grid.dispatchEvent('drop', { dataTransfer });
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('to-app-files')).toContainText('From PC.heic');
  await expect(dialog.getByTestId('localsend-status')).toBeVisible();
  await expect(dialog.getByTestId('to-app-start')).toContainText('Send 1 file to Photos');
  consoleWatch.assertClean();
});
