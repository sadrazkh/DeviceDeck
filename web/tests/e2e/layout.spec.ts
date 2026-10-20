import { expect, test } from '@playwright/test';
import { layoutProblems } from './layout';
import { query, settle, type Variant } from './helpers';

// Overview at the window sizes people use, in light, dark and right-to-left Persian: no text cut without a tooltip,
// no label broken over two lines, nothing drawn over anything else or out of its card.

const SIZES = [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const;
const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];
const PHONES: { name: string; params: Record<string, string> }[] = [
  { name: 'iphone', params: {} },
  { name: 'android', params: { device: 'android' } },
  { name: 'limited', params: { mock: 'limited' } },
];

for (const [width, height] of SIZES) {
  for (const variant of VARIANTS) {
    test(`overview fits at ${width}×${height} (${variant})`, async ({ page }) => {
      await page.setViewportSize({ width, height });
      for (const phone of PHONES) {
        await page.goto(query(variant, { screen: 'overview', ...phone.params }));
        await expect(page.getByTestId('screen-overview')).toBeVisible();
        await expect(page.getByTestId('details-card').locator('dd').first()).toBeVisible();
        await settle(page);
        expect(await layoutProblems(page), `${phone.name} at ${width}×${height} (${variant})`).toEqual([]);
      }
    });
  }
}

// The round-2 screens, the same way; clean-up after its analysis, when every card is filled.
const SCREENS: { name: string; params: Record<string, string>; ready: string; analyse?: boolean }[] = [
  { name: 'backup', params: { screen: 'backup' }, ready: '[data-testid=backup-row]' },
  { name: 'backup-android', params: { screen: 'backup', device: 'android' }, ready: '[data-testid=backup-row]' },
  { name: 'contacts', params: { screen: 'contacts' }, ready: '[data-testid=contact-details]' },
  { name: 'battery', params: { screen: 'battery' }, ready: '[data-testid=battery-chart]' },
  { name: 'battery-android', params: { screen: 'battery', device: 'android' }, ready: '[data-testid=battery-chart]' },
  { name: 'cleanup', params: { screen: 'cleanup', device: 'android' }, ready: '[data-testid=screen-cleanup]', analyse: true },
  { name: 'toolbox', params: { screen: 'toolbox', device: 'android' }, ready: '[data-testid=tool-tile-mirror]' },
  { name: 'tool-crash', params: { screen: 'toolbox', tool: 'crash' }, ready: '[data-testid=crash-row]' },
  { name: 'tool-log', params: { screen: 'toolbox', tool: 'log' }, ready: '[data-testid=log-row]' },
  { name: 'settings', params: { screen: 'settings' }, ready: '[data-testid=screen-settings]' },
];

for (const [width, height] of SIZES) {
  for (const variant of VARIANTS) {
    test(`round-2 screens fit at ${width}×${height} (${variant})`, async ({ page }) => {
      test.setTimeout(180_000);
      await page.setViewportSize({ width, height });
      for (const screen of SCREENS) {
        await page.goto(query(variant, { queue: '0', ...screen.params }));
        await expect(page.locator(screen.ready).first()).toBeVisible({ timeout: 15_000 });
        if (screen.analyse) {
          await page.getByTestId('cleanup-analyze').click();
          await expect(page.getByTestId('cleanup-body')).toHaveAttribute('data-state', 'done', { timeout: 15_000 });
        }
        await settle(page);
        expect(await layoutProblems(page), `${screen.name} at ${width}×${height} (${variant})`).toEqual([]);
      }
    });
  }
}
