/// <reference types="vitest/config" />
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath } from 'node:url';

// The built page makes no network requests: images come from data:/blob: URLs (mock) or the
// host's virtual host https://device.local/ (WebView2). Dev mode skips this so HMR keeps working.
const contentSecurityPolicy: Plugin = {
  name: 'devicedeck-csp',
  apply: 'build',
  transformIndexHtml(html) {
    const csp = [
      "default-src 'self'",
      "script-src 'self'",
      "style-src 'self' 'unsafe-inline'",
      "img-src 'self' data: blob: https://device.local",
      "media-src 'self' data: blob: https://device.local",
      "font-src 'self' data:",
      "connect-src 'none'",
    ].join('; ');
    return html.replace('<head>', `<head>\n    <meta http-equiv="Content-Security-Policy" content="${csp}" />`);
  },
};

// Port 5173 is Vite's default. Never 10808 or 10085 (held by the owner's IRNetFree app).
export default defineConfig({
  plugins: [react(), contentSecurityPolicy],
  // Relative asset URLs so the host can serve dist/ from any virtual host or folder.
  base: './',
  server: { port: 5173, strictPort: true },
  preview: { port: 4173, strictPort: true },
  build: {
    target: 'es2022',
    chunkSizeWarningLimit: 1500,
    // index.html is the app; mirror.html is the screen-mirroring window (toolbox).
    rollupOptions: {
      input: {
        main: fileURLToPath(new URL('./index.html', import.meta.url)),
        mirror: fileURLToPath(new URL('./mirror.html', import.meta.url)),
      },
    },
  },
  test: {
    environment: 'jsdom',
    include: ['tests/unit/**/*.test.ts', 'tests/unit/**/*.test.tsx'],
    restoreMocks: true,
  },
});
