import { defineConfig } from '@playwright/test';

// Checks against the real host (DeviceDeck.exe with WebView2), driven through its DevTools port; never the mouse.
// Not part of `npm test`: run `npm run test:host` after `dotnet build`. The page comes from this worktree's Vite
// dev server (DEVICEDECK_DEV=1 with DEVICEDECK_DEV_URL), on DD_DEV_PORT (default 5211); DevTools on DD_CDP_PORT (9361).
const devPort = Number(process.env.DD_DEV_PORT ?? 5211);

export default defineConfig({
  testDir: 'tests/host',
  outputDir: 'test-results/host',
  // One window at a time (DeviceDeck is single-instance).
  workers: 1,
  fullyParallel: false,
  reporter: [['list']],
  timeout: 180_000,
  expect: { timeout: 20_000 },
  webServer: {
    command: `npx vite --port ${devPort} --strictPort`,
    url: `http://localhost:${devPort}`,
    reuseExistingServer: process.env.PW_REUSE === '1',
    timeout: 60_000,
  },
});
