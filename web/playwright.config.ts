import { defineConfig } from '@playwright/test';

// Several worktrees can run their tests at once on this PC, so the dev server's port comes from PW_PORT
// (default 5173) and an already running server is reused only when PW_REUSE=1 — otherwise a test run could
// silently hit another worktree's server and test the wrong code.
const port = Number(process.env.PW_PORT ?? 5173);
const url = `http://localhost:${port}`;

// Uses the Microsoft Edge already installed on this PC (channel 'msedge'); no browser downloads.
export default defineConfig({
  testDir: 'tests/e2e',
  outputDir: 'test-results/playwright',
  fullyParallel: true,
  workers: 4,
  reporter: [['list']],
  timeout: 60_000,
  use: {
    channel: 'msedge',
    baseURL: url,
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
  },
  webServer: {
    command: `npx vite --port ${port} --strictPort`,
    url,
    reuseExistingServer: process.env.PW_REUSE === '1',
    timeout: 60_000,
  },
});
