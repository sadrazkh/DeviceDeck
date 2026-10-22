import { expect, test, type Locator, type Page } from '@playwright/test';
import { contrastProblems } from './contrast';
import { layoutProblems } from './layout';
import { query, settle, shot, watchConsole, type Variant } from './helpers';

// Hidden photos in the photo backup (round 4, hiddenbackup): normal only, both or hidden only; hidden ones in a folder
// of their own (dated inside) or with the others; the folder's name; the example path and the privacy note. Every state
// check waits (locator assertions or expect.poll): GitHub's runner is slower than this PC.

const SIZES = [
  [1024, 680],
  [1280, 800],
  [1600, 1000],
] as const;

const TEXT = {
  en: {
    normal: 'Normal only',
    both: 'Both',
    only: 'Hidden only',
    apart: 'In a folder of their own',
    together: 'With the other photos',
    privacy: 'anyone who can open this folder on the PC can see them',
    moves: 'Changing this moves nothing on the PC',
    leftOut: 'this phone backs up hidden photos only',
  },
  fa: {
    normal: 'فقط عادی',
    both: 'هر دو',
    only: 'فقط پنهان',
    apart: 'در پوشه‌ای جدا',
    together: 'کنار بقیهٔ عکس‌ها',
    privacy: 'هر کسی که بتواند این پوشه را روی رایانه باز کند',
    moves: 'این تغییر چیزی را روی رایانه جابه‌جا نمی‌کند',
    leftOut: 'این گوشی فقط از عکس‌های پنهان پشتیبان می‌گیرد',
  },
};

async function openPanel(page: Page, variant: Variant): Promise<Locator> {
  await page.goto(query(variant, { screen: 'photos', queue: '0' }));
  await page.getByTestId('backup-open').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('backup-count')).toBeVisible();
  return dialog;
}

async function fits(page: Page, scope: string, label: string) {
  await settle(page);
  await expect.poll(() => layoutProblems(page, scope), { message: `layout ${label}` }).toEqual([]);
  await expect.poll(() => contrastProblems(page), { message: `contrast ${label}` }).toEqual([]);
}

