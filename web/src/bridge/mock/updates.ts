// Updates in the mock (round 4): app.updateStatus and friends in each state the page shows, chosen with ?update=…,
// and release notes chosen with ?notes=… (including hostile ones, for the sanitising tests). Nothing here goes online.

import type { BridgeEvents, EventName, MethodName, MethodParams, MethodResult } from '../methods';
import type { ReleaseNotes } from '../releaseNotes';
import type { UpdateStatus } from '../types';

/**
 * idle (default: a Setup copy, nothing checked; Check now finds it up to date) · checking · downloading (42 %, stays) ·
 * downloadThenReady (42 % → ready in about 3 s) · ready · error · rateLimited · upToDate · portable (the zip, up to date) ·
 * portableNew (the zip, v0.4.0 on GitHub: "notInstalled with a new version") · whatsNew (the first start of v0.4.0).
 */
export type MockUpdateScenario =
  | 'idle'
  | 'checking'
  | 'downloading'
  | 'downloadThenReady'
  | 'ready'
  | 'error'
  | 'rateLimited'
  | 'upToDate'
  | 'portable'
  | 'portableNew'
  | 'whatsNew';

export const MOCK_UPDATE_SCENARIOS: readonly MockUpdateScenario[] = [
  'idle',
  'checking',
  'downloading',
  'downloadThenReady',
  'ready',
  'error',
  'rateLimited',
  'upToDate',
  'portable',
  'portableNew',
  'whatsNew',
];

/** owner: notes as the owner writes them (English, then Persian in <details>) · english: no Persian part · hostile: HTML, scripts, images and foreign links · missing: none could be had · cache: last time's copy. */
export type MockNotesScenario = 'owner' | 'english' | 'hostile' | 'missing' | 'cache';

export const MOCK_NOTES_SCENARIOS: readonly MockNotesScenario[] = ['owner', 'english', 'hostile', 'missing', 'cache'];

type Handler<M extends MethodName> = (params: MethodParams<M>) => MethodResult<M>;

export interface MockUpdatesHost {
  emit<E extends EventName>(event: E, params: BridgeEvents[E]): void;
  now(): number;
  after(ms: number, run: () => void): void;
  fault(code: string, message?: string): Error;
  badParams(message: string): Error;
}

const VERSION = /^\d+\.\d+\.\d+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$/;
const MINUTE = 60_000;

export class MockUpdates {
  private status!: UpdateStatus;
  scenario: MockUpdateScenario = 'idle';
  notes: MockNotesScenario = 'owner';
  /** What the page asked for, for tests. */
  readonly opened: (string | null)[] = [];
  readonly whatsNewShown: string[] = [];
  applied = 0;

  constructor(private readonly host: MockUpdatesHost) {}

  /** Called once the backend's clock is set (the backend's constructor). */
  configure(scenario: MockUpdateScenario, notes: MockNotesScenario) {
    this.scenario = scenario;
    this.notes = notes;
    const host = this.host;
    const now = host.now();
    const iso = (offset: number) => new Date(now + offset).toISOString();
    const base: UpdateStatus = {
      state: 'idle',
      currentVersion: '0.3.0',
      newVersion: null,
      percent: null,
      error: null,
      lastChecked: null,
      install: 'setup',
      channel: 'stable',
      architecture: 'x64',
      lastResult: null,
      retryAt: null,
      whatsNew: null,
    };
    const found = { newVersion: '0.4.0', lastChecked: iso(-12 * MINUTE), lastResult: 'newVersion' } as const;
    const offline = 'DeviceDeck couldn’t reach GitHub to look for updates. Check the connection and try again.';
    const states: Record<MockUpdateScenario, UpdateStatus> = {
      idle: { ...base, currentVersion: '0.1.0-mock' },
      checking: { ...base, state: 'checking', lastChecked: iso(-6 * 60 * MINUTE), lastResult: 'upToDate' },
      downloading: { ...base, ...found, state: 'downloading', percent: 42 },
      downloadThenReady: { ...base, ...found, state: 'downloading', percent: 42 },
      ready: { ...base, ...found, state: 'ready' },
      error: { ...base, state: 'error', error: offline, lastChecked: iso(-5 * MINUTE), lastResult: 'error' },
      rateLimited: { ...base, state: 'error', error: 'GitHub asked DeviceDeck to wait before checking again.', retryAt: iso(40 * MINUTE), lastChecked: iso(-MINUTE), lastResult: 'error' },
      upToDate: { ...base, state: 'upToDate', lastChecked: iso(-2 * 60 * MINUTE), lastResult: 'upToDate' },
      portable: { ...base, install: 'portable', state: 'upToDate', lastChecked: iso(-3 * 60 * MINUTE), lastResult: 'upToDate' },
      portableNew: { ...base, ...found, install: 'portable', state: 'available' },
      whatsNew: { ...base, currentVersion: '0.4.0', whatsNew: '0.4.0' },
    };
    this.status = states[scenario];
    if (scenario === 'downloadThenReady') this.advance();
  }

