import { chromium, type FullConfig } from '@playwright/test';

// Before any test: one page load, so the dev server has compiled the app (and found its dependencies) once. On
// GitHub's runner the first tests otherwise all wait for that at the same time: in the v0.6.0 run the four that
// started first took 1.3 minutes each and three timed out. The app has no lazy screens, so one load compiles it all.
export default async function warmUp(config: FullConfig) {
  const baseURL = config.projects[0]?.use.baseURL;
  if (!baseURL) return;
  const started = Date.now();
  const browser = await chromium.launch({ channel: 'msedge' });
  try {
    const page = await browser.newPage();
    await page.goto(`${baseURL}/?latency=0&indexing=0&theme=light`, { timeout: 180_000 });
    await page.locator('[data-testid^="screen-"]').first().waitFor({ timeout: 180_000 });
    console.log(`Dev server warmed up in ${((Date.now() - started) / 1000).toFixed(1)} s.`);
  } finally {
    await browser.close();
  }
}