for (const variant of ['light', 'dark', 'rtl'] as const) {
  test(`the panel offers every combination of normal and hidden photos, with an example and a privacy note (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    const text = variant === 'rtl' ? TEXT.fa : TEXT.en;
    const dialog = await openPanel(page, variant);
    const section = dialog.getByTestId('backup-hidden');
    await section.scrollIntoViewIfNeeded();

    // Normal only by default: the Hidden album is left out, and nothing about where it would go is shown.
    await expect(section.getByRole('radio', { name: text.normal })).toHaveAttribute('aria-checked', 'true');
    await expect(section).toHaveAttribute('data-hidden', 'exclude');
    await expect(section.getByTestId('backup-hidden-example')).toHaveCount(0);
    const start = dialog.getByTestId('backup-start');
    const normalLabel = await start.innerText();

    // Both: into a folder of their own by default, dated inside; the button counts them too.
    await section.getByRole('radio', { name: text.both }).click();
    await expect(section).toHaveAttribute('data-hidden', 'include');
    await expect(section.getByRole('radio', { name: text.apart })).toHaveAttribute('aria-checked', 'true');
    await expect(section.getByTestId('backup-hidden-example')).toContainText(/Phone backups\\Nima’s iPhone\\Hidden\\\d{4}\\\d{2}\\IMG_1234\.HEIC/);
    await expect(section.getByTestId('backup-hidden-privacy')).toContainText(text.privacy);
    await expect(section.getByTestId('backup-hidden-moves')).toContainText(text.moves);
    await expect(start).not.toHaveText(normalLabel);
    await expect(dialog.getByTestId('backup-use-defaults')).toBeVisible();

    for (const [width, height] of SIZES) {
      await page.setViewportSize({ width, height });
      await section.scrollIntoViewIfNeeded();
      await fits(page, '[role=dialog]', `${variant} at ${width}`);
    }
    await shot(page, `photos-backup-hidden-panel-${variant}`);

    // With the others: the same date folders, no hidden folder name to set.
    await section.getByRole('radio', { name: text.together }).click();
    await expect(section).toHaveAttribute('data-layout', 'together');
    await expect(section.getByTestId('backup-hidden-folder')).toHaveCount(0);
    await expect(section.getByTestId('backup-hidden-example')).not.toContainText('Hidden');
    await expect(section.getByTestId('backup-hidden-example')).toContainText(/Nima’s iPhone\\\d{4}\\\d{2}\\IMG_1234\.HEIC/);

    // Hidden only: the others are left out, and the panel says so.
    await section.getByRole('radio', { name: text.only }).click();
    await expect(section).toHaveAttribute('data-hidden', 'only');
    await expect(dialog.getByTestId('backup-normal-left-out')).toContainText(text.leftOut);
    await section.getByRole('radio', { name: text.apart }).click();
    await expect(section).toHaveAttribute('data-layout', 'separate');
    await fits(page, '[role=dialog]', `${variant}, hidden only`);
    consoleWatch.assertClean();
  });
}

test('the hidden folder can be renamed, is checked as one folder name, and Persian suggests پنهان', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const dialog = await openPanel(page, 'light');
  const section = dialog.getByTestId('backup-hidden');
  await section.getByRole('radio', { name: 'Both' }).click();
  const field = section.getByTestId('backup-hidden-folder');
  await expect(field).toHaveValue('Hidden');
  await expect(section.getByTestId('backup-hidden-suggest')).toHaveCount(0); // already the suggestion

  // Not one folder name: said while typing, never saved.
  await field.fill('Photos/Private');
  await expect(section.getByTestId('backup-hidden-folder-error')).toHaveText('One folder name, without \\ or /.');
  await expect(field).toHaveAttribute('aria-invalid', 'true');
  await field.fill('2026');
  await expect(section.getByTestId('backup-hidden-folder-error')).toContainText('date folder');
  await field.press('Enter');
  await expect(field).toHaveValue('Hidden');
  await expect(section.getByTestId('backup-hidden-folder-error')).toHaveCount(0);

  // A good name is saved when the field is left, and the example follows; the English suggestion brings Hidden back.
  await field.fill('Private');
  await field.press('Enter');
  await expect(section.getByTestId('backup-hidden-example')).toContainText('\\Private\\');
  await expect(section.getByTestId('backup-hidden-suggest')).toHaveText('Use “Hidden”');
  await section.getByTestId('backup-hidden-suggest').click();
  await expect(field).toHaveValue('Hidden');
  await expect(section.getByTestId('backup-hidden-example')).toContainText('\\Hidden\\');
  consoleWatch.assertClean();
});

test('in Persian the hidden folder can be called پنهان, and the path stays left to right', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  const dialog = await openPanel(page, 'rtl');
  const section = dialog.getByTestId('backup-hidden');
  await section.getByRole('radio', { name: 'هر دو' }).click();
  const field = section.getByTestId('backup-hidden-folder');
  await expect(field).toHaveValue('Hidden');
  await expect(section.getByTestId('backup-hidden-suggest')).toHaveText('استفاده از «پنهان»');
  await section.getByTestId('backup-hidden-suggest').click();
  await expect(field).toHaveValue('پنهان');
  await expect(section.getByTestId('backup-hidden-example')).toContainText('\\پنهان\\');
  await expect(section.getByTestId('backup-hidden-suggest')).toHaveCount(0);
  await expect(section.getByTestId('backup-hidden-example')).toHaveCSS('direction', 'ltr');
  // The folders read left to right, پنهان in its place before the year (not swapped with the date folders).
  const folders = section.getByTestId('backup-hidden-example').locator('bdi');
  await expect
    .poll(async () => {
      const lefts = await folders.evaluateAll((els) => els.map((el) => el.getBoundingClientRect().left));
      return lefts.length > 4 && lefts.every((left, i) => i === 0 || left > lefts[i - 1]!);
    })
    .toBe(true);
  await field.fill('a:b');
  await expect(section.getByTestId('backup-hidden-folder-error')).toContainText('بدون');
  await field.press('Enter');
  await expect(field).toHaveValue('پنهان');
  await settle(page);
  await shot(page, 'photos-backup-hidden-folder-rtl');
  consoleWatch.assertClean();
});

test('an Android phone has no Hidden album, so no hidden choices', async ({ page }) => {
  const consoleWatch = watchConsole(page);
  await page.goto(query('light', { screen: 'photos', device: 'android', queue: '0' }));
  await page.getByTestId('backup-open').click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByTestId('backup-count')).toContainText('46');
  await expect(dialog.getByTestId('backup-save-edited')).toHaveCount(0);
  await expect(dialog.getByTestId('backup-hidden')).toHaveCount(0);
  consoleWatch.assertClean();
});

for (const variant of ['light', 'dark', 'rtl'] as const) {
  test(`Settings has the same hidden defaults, with the example and the note (${variant})`, async ({ page }) => {
    const consoleWatch = watchConsole(page);
    const text = variant === 'rtl' ? TEXT.fa : TEXT.en;
    await page.goto(query(variant, { screen: 'settings', queue: '0' }));
    const rows = page.getByTestId('settings-backup-hidden');
    await rows.scrollIntoViewIfNeeded();
    await expect(rows.getByRole('radio', { name: text.normal })).toHaveAttribute('aria-checked', 'true');
    await expect(rows.getByTestId('backup-hidden-example')).toHaveCount(0);

    await rows.getByRole('radio', { name: text.both }).click();
    await expect(rows).toHaveAttribute('data-hidden', 'include');
    await expect(rows.getByTestId('backup-hidden-example')).toContainText(/Phone backups\\iPhone\\Hidden\\\d{4}\\\d{2}\\IMG_1234\.HEIC/);
    await expect(rows.getByTestId('backup-hidden-privacy')).toContainText(text.privacy);
    if (variant === 'rtl') {
      await rows.getByTestId('backup-hidden-suggest').click();
      await expect(rows.getByTestId('backup-hidden-folder')).toHaveValue('پنهان');
      await expect(rows.getByTestId('backup-hidden-example')).toContainText('\\پنهان\\');
    }

    for (const [width, height] of SIZES) {
      await page.setViewportSize({ width, height });
      await rows.scrollIntoViewIfNeeded();
      await fits(page, '[data-testid=content]', `settings ${variant} at ${width}`);
    }
    await shot(page, `settings-photo-backup-hidden-${variant}`);

    // The defaults reach a phone without options of its own (in the same page: the mock keeps its state).
    await page.getByTestId('nav-photos').click();
    await page.getByTestId('backup-open').click();
    await expect(page.getByRole('dialog').getByTestId('backup-hidden')).toHaveAttribute('data-hidden', 'include');
    await expect(page.getByRole('dialog').getByTestId('backup-use-defaults')).toHaveCount(0);
    consoleWatch.assertClean();
  });
}
