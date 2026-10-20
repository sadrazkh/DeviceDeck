import { expect, type ConsoleMessage, type Page } from '@playwright/test';

/** Collects console errors and uncaught page errors; call `assertClean()` at the end of a test. */
export function watchConsole(page: Page) {
  const problems: string[] = [];
  page.on('console', (message: ConsoleMessage) => {
    if (message.type() === 'error' || message.type() === 'warning') {
      const text = message.text();
      // Vite's dev client and React's DevTools hint are not page problems.
      if (/Download the React DevTools|\[vite\]/.test(text)) return;
      problems.push(`${message.type()}: ${text}`);
    }
  });
  page.on('pageerror', (error) => problems.push(`pageerror: ${error.message}`));
  return {
    problems,
    assertClean() {
      expect(problems, problems.join('\n')).toEqual([]);
    },
  };
}

export type Variant = 'light' | 'dark' | 'rtl';

export function query(variant: Variant, extra: Record<string, string> = {}): string {
  const params = new URLSearchParams({ latency: '0', indexing: '0', ...extra });
  if (variant === 'rtl') {
    params.set('lang', 'fa');
    params.set('theme', 'light');
  } else {
    params.set('theme', variant);
  }
  return `/?${params.toString()}`;
}

/** Waits for thumbnails and icons in view to finish loading and for motion to settle. */
export async function settle(page: Page) {
  await page.waitForFunction(() => Array.from(document.images).every((img) => img.complete));
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(450);
}

export async function shot(page: Page, name: string) {
  await page.screenshot({ path: `test-results/screens/${name}.png` });
}
