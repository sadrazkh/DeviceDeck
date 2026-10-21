import { expect, test, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { layoutProblems } from '../e2e/layout';
import { domClick, launch, openScreen, type Host } from './host';

// A read-only scan of App documents in the real host, through DevTools (never the mouse): at 1024×680 and 1280×800,
// in English and Persian, it opens every app on the phone and the folders inside (down to DD_SCAN_DEPTH, at most
// DD_SCAN_FOLDERS per folder) and runs the layout check on each view. It only lists: nothing is added, renamed,
// exported or deleted. The page size is set with DevTools' device metrics; the window itself is not touched.
//
// The final phase, on the owner's iPhone:  DD_PROVIDERS=ios npm run test:host -- scan
// Without DD_PROVIDERS it scans the fake phones (to check the scan itself).
// Report: test-results/host/app-documents-scan.json (one entry per view, with its problems).

const PROVIDERS = process.env.DD_PROVIDERS ?? 'fake';
const DEPTH = Number(process.env.DD_SCAN_DEPTH ?? 1);
const FOLDERS = Number(process.env.DD_SCAN_FOLDERS ?? 10);
const ROOT = process.env.DD_TEST_ROOT ?? 'E:\\DeviceDeck-test';
const SIZES = [
  [1024, 680],
  [1280, 800],
] as const;
const LANGUAGES = [
  ['en', 'English'],
  ['fa', 'فارسی'],
] as const;

interface View {
  view: string;
  problems: string[];
}

let host: Host;
const report: View[] = [];

test.describe.configure({ mode: 'serial' });

test.beforeAll(async () => {
  host = await launch({ providers: PROVIDERS, data: path.join(ROOT, `ui-scan-data-${PROVIDERS}`) });
});

test.afterAll(async () => {
  if (host) {
    await setLanguage(host.page, 'en', 'English').catch(() => undefined);
    await host.close();
  }
  mkdirSync('test-results/host', { recursive: true });
  writeFileSync('test-results/host/app-documents-scan.json', JSON.stringify(report, null, 2));
});

async function setLanguage(page: Page, lang: string, label: string) {
  await openScreen(page, 'settings');
  await domClick(page, `[data-testid=screen-settings] [role=radio]:text-is("${label}")`);
  await page.waitForFunction((lang) => document.documentElement.lang === lang, lang);
}

/** Waits until the view has stopped loading: no skeletons, no "Counting…". */
async function settled(page: Page) {
  await page.waitForFunction(
    () =>
      !document.querySelector('[data-testid=content] [class*=_skeleton]') &&
      !Array.from(document.querySelectorAll('[data-testid=app-card-meta]')).some((m) => (m.textContent ?? '').includes('…')),
    undefined,
    { timeout: 120_000 },
  );
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(400);
}

async function check(page: Page, view: string) {
  await settled(page);
  report.push({ view, problems: await layoutProblems(page) });
}

/** App documents' grid (the screen remembers an opened app: go up out of it first). */
async function openGrid(page: Page) {
  await openScreen(page, 'appDocuments');
  for (let i = 0; i < 20 && (await page.getByTestId('file-browser').count()) > 0; i++) await domClick(page, '[data-testid=browser-up]');
  await expect(page.getByTestId('screen-appDocuments')).toBeVisible();
}

/** The open folder's subfolders that can be opened (not greyed by the phone). */
async function subfolders(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    Array.from(document.querySelectorAll('[data-testid=file-table] [role=row][id]'))
      .filter((row) => !row.hasAttribute('data-blocked') && row.querySelector('[data-kind=folder]'))
      .map((row) => row.querySelector('[title]')?.getAttribute('title') ?? '')
      .filter(Boolean),
  );
}

async function walk(page: Page, view: string, depth: number) {
  if (depth >= DEPTH) return;
  const names = (await subfolders(page)).slice(0, FOLDERS);
  const browser = page.getByTestId('file-browser');
  const here = (await browser.getAttribute('data-path')) ?? '/';
  for (const name of names) {
    const row = page.locator('[data-testid=file-table] [role=row][id]').filter({ has: page.locator(`[title="${name.replace(/"/g, '\\"')}"]`) });
    await row.first().dispatchEvent('dblclick');
    await expect(browser).toHaveAttribute('data-path', here === '/' ? `/${name}` : `${here}/${name}`, { timeout: 60_000 });
    await check(page, `${view} / ${name}`);
    await walk(page, `${view} / ${name}`, depth + 1);
    await domClick(page, '[data-testid=browser-up]');
    await expect(browser).toHaveAttribute('data-path', here, { timeout: 60_000 });
  }
}

for (const [lang, label] of LANGUAGES) {
  for (const [width, height] of SIZES) {
    test(`App documents, every app opened, at ${width}×${height} (${lang})`, async () => {
      test.setTimeout(30 * 60_000);
      const { page } = host;
      await setLanguage(page, lang, label);
      await page.setViewportSize({ width, height });
      // DevTools' device metrics really size the page in WebView2 (the window stays as it is).
      expect(await page.evaluate(() => [innerWidth, innerHeight])).toEqual([width, height]);
      const tag = `${width}×${height} ${lang}`;

      await openGrid(page);
      await check(page, `${tag}: grid`);
      const cards = await page.evaluate(() => Array.from(document.querySelectorAll('[data-testid^="app-card-"][data-testid*=":"]')).map((c) => c.getAttribute('data-testid')!));
      expect(cards.length, 'no app shares files on this phone').toBeGreaterThan(0);

      for (const card of cards) {
        await openGrid(page);
        await domClick(page, `[data-testid="${card}"]`);
        await expect(page.getByTestId('file-browser')).toBeVisible({ timeout: 60_000 });
        const view = `${tag}: ${card.replace('app-card-', '')}`;
        await check(page, view);
        await walk(page, view, 0);
      }

      const failing = report.filter((v) => v.view.startsWith(tag) && v.problems.length > 0);
      const views = report.filter((v) => v.view.startsWith(tag)).length;
      console.log(`${tag}: ${views} views, ${failing.length} with problems`);
      expect(failing, failing.map((v) => `${v.view}\n  ${v.problems.join('\n  ')}`).join('\n')).toEqual([]);
    });
  }
}
