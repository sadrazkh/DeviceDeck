import { expect, test } from '@playwright/test';
import { contrastProblems } from './contrast';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// The Toolbox screen and each tool, in light, dark and right-to-left Persian, against the mock backend.
// Screenshots go to test-results/screens/; each page must log no errors or warnings.

interface ToolCase {
  name: string;
  params: Record<string, string>;
  ready: string;
}

const TOOLS: ToolCase[] = [
  { name: 'toolbox', params: { screen: 'toolbox' }, ready: '[data-testid=tool-tile-log]' },
  { name: 'toolbox-android', params: { screen: 'toolbox', device: 'android' }, ready: '[data-testid=tool-tile-mirror]:not([disabled])' },
  { name: 'tool-log', params: { screen: 'toolbox', tool: 'log' }, ready: '[data-testid=log-row]' },
  { name: 'tool-crash', params: { screen: 'toolbox', tool: 'crash' }, ready: '[data-testid=crash-row]' },
  { name: 'tool-crash-android', params: { screen: 'toolbox', tool: 'crash', device: 'android' }, ready: '[data-testid=crash-row]' },
  { name: 'tool-report', params: { screen: 'toolbox', tool: 'report' }, ready: '[data-testid=report-preview]' },
  { name: 'tool-heic', params: { screen: 'toolbox', tool: 'heic' }, ready: '[data-testid=heic-drop]' },
  { name: 'tool-mirror', params: { screen: 'toolbox', tool: 'mirror', device: 'android' }, ready: '[data-testid=mirror-open]' },
  { name: 'tool-wifi', params: { screen: 'toolbox', tool: 'wifi' }, ready: '[data-testid=iphone-sync] [role=switch]' },
  { name: 'tool-wifi-android', params: { screen: 'toolbox', tool: 'wifi', device: 'android' }, ready: '[data-testid=android-pairing]' },
  { name: 'tool-ringtone', params: { screen: 'toolbox', tool: 'ringtone', device: 'android' }, ready: '[data-testid=ringtone-drop]' },
  { name: 'tool-ringtone-iphone', params: { screen: 'toolbox', tool: 'ringtone' }, ready: '[data-testid=ringtone-tool]' },
];

const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];

for (const tool of TOOLS) {
  for (const variant of VARIANTS) {
    test(`${tool.name} (${variant}) renders cleanly`, async ({ page }) => {
      const consoleWatch = watchConsole(page);
      await page.goto(query(variant, { queue: '0', ...tool.params }));
      await expect(page.locator(tool.ready).first()).toBeVisible();
      await expect(page.locator('html')).toHaveAttribute('dir', variant === 'rtl' ? 'rtl' : 'ltr');
      await settle(page);
      await shot(page, `${tool.name}-${variant}`);
      // Text contrast (WCAG AA) in both themes; Persian uses the light colours (desktop session's check).
      if (variant !== 'rtl') expect(await contrastProblems(page)).toEqual([]);
      consoleWatch.assertClean();
    });
  }
}

test('the tiles open each tool and come back', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'toolbox', device: 'android', queue: '0' }));
  await expect(page.getByTestId('tool-tile-log')).toBeEnabled();
  await settle(page); // the shell's opening transition re-mounts the first screen once
  for (const id of ['log', 'crash', 'report', 'heic', 'mirror', 'wifi', 'ringtone']) {
    await page.getByTestId(`tool-tile-${id}`).click();
    await expect(page.getByTestId(`tool-${id}`)).toBeVisible();
    await page.getByTestId('toolbox-back').click();
    await expect(page.getByTestId('toolbox-tiles')).toBeVisible();
  }
  consoleWatch.assertClean();
});

test('an iPhone shows mirroring as Android only', async ({ page }) => {
  await page.goto(query('light', { screen: 'toolbox', queue: '0' }));
  await expect(page.getByTestId('tool-tile-mirror')).toBeDisabled();
  await expect(page.getByTestId('tool-tile-ringtone')).toBeEnabled();
});

test('the live log filters, pauses and saves', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'toolbox', tool: 'log', device: 'android', queue: '0' }));
  await expect(page.getByTestId('log-row').first()).toBeVisible();
  await settle(page);
  await page.getByTestId('log-level').selectOption('error');
  await expect(page.getByTestId('log-row').first()).toBeVisible({ timeout: 15_000 });
  // One snapshot of the rows shown: only errors and faults pass the filter.
  const levels = await page.getByTestId('log-row').evaluateAll((rows) => rows.map((r) => r.querySelector('span[title]')?.getAttribute('title')));
  expect(levels.length).toBeGreaterThan(0);
  expect(levels.every((l) => l === 'error' || l === 'fault')).toBe(true);
  await page.getByTestId('log-pause').click();
  const frozen = await page.getByTestId('log-row').count();
  await page.waitForTimeout(1500);
  expect(await page.getByTestId('log-row').count()).toBe(frozen);
  await expect(page.getByTestId('log-status')).toContainText('Paused');
  await page.getByTestId('log-save').click();
  await expect(page.getByText(/Saved \d+ lines? of log/)).toBeVisible();
  consoleWatch.assertClean();
});

test('crash reports open, save, and delete only after confirming', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'toolbox', tool: 'crash', queue: '0' }));
  const rows = page.getByTestId('crash-row');
  await expect(rows.first()).toBeVisible();
  await settle(page);
  const count = await rows.count();
  await rows.first().click();
  await expect(page.getByTestId('crash-viewer')).toContainText('Process:');
  await page.getByTestId('crash-save').click();
  await expect(page.getByText('Saved 1 report')).toBeVisible();
  await page.getByTestId('crash-delete').click();
  await expect(page.getByRole('dialog')).toContainText('Delete 1 crash report from the phone?');
  await page.getByTestId('crash-delete-confirm').click();
  await expect(rows).toHaveCount(count - 1);
  consoleWatch.assertClean();
});

test('QR pairing shows a code and follows the stages', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'toolbox', tool: 'wifi', device: 'android', queue: '0' }));
  await expect(page.getByTestId('android-pairing')).toBeVisible();
  await settle(page);
  await page.getByRole('radio', { name: 'QR code' }).click();
  await expect(page.getByTestId('wifi-qr').locator('svg path')).toHaveCount(1);
  await expect(page.getByTestId('wifi-qr-stage')).toHaveText('Connected over Wi-Fi.', { timeout: 20_000 });
  consoleWatch.assertClean();
});

test('the mirror page opens for an Android phone (browser preview)', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(`/mirror.html?device=R5CY10QWERT&latency=0`);
  await expect(page.getByTestId('mirror-preview')).toBeVisible();
  await expect(page.getByTestId('mirror-home')).toBeEnabled();
  await page.getByTestId('mirror-rotate').click();
  await expect(page).toHaveTitle(/Galaxy S25 Ultra/);
  await shot(page, 'mirror-window');
  consoleWatch.assertClean();
});
