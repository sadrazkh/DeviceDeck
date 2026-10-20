import { defineConfig } from '@playwright/test';

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
    baseURL: 'http://localhost:5173',
    viewport: { width: 1440, height: 900 },
    deviceScaleFactor: 1,
  },
  webServer: {
    command: 'npx vite --port 5173 --strictPort',
    url: 'http://localhost:5173',
    reuseExistingServer: true,
    timeout: 60_000,
  },
});
