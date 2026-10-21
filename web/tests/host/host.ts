import { chromium, type Browser, type Page } from '@playwright/test';
import { execFileSync, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Starts DeviceDeck.exe for a check, connects to its page over the WebView2 DevTools port, and quits it again.
// Never the mouse, the keyboard or the screen: everything goes through the page's DOM.

export const CDP_PORT = Number(process.env.DD_CDP_PORT ?? 9361);
export const DEV_PORT = Number(process.env.DD_DEV_PORT ?? 5211);
export const DEV_URL = `http://localhost:${DEV_PORT}/`;

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
export const EXE = process.env.DD_EXE ?? path.join(repo, 'src/DeviceDeck.Shell/bin/Debug/net10.0-windows10.0.19041.0/win-x64/DeviceDeck.exe');

/** Is a DeviceDeck window (any build) running? Only one can: a check must never quit someone else's. */
export function running(): boolean {
  const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq DeviceDeck.exe', '/NH', '/FO', 'CSV'], { encoding: 'utf8' });
  return out.includes('"DeviceDeck.exe"');
}

export interface Host {
  browser: Browser;
  page: Page;
  /** Quits the app this check started (DeviceDeck.exe --quit). */
  close(): Promise<void>;
}

/**
 * Starts DeviceDeck with the page from this worktree's dev server and DevTools on CDP_PORT.
 * `providers`: DEVICEDECK_PROVIDERS ("fake" for the sample phones, "ios" for the owner's iPhone).
 * `data`: its own data folder (settings, logs), never the owner's.
 */
export async function launch(options: { providers: string; data: string }): Promise<Host> {
  if (!existsSync(EXE)) throw new Error(`${EXE} is missing: run "dotnet build DeviceDeck.slnx" first.`);
  if (running()) throw new Error('DeviceDeck is already running. It is left alone: close it yourself, then run this again.');

  const child = spawn(EXE, [], {
    detached: true,
    stdio: 'ignore',
    env: {
      ...process.env,
      DEVICEDECK_DEV: '1',
      DEVICEDECK_DEV_URL: DEV_URL,
      DEVICEDECK_PROVIDERS: options.providers,
      DEVICEDECK_DATA: options.data,
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${CDP_PORT}`,
    },
  });
  child.unref();

  const deadline = Date.now() + 60_000;
  let browser: Browser | null = null;
  while (!browser) {
    try {
      browser = await chromium.connectOverCDP(`http://127.0.0.1:${CDP_PORT}`);
    } catch (error) {
      if (Date.now() > deadline) throw new Error(`DevTools never answered on port ${CDP_PORT}: ${String(error)}`);
      await new Promise((r) => setTimeout(r, 500));
    }
  }

  let page: Page | undefined;
  while (!page) {
    page = browser.contexts().flatMap((c) => c.pages()).find((p) => p.url().startsWith(DEV_URL));
    if (!page) {
      if (Date.now() > deadline) throw new Error(`No page from ${DEV_URL} in the window.`);
      await new Promise((r) => setTimeout(r, 250));
    }
  }
  await page.waitForLoadState('domcontentloaded');

  return {
    browser,
    page,
    async close() {
      await browser.close().catch(() => undefined);
      execFileSync(EXE, ['--quit'], { stdio: 'ignore' });
      const until = Date.now() + 30_000;
      while (running() && Date.now() < until) await new Promise((r) => setTimeout(r, 300));
    },
  };
}

/** A DOM click (element.click()): no pointer input at all, so nothing on the desktop moves. */
export async function domClick(page: Page, selector: string) {
  await page.locator(selector).first().waitFor();
  await page.locator(selector).first().evaluate((el) => (el as HTMLElement).click());
}

/** Opens a screen from the navigation (section: overview, files, appDocuments, …). */
export async function openScreen(page: Page, section: string) {
  await domClick(page, `[data-testid=nav-${section}]`);
}

export interface DropReport {
  target?: string;
  hit?: string;
  error?: string;
  windows?: { class: string; depth: number; visible: boolean; dropTarget: boolean }[];
  calls?: { call: string; hr: number | string; effect?: number; error?: string }[];
}

/**
 * Asks the host (DEVICEDECK_DEV only) to play a File Explorer drop of `paths` at page point (x, y): the OLE drop
 * target its window registered gets DragEnter, DragOver and Drop with Explorer's own data object for those files.
 */
export async function hostDrop(page: Page, x: number, y: number, paths: string[], cancel = false): Promise<DropReport> {
  return page.evaluate(
    ({ x, y, paths, cancel }) =>
      new Promise<DropReport>((resolve, reject) => {
        const webview = (window as unknown as { chrome: { webview: { postMessage(m: unknown): void; addEventListener(t: string, l: (e: { data: unknown }) => void): void; removeEventListener(t: string, l: (e: { data: unknown }) => void): void } } }).chrome.webview;
        const timer = setTimeout(() => reject(new Error('The host never reported the drop.')), 20_000);
        const listener = (e: { data: unknown }) => {
          const data = e.data as { $dd?: string; report?: DropReport };
          if (data?.$dd !== 'dropSelfTest') return;
          clearTimeout(timer);
          webview.removeEventListener('message', listener);
          resolve(data.report ?? {});
        };
        webview.addEventListener('message', listener);
        webview.postMessage({ $dd: 'dropSelfTest', x, y, paths, cancel });
      }),
    { x, y, paths, cancel },
  );
}

/** Records the HTML drag events the page receives (capture phase, on the document). */
export async function recordDragEvents(page: Page) {
  await page.evaluate(() => {
    const w = window as unknown as { __drops: unknown[] };
    w.__drops = [];
    for (const type of ['dragenter', 'dragover', 'dragleave', 'drop']) {
      document.addEventListener(
        type,
        (e) => {
          const event = e as DragEvent;
          const target = (event.target as Element | null)?.closest?.('[data-testid]')?.getAttribute('data-testid') ?? null;
          w.__drops.push({
            type,
            x: Math.round(event.clientX),
            y: Math.round(event.clientY),
            target,
            types: Array.from(event.dataTransfer?.types ?? []),
            files: Array.from(event.dataTransfer?.files ?? []).map((f) => f.name),
          });
        },
        true,
      );
    }
  });
}

export async function dragEvents(page: Page) {
  return page.evaluate(
    () => (window as unknown as { __drops: { type: string; x: number; y: number; target: string | null; types: string[]; files: string[] }[] }).__drops,
  );
}

/** The centre of an element, in page (CSS) pixels. */
export async function centre(page: Page, selector: string) {
  const box = await page.locator(selector).first().boundingBox();
  if (!box) throw new Error(`${selector} is not on screen.`);
  return { x: Math.round(box.x + box.width / 2), y: Math.round(box.y + box.height / 2) };
}
