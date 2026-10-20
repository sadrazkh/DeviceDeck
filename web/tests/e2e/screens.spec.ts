import { expect, test } from '@playwright/test';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// Every screen in light, dark and right-to-left Persian, against the mock backend.
// Screenshots go to test-results/screens/ (git-ignored); each page must log no errors or warnings.

interface ScreenCase {
  name: string;
  params: Record<string, string>;
  ready: string;
}

const SCREENS: ScreenCase[] = [
  { name: 'welcome', params: { mock: 'empty' }, ready: '[data-testid=screen-welcome]' },
  { name: 'welcome-trust', params: { mock: 'trust' }, ready: '[data-testid=welcome-state-awaitingTrust]' },
  { name: 'welcome-usb-debugging', params: { mock: 'trust', device: 'android' }, ready: '[data-testid=welcome-state-unauthorized]' },
  { name: 'welcome-driver-missing', params: { mock: 'driverMissing' }, ready: '[data-testid=drivers]' },
  { name: 'overview', params: { screen: 'overview' }, ready: '[data-testid=battery-card] [role=img]' },
  { name: 'overview-android', params: { screen: 'overview', device: 'android' }, ready: '[data-testid=battery-card] [role=img]' },
  { name: 'overview-limited', params: { mock: 'limited', screen: 'overview' }, ready: '[data-testid=screen-overview]' },
  { name: 'photos', params: { screen: 'photos' }, ready: '[data-testid=photo-grid] [role=gridcell]' },
  { name: 'photos-android', params: { screen: 'photos', device: 'android' }, ready: '[data-testid=photo-grid] [role=gridcell]' },
  { name: 'files', params: { screen: 'files' }, ready: '[data-testid=app-card-app\\:org\\.videolan\\.vlc-ios]' },
  { name: 'files-android', params: { screen: 'files', device: 'android' }, ready: '[data-testid=file-table] [role=row][aria-selected]' },
  { name: 'app-documents', params: { screen: 'appDocuments' }, ready: '[data-testid=app-card-app\\:org\\.videolan\\.vlc-ios]' },
  { name: 'app-documents-android', params: { screen: 'appDocuments', device: 'android' }, ready: '[data-testid=app-card-appmedia\\:org\\.telegram\\.messenger]' },
  { name: 'apps', params: { screen: 'apps' }, ready: '[data-testid=app-row]' },
  { name: 'apps-android', params: { screen: 'apps', device: 'android' }, ready: '[data-testid=app-row]' },
  { name: 'coming-next', params: { screen: 'backup' }, ready: '[data-testid=screen-backup]' },
  { name: 'settings', params: { screen: 'settings' }, ready: '[data-testid=screen-settings]' },
];

const VARIANTS: Variant[] = ['light', 'dark', 'rtl'];

for (const screen of SCREENS) {
  for (const variant of VARIANTS) {
    test(`${screen.name} (${variant}) renders cleanly`, async ({ page }) => {
      const consoleWatch = watchConsole(page);
      await page.goto(query(variant, screen.params));
      await expect(page.locator(screen.ready).first()).toBeVisible();
      const html = page.locator('html');
      await expect(html).toHaveAttribute('dir', variant === 'rtl' ? 'rtl' : 'ltr');
      await expect(html).toHaveAttribute('data-theme', variant === 'dark' ? 'dark' : 'light');
      await settle(page);
      await shot(page, `${screen.name}-${variant}`);
      consoleWatch.assertClean();
    });
  }
}

test('compact window (1024×680) collapses the rail to icons', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.setViewportSize({ width: 1024, height: 680 });
  for (const variant of ['light', 'rtl'] as const) {
    for (const screen of ['overview', 'photos']) {
      await page.goto(query(variant, { screen }));
      await expect(page.locator(`[data-testid=screen-${screen}]`)).toBeVisible();
      const rail = page.getByRole('navigation', { name: variant === 'rtl' ? 'بخش‌ها' : 'Sections' });
      await expect(rail).toHaveAttribute('data-compact', 'true');
      const box = await rail.boundingBox();
      expect(box!.width).toBeLessThanOrEqual(64);
      await settle(page);
      await shot(page, `${screen}-1024-${variant}`);
    }
  }
  consoleWatch.assertClean();
});

test('top-tabs layout (direction C switch)', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'overview', nav: 'tabs' }));
  await expect(page.locator('html')).toHaveAttribute('data-nav', 'tabs');
  await expect(page.getByTestId('nav-photos')).toBeVisible();
  await settle(page);
  await shot(page, 'overview-tabs-light');
  consoleWatch.assertClean();
});

for (const variant of ['light', 'rtl'] as const) {
  test(`title bar keeps clear of the host's caption buttons (${variant})`, async ({ page }) => {
    await page.goto(query(variant, { screen: 'overview' }));
    await expect(page.getByTestId('screen-overview')).toBeVisible();
    // What the WinUI host publishes; the buttons stay on the right in Persian too.
    await page.evaluate(() => {
      const s = document.documentElement.style;
      s.setProperty('--dd-caption-left', '0px');
      s.setProperty('--dd-caption-right', '188px');
      s.setProperty('--dd-titlebar-height', '48px');
    });
    const width = page.viewportSize()!.width;
    const controls = page.locator('header button');
    const count = await controls.count();
    expect(count).toBeGreaterThan(2);
    for (let i = 0; i < count; i++) {
      const box = await controls.nth(i).boundingBox();
      if (box) expect(box.x + box.width, `button ${i}`).toBeLessThanOrEqual(width - 188);
    }
  });
}

test('iPhone battery health is labelled as an estimate; Android is not', async ({ page }) => {
  await page.goto(query('light', { screen: 'overview' }));
  const card = page.getByTestId('battery-card');
  await expect(card).toContainText('Estimated health');
  // Keyboard focus opens the tooltip at once (hover waits for the delay and the screen's fade-in).
  await page.getByTestId('health-estimated-info').focus();
  await expect(page.getByRole('tooltip')).toContainText('iOS Settings');
  await page.goto(query('light', { screen: 'overview', device: 'android' }));
  await expect(page.getByTestId('battery-card')).toContainText('health');
  await expect(page.getByTestId('battery-card')).not.toContainText('Estimated');
  await expect(page.getByTestId('health-estimated-info')).toHaveCount(0);
});

test('Persian mirrors the layout and uses Persian digits', async ({ page }) => {
  await page.goto(query('rtl', { screen: 'overview' }));
  await expect(page.getByTestId('screen-overview')).toBeVisible();
  const rail = await page.getByRole('navigation', { name: 'بخش‌ها' }).boundingBox();
  const content = await page.getByTestId('content').boundingBox();
  expect(rail!.x).toBeGreaterThan(content!.x); // the nav rail is on the right
  await expect(page.getByTestId('storage-card')).toContainText('۲۵۶');
  await expect(page.getByTestId('battery-card')).toContainText('۸۷٪');
});