  get version(): string {
    return this.status.currentVersion;
  }

  private set(status: UpdateStatus): UpdateStatus {
    this.status = status;
    this.host.emit('app.updateChanged', { ...status });
    return { ...status };
  }

  /** downloadThenReady: 42 % → 100 % in steps, then ready. */
  private advance() {
    this.host.after(600, () => {
      const percent = Math.min(100, (this.status.percent ?? 0) + 20);
      if (percent < 100) {
        this.set({ ...this.status, percent });
        this.advance();
      } else {
        this.set({ ...this.status, state: 'ready', percent: null });
      }
    });
  }

  private requireVersion(version: unknown): string {
    if (typeof version !== 'string' || !VERSION.test(version)) throw this.host.badParams('version must look like 0.4.0 (MAJOR.MINOR.PATCH, optionally -prerelease).');
    return version;
  }

  releaseNotes(version: string): ReleaseNotes {
    const url = `https://github.com/sadrazkh/DeviceDeck/releases/tag/v${version}`;
    const title = `DeviceDeck v${version}`;
    switch (this.notes) {
      case 'missing':
        return { version, url, title: null, markdown: null, published: null, source: 'none' };
      case 'hostile':
        return { version, url, title, markdown: HOSTILE_NOTES, published: '2026-11-02T10:00:00Z', source: 'github' };
      case 'english':
        return { version, url, title, markdown: ownerNotes(version).split('<details>')[0]!.trim(), published: '2026-11-02T10:00:00Z', source: 'github' };
      default:
        return { version, url, title, markdown: ownerNotes(version), published: '2026-11-02T10:00:00Z', source: this.notes === 'cache' ? 'cache' : 'github' };
    }
  }

  readonly handlers: {
    [M in 'app.updateStatus' | 'app.checkForUpdates' | 'app.applyUpdate' | 'app.releaseNotes' | 'app.openReleasePage' | 'app.whatsNewShown']: Handler<M>;
  } = {
    'app.updateStatus': () => ({ ...this.status }),
    'app.checkForUpdates': () => {
      const s = this.status;
      if (s.state === 'ready' || s.state === 'checking' || s.state === 'downloading') return { ...s };
      const lastChecked = new Date(this.host.now()).toISOString();
      if (this.scenario === 'error' || this.scenario === 'rateLimited') return this.set({ ...s, lastChecked });
      if (s.state === 'available') return this.set({ ...s, lastChecked, lastResult: 'newVersion' });
      return this.set({ ...s, state: 'upToDate', newVersion: null, error: null, retryAt: null, percent: null, lastChecked, lastResult: 'upToDate' });
    },
    'app.applyUpdate': () => {
      if (this.status.state !== 'ready') throw this.host.fault('internal', 'No update has been downloaded yet.');
      // The real app quits here and Velopack starts the new version.
      this.applied++;
      return {};
    },
    'app.releaseNotes': ({ version }) => this.releaseNotes(this.requireVersion(version)),
    'app.openReleasePage': ({ version }) => {
      this.opened.push(version === undefined || version === null ? null : this.requireVersion(version));
      return {};
    },
    'app.whatsNewShown': ({ version }) => {
      this.whatsNewShown.push(this.requireVersion(version));
      if (this.status.whatsNew === version) this.set({ ...this.status, whatsNew: null });
      return {};
    },
  };
}

