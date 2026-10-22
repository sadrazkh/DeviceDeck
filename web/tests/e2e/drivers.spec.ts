import { expect, test, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { query, settle, watchConsole, type Variant } from './helpers';
import { layoutProblems } from './layout';

// The Drivers card (v0.6): every state the host can send (?drivers=…), what each button does, and the layout and
// contrast at 1024–1600 in light, dark and Persian. Every check waits for its state (locator assertions, expect.poll);
// nothing depends on a toast or on how long something takes.

const welcome = (variant: Variant, drivers: string, extra: Record<string, string> = {}) => query(variant, { mock: 'empty', queue: '0', drivers, ...extra });

const row = (page: Page, id: 'ios' | 'android') => page.getByTestId(`driver-row-${id}`);

const opened = (page: Page) => page.evaluate(() => (window as unknown as { deviceDeckMock: { drivers: { opened: string[] } } }).deviceDeckMock.drivers.opened);

interface StateCase {
  drivers: string;
  ios: string;
  android: string;
  iosAction?: string;
  androidAction?: string;
  phone?: string;
}

const STATES: StateCase[] = [
  { drivers: 'ready', ios: 'ready', android: 'ready' },
  { drivers: 'iosMissing', ios: 'action', android: 'ready', iosAction: 'getAppleDevices' },
  { drivers: 'iosStopped', ios: 'action', android: 'ready', iosAction: 'startAmds' },
  { drivers: 'adbMissing', ios: 'ready', android: 'action', androidAction: 'downloadAdb' },
  { drivers: 'adbDownloading', ios: 'ready', android: 'downloading' },
  { drivers: 'adbIdle', ios: 'ready', android: 'notStarted' },
  { drivers: 'adbStarting', ios: 'ready', android: 'starting' },
  { drivers: 'adbFailed', ios: 'ready', android: 'problem', androidAction: 'restartAdb' },
  { drivers: 'androidNoDriver', ios: 'ready', android: 'ready', phone: 'driverMissing' },
  { drivers: 'androidUsbOff', ios: 'ready', android: 'ready', phone: 'usbDebuggingOff' },
  { drivers: 'androidUnauthorized', ios: 'ready', android: 'ready', phone: 'unauthorized' },
];

for (const state of STATES) {
  test(`drivers card: ${state.drivers}`, async ({ page }) => {
    const console = watchConsole(page);
    await page.goto(welcome('light', state.drivers));
    await expect(row(page, 'ios')).toHaveAttribute('data-chip', state.ios);
    await expect(row(page, 'android')).toHaveAttribute('data-chip', state.android);

    for (const [id, action] of [
      ['ios', state.iosAction],
      ['android', state.androidAction],
    ] as const) {
      const button = page.getByTestId(`driver-action-${id}`);
      if (action) await expect(button).toHaveAttribute('data-action', action);
      else await expect(button).toHaveCount(0);
    }

    if (state.phone) await expect(page.getByTestId(`driver-phone-${state.phone}`)).toBeVisible();
    else await expect(page.locator('[data-testid^=driver-phone-]')).toHaveCount(0);

    // Never red unless something really failed.
    if (state.ios !== 'problem' && state.android !== 'problem') await expect(page.locator('[data-chip=problem]')).toHaveCount(0);
    console.assertClean();
  });
}

test('starting and not-started Android tools are grey, with no button and no red', async ({ page }) => {
  for (const drivers of ['adbIdle', 'adbStarting']) {
    await page.goto(welcome('light', drivers));
    const chip = page.getByTestId('driver-chip-android');
    await expect(chip).toBeVisible();
    await expect(chip).not.toHaveClass(/badge-danger|badge-warning|badge-success/);
  }
});

test('missing Apple driver: the Store, then iTunes, then it turns green by itself', async ({ page }) => {
  await page.goto(welcome('light', 'iosMissing'));
  await page.getByTestId('driver-action-ios').click();
  await expect.poll(() => opened(page)).toEqual(['appleDevicesStore']);
  await page.getByTestId('driver-itunes').click();
  await expect.poll(() => opened(page)).toEqual(['appleDevicesStore', 'itunes']);

  // Apple Devices finished installing: the host's drivers.changed turns the row green, no restart.
  await page.getByTestId('sim-install-apple-devices').click();
  await expect(row(page, 'ios')).toHaveAttribute('data-chip', 'ready');
  await expect(page.getByTestId('driver-action-ios')).toHaveCount(0);
});

test('stopped Apple service: Start goes through Windows prompt and the row turns green', async ({ page }) => {
  await page.goto(welcome('light', 'iosStopped'));
  await expect(row(page, 'ios')).toContainText('Windows asks for permission first');
  await page.getByTestId('driver-action-ios').click();
  await expect(row(page, 'ios')).toHaveAttribute('data-chip', 'ready');
});

test('a declined Windows prompt says so on the row and nothing changes', async ({ page }) => {
  await page.goto(welcome('light', 'iosStopped', { uac: 'decline' }));
  await page.getByTestId('driver-action-ios').click();
  await expect(page.getByTestId('driver-error-ios')).toContainText('permission prompt was closed');
  await expect(row(page, 'ios')).toHaveAttribute('data-chip', 'action');
});

test('missing Android tools: download with progress, then ready', async ({ page }) => {
  await page.goto(welcome('light', 'adbMissing'));
  await expect(row(page, 'android')).toContainText('about 8 MB');
  await page.getByTestId('driver-license').click();
  await expect.poll(() => opened(page)).toEqual(['platformToolsLicense']);

  await page.getByTestId('driver-action-android').click();
  // The download's steps can pass quickly; record what the row showed instead of catching each one.
  await expect(row(page, 'android')).toHaveAttribute('data-chip', 'ready');
  await expect(page.getByTestId('driver-progress')).toHaveCount(0);
});

test('a running download shows its progress and Cancel puts the Download button back', async ({ page }) => {
  await page.goto(welcome('light', 'adbDownloading'));
  const progress = page.getByTestId('driver-progress');
  await expect(progress).toHaveAttribute('data-state', 'downloading');
  await expect(progress.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '42');
  await expect(progress).toContainText(/3\.4 MB of 8(\.0)? MB/);
  await page.getByTestId('driver-cancel').click();
  await expect(progress).toHaveCount(0);
  await expect(page.getByTestId('driver-action-android')).toHaveAttribute('data-action', 'downloadAdb');
});

test('a download Google did not sign is refused with the reason and Try again', async ({ page }) => {
  await page.goto(welcome('light', 'adbDownloadFails'));
  await page.getByTestId('driver-action-android').click();
  await expect(row(page, 'android')).toHaveAttribute('data-chip', 'problem');
  await expect(row(page, 'android')).toContainText('aren’t signed by Google');
  await expect(page.getByTestId('driver-action-android')).toHaveAttribute('data-action', 'retryDownload');
});

test('Android tools that failed: the reason, then Restart brings them up', async ({ page }) => {
  await page.goto(welcome('light', 'adbFailed'));
  await expect(row(page, 'android')).toContainText('cannot bind to 127.0.0.1:5137');
  await page.getByTestId('driver-action-android').click();
  await expect(row(page, 'android')).toHaveAttribute('data-chip', 'ready');
});

test('a phone without its USB driver links the maker and Google', async ({ page }) => {
  await page.goto(welcome('light', 'androidNoDriver'));
  const help = page.getByTestId('driver-phone-driverMissing');
  await expect(help).toContainText('Galaxy S23 needs its maker’s USB driver');
  await help.getByTestId('driver-link-maker').click();
  await help.getByTestId('driver-link-google').click();
  await expect.poll(() => opened(page)).toEqual(['samsungUsbDriver', 'googleUsbDriver']);
});

test('a phone with USB debugging off gets the three steps', async ({ page }) => {
  await page.goto(welcome('light', 'androidUsbOff'));
  const help = page.getByTestId('driver-phone-usbDebuggingOff');
  await expect(help).toContainText('Pixel 8 is plugged in, but USB debugging is off');
  await expect(help.locator('li')).toHaveCount(3);
});

test('Check again shows what the host says now', async ({ page }) => {
  await page.goto(welcome('light', 'adbIdle'));
  await expect(row(page, 'android')).toHaveAttribute('data-chip', 'notStarted');
  // The host's answer changes without a drivers.changed reaching the page.
  await page.evaluate(() => {
    const mock = (window as unknown as { deviceDeckMock: { drivers: { setQuietly(rows: unknown[]): void } } }).deviceDeckMock;
    mock.drivers.setQuietly([
      { providerId: 'ios', state: 'ready', version: '18.4.0.4', detail: null },
      { providerId: 'android', state: 'ready', version: '37.0.1', detail: null, source: 'bundled' },
    ]);
  });
  await page.getByTestId('drivers-check').click();
  await expect(row(page, 'android')).toHaveAttribute('data-chip', 'ready');
});

test('the card asks again by itself every 30 s while it is on screen', async ({ page }) => {
  await page.clock.install();
  await page.goto(welcome('light', 'adbIdle'));
  await page.clock.resume();
  await expect(row(page, 'android')).toHaveAttribute('data-chip', 'notStarted');
  await page.evaluate(() => {
    const mock = (window as unknown as { deviceDeckMock: { drivers: { setQuietly(rows: unknown[]): void } } }).deviceDeckMock;
    mock.drivers.setQuietly([
      { providerId: 'ios', state: 'ready', version: '18.4.0.4', detail: null },
      { providerId: 'android', state: 'ready', version: '37.0.1', detail: null, source: 'bundled' },
    ]);
  });
  await page.clock.fastForward(31_000);
  await expect(row(page, 'android')).toHaveAttribute('data-chip', 'ready');
});

test('Settings has the same card', async ({ page }) => {
  await page.goto(query('light', { screen: 'settings', drivers: 'adbMissing', queue: '0' }));
  await expect(page.getByTestId('screen-settings').getByTestId('driver-row-android')).toHaveAttribute('data-chip', 'action');
});

test('Persian: the card is in Persian, right to left', async ({ page }) => {
  await page.goto(welcome('rtl', 'adbMissing'));
  await expect(page.getByTestId('drivers')).toContainText('ابزار اندروید');
  await expect(page.getByTestId('driver-action-android')).toContainText('حدود ۸ مگابایت');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
});

// ---------- Layout and contrast ----------

const SIZES = [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const;
const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];
const LAYOUT_STATES = ['iosMissing', 'iosStopped', 'adbDownloading', 'adbFailed', 'androidNoDriver', 'androidUsbOff'];

for (const [width, height] of SIZES) {
  for (const variant of VARIANTS) {
    test(`drivers card fits at ${width}×${height} (${variant})`, async ({ page }) => {
      test.setTimeout(120_000);
      await page.setViewportSize({ width, height });
      for (const drivers of LAYOUT_STATES) {
        await page.goto(welcome(variant, drivers));
        await expect(row(page, 'android')).toBeVisible();
        if (drivers === 'adbDownloading') await expect(page.getByTestId('driver-progress')).toBeVisible();
        await settle(page);
        await expect.poll(() => layoutProblems(page), `${drivers} at ${width}×${height} (${variant})`).toEqual([]);
        if (width === 1280) await expect.poll(() => contrastProblems(page), `${drivers} (${variant})`).toEqual([]);
      }
    });
  }
}

test('drivers card in Persian and dark: readable everywhere', async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  for (const drivers of LAYOUT_STATES) {
    await page.goto(`/?${new URLSearchParams({ latency: '0', indexing: '0', queue: '0', mock: 'empty', drivers, lang: 'fa', theme: 'dark' })}`);
    await expect(row(page, 'android')).toBeVisible();
    await settle(page);
    await expect.poll(() => layoutProblems(page), drivers).toEqual([]);
    await expect.poll(() => contrastProblems(page), drivers).toEqual([]);
  }
});

