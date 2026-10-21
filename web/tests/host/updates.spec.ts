import { expect, test } from '@playwright/test';
import { createServer, type IncomingMessage, type Server } from 'node:http';
import { mkdirSync, writeFileSync } from 'node:fs';
import type { AddressInfo } from 'node:net';
import path from 'node:path';
import { ownerNotes } from '../../src/bridge/mock/updates';
import { domClick, launch, openScreen, type Host } from './host';

// Updates you can see (round 4) in the real window: a dev build is not installed by the Setup, so it is the portable
// case. GitHub's API is a local fake (DEVICEDECK_UPDATE_FEED); nothing goes online. No Windows notification either: the
// data folder's update history says 9.9.9 was already announced. Download is never clicked (it would open the browser).
//
// npm run test:host -- tests/host/updates.spec.ts   (in the updates worktree: DD_DEV_PORT=5221 DD_CDP_PORT=9371; a bare
// "updates" filter would match every spec there, since the worktree's own path has the word)

const ROOT = process.env.DD_TEST_ROOT ?? 'E:\\DeviceDeck-test';
const stamp = Date.now().toString(36);
const DATA = path.join(ROOT, 'updates-host', `run-${stamp}`);
const REPO = '/repos/sadrazkh/DeviceDeck/';

interface Seen {
  path: string;
  userAgent: string | undefined;
  ifNoneMatch: string | undefined;
  status: number;
}

let server: Server;
let feed = '';
const seen: Seen[] = [];

function release(version: string, body: string) {
  return JSON.stringify({
    tag_name: `v${version}`,
    name: `DeviceDeck v${version}`,
    html_url: `https://github.com/sadrazkh/DeviceDeck/releases/tag/v${version}`,
    body,
    draft: false,
    prerelease: false,
    published_at: '2026-11-02T10:00:00Z',
    assets: [],
  });
}

const ROUTES: Record<string, { etag: string; body: string }> = {
  'releases/latest': { etag: 'W/"latest-999"', body: release('9.9.9', ownerNotes('9.9.9')) },
  'releases/tags/v9.9.9': { etag: 'W/"tag-999"', body: release('9.9.9', ownerNotes('9.9.9')) },
  // No release for v0.3.0 here: What's new for it falls back to the notes built into the app.
};

function answer(request: IncomingMessage): { status: number; headers: Record<string, string>; body?: string } {
  const route = ROUTES[(request.url ?? '').replace(REPO, '')];
  const limits = { 'X-RateLimit-Limit': '60', 'X-RateLimit-Remaining': '59', 'X-RateLimit-Reset': '1900000000' };
  if (!route) return { status: 404, headers: { 'Content-Type': 'application/json', ...limits }, body: '{"message":"Not Found"}' };
  if (request.headers['if-none-match'] === route.etag) return { status: 304, headers: { ETag: route.etag, ...limits } };
  return { status: 200, headers: { 'Content-Type': 'application/json; charset=utf-8', ETag: route.etag, ...limits }, body: route.body };
}

test.beforeAll(async () => {
  server = createServer((request, response) => {
    const { status, headers, body } = answer(request);
    seen.push({ path: request.url ?? '', userAgent: request.headers['user-agent'], ifNoneMatch: request.headers['if-none-match'] as string | undefined, status });
    response.writeHead(status, headers).end(body);
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  feed = `http://127.0.0.1:${(server.address() as AddressInfo).port}${REPO}`;

  // 9.9.9 was already announced: no Windows notification on this PC during the check.
  mkdirSync(path.join(DATA, 'updates'), { recursive: true });
  writeFileSync(path.join(DATA, 'updates', 'state.json'), JSON.stringify({ notifiedVersion: '9.9.9' }));
});

test.afterAll(async () => {
  await new Promise((resolve) => server?.close(resolve));
});

test('the portable copy finds v9.9.9 on the (fake) GitHub, asks again with its ETag, and shows its notes', async () => {
  let host: Host | undefined;
  try {
    host = await launch({ providers: 'fake', data: DATA, env: { DEVICEDECK_UPDATE_FEED: feed, DEVICEDECK_WHATSNEW: '0' } });
    const { page } = host;
    await openScreen(page, 'settings');
    const card = page.getByTestId('settings-updates');
    await expect(card).toContainText('Portable zip (checks only)');
    await expect(card).toContainText('every 12 hours');

    await domClick(page, '[data-testid=check-updates]');
    await expect(page.getByTestId('update-button')).toHaveText('v9.9.9 on GitHub');
    await expect(card.getByTestId('update-status')).toContainText('Version 9.9.9 is on GitHub.');
    await expect(page.getByTestId('update-banner')).toContainText('DeviceDeck 9.9.9 is on GitHub');

    const latest = seen.filter((s) => s.path === `${REPO}releases/latest`);
    expect(latest).toHaveLength(1);
    expect(latest[0]!.userAgent).toMatch(/^DeviceDeck\/\d+\.\d+\.\d+.* \(\+https:\/\/github\.com\/sadrazkh\/DeviceDeck\)$/);
    expect(latest[0]!.ifNoneMatch).toBeUndefined();

    // Check now again: the same release, asked with its ETag (a 304).
    await domClick(page, '[data-testid=check-updates]');
    await expect.poll(() => seen.filter((s) => s.path === `${REPO}releases/latest`).length).toBe(2);
    expect(seen.filter((s) => s.path === `${REPO}releases/latest`)[1]).toMatchObject({ ifNoneMatch: 'W/"latest-999"', status: 304 });
    await expect(page.getByTestId('update-button')).toHaveText('v9.9.9 on GitHub');

    await domClick(page, '[data-testid=update-button]');
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('heading', { level: 2 })).toHaveText('What’s new in v9.9.9');
    await expect(dialog.getByTestId('release-notes').getByRole('heading', { level: 3 })).toContainText('DeviceDeck v9.9.9');
    await expect(dialog.getByTestId('whats-new-download')).toBeVisible();
    expect(seen.some((s) => s.path === `${REPO}releases/tags/v9.9.9`)).toBe(true);
    await domClick(page, '[role=dialog] button:has-text("Later")');
    await expect(dialog).toBeHidden();
  } finally {
    await host?.close();
  }
});

test('What’s new for this version comes from the notes built into the app when GitHub has none', async () => {
  let host: Host | undefined;
  try {
    host = await launch({ providers: 'fake', data: DATA, env: { DEVICEDECK_UPDATE_FEED: feed, DEVICEDECK_WHATSNEW: '1' } });
    const { page } = host;
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('heading', { level: 2 })).toHaveText(/^What’s new in v\d+\.\d+\.\d+/);
    console.log(`What's new opened by itself: ${await dialog.getByRole('heading', { level: 2 }).textContent()}`);
    // The fake feed has no release for this version: these are docs/releases/v{version}.md, embedded in the build.
    await expect(dialog.getByTestId('release-notes').getByRole('heading', { level: 3 })).toContainText('DeviceDeck v');
    await domClick(page, '[role=dialog] button:has-text("Close") >> nth=-1');
    await expect(dialog).toBeHidden();
  } finally {
    await host?.close();
  }
});
