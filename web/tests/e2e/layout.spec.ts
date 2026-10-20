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