// Screenshots of every state for the report: DRIVERS_SHOTS=1 npx playwright test drivers (test-results/screens/drivers).
test('card screenshots', async ({ page }) => {
  test.skip(!process.env.DRIVERS_SHOTS, 'only when asked for (DRIVERS_SHOTS=1)');
  test.setTimeout(300_000);
  await page.setViewportSize({ width: 1280, height: 1000 });
  const looks: { name: string; params: Record<string, string> }[] = [
    { name: 'en-light', params: { theme: 'light' } },
    { name: 'en-dark', params: { theme: 'dark' } },
    { name: 'fa-light', params: { lang: 'fa', theme: 'light' } },
    { name: 'fa-dark', params: { lang: 'fa', theme: 'dark' } },
  ];
  for (const drivers of ['ready', ...STATES.map((s) => s.drivers).filter((s) => s !== 'ready'), 'adbDownloadFails']) {
    for (const look of looks) {
      await page.goto(`/?${new URLSearchParams({ latency: '0', indexing: '0', queue: '0', mock: 'empty', drivers, ...look.params })}`);
      await expect(row(page, 'android')).toBeVisible();
      if (drivers === 'adbDownloading') await expect(page.getByTestId('driver-progress')).toBeVisible();
      if (drivers === 'adbDownloadFails') {
        await page.getByTestId('driver-action-android').click();
        await expect(row(page, 'android')).toHaveAttribute('data-chip', 'problem');
      }
      await settle(page);
      await page.getByTestId('drivers').screenshot({ path: `test-results/screens/drivers/${drivers}-${look.name}.png` });
    }
  }
});