/** Notes the way the owner writes them: a heading, sections, emoji, bold one-liners and lists, then the Persian in <details>. */
export function ownerNotes(version: string): string {
  return `# 🚀 DeviceDeck v${version} — updates you can see, and What’s new right in the app

A button in the title bar now says when an update is ready, and this window shows what it brings.

### 🪟 Windows
- 🔔 **Updates you can see** — an “Update ready” button in the title bar, a banner the first time, and a Windows notification.
- 📝 **What’s new** — the release notes, in English or Persian, before you restart. Once more after the update.
- 📦 **The portable zip checks too** — it asks GitHub for a newer version when it starts and every 12 hours, and Download opens the release page.

### ⚙️ Settings
- **Updates** shows the version, the channel, when DeviceDeck last looked and what it found.
  - Check now and What’s new are there too.

### ℹ️ Good to know
- The notes come from [the release on GitHub](https://github.com/sadrazkh/DeviceDeck/releases) and are kept for when you’re offline.
- Not signed yet, so SmartScreen may warn the first time: More info → Run anyway.

<details>
<summary>🇮🇷 فارسی</summary>

# 🚀 دیوایس‌دک v${version} — به‌روزرسانی‌ای که دیده می‌شود، و «تازه‌ها» داخل خود برنامه

حالا یک دکمه در نوار عنوان می‌گوید کی به‌روزرسانی آماده است، و این پنجره نشان می‌دهد چه چیزی آورده.

### 🪟 ویندوز
- 🔔 **به‌روزرسانی‌ای که دیده می‌شود** — دکمهٔ «به‌روزرسانی آماده است» در نوار عنوان، یک اعلان داخل برنامه بار اول، و اعلان ویندوز.
- 📝 **تازه‌ها** — یادداشت‌های انتشار، به فارسی یا انگلیسی، پیش از راه‌اندازی دوباره. و یک بار دیگر بعد از به‌روزرسانی.
- 📦 **فایل zip هم بررسی می‌کند** — نسخهٔ قابل‌حمل وقتی باز می‌شود و بعد هر ۱۲ ساعت از GitHub می‌پرسد نسخهٔ تازه‌ای هست یا نه، و «دانلود» صفحهٔ انتشار را باز می‌کند.

### ⚙️ تنظیمات
- **به‌روزرسانی** نسخه، کانال، آخرین بررسی و نتیجه‌اش را نشان می‌دهد.
  - «الان بررسی کن» و «تازه‌ها» هم همان‌جا هستند.

### ℹ️ خوب است بدانی
- یادداشت‌ها از [صفحهٔ انتشار در GitHub](https://github.com/sadrazkh/DeviceDeck/releases) می‌آیند و برای وقتی که آفلاین هستی نگه داشته می‌شوند.
- هنوز امضا نشده، پس SmartScreen ممکن است بار اول هشدار بدهد: More info ← Run anyway.

</details>
`;
}

/** Everything the notes must not turn into: scripts, raw HTML, remote images, links off github.com. */
export const HOSTILE_NOTES = `# Release <script>window.__pwned = 1</script>notes
<img src="https://evil.example/pixel.png" onerror="window.__pwned = 2">
<iframe src="https://evil.example/frame"></iframe>
<style>body { display: none }</style>
<!-- a hidden comment -->
<a href="javascript:window.__pwned = 3">raw anchor</a>

- [script link](javascript:window.__pwned=4)
- [data link](data:text/html,<script>window.__pwned=5</script>)
- [other site](https://evil.example/phish)
- [plain http GitHub](http://github.com/sadrazkh/DeviceDeck)
- [look-alike](https://github.com.evil.example/x)
- [GitHub issue](https://github.com/sadrazkh/DeviceDeck/issues/1)
- ![tracking pixel](https://evil.example/tracker.png)
- <b onclick="window.__pwned = 6">tag bold</b> and **real bold**
- Bare https://github.com/sadrazkh/DeviceDeck/releases and https://evil.example/bare
- &lt;script&gt; written as text
`;
