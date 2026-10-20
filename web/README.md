# DeviceDeck web UI

React 19 + TypeScript (strict) + Vite. Runs inside the WinUI 3 host's WebView2 over Mica, or in any
browser against a built-in mock backend.

```
npm ci
npm run dev      # http://localhost:5173 (mock backend)
npm run build    # type-check + dist/
npm test         # Vitest, then Playwright in the installed Microsoft Edge
```

Playwright uses `channel: 'msedge'` and never downloads browsers. Screenshots of every screen in light,
dark and right-to-left Persian land in `test-results/screens/` (git-ignored); every page must log no
console errors or warnings.

## Layout

| Path | What |
| --- | --- |
| `src/styles/tokens.css` | every colour, radius, spacing step, type size, shadow and motion value (light, dark, `[dir=rtl]`, density) |
| `src/bridge/` | typed JSON-RPC client for every method and event in `docs/bridge-contract.md`, WebView2 transport, mock transport |
| `src/bridge/mock/` | mock backend: sample data (`mock-data.ts`), ~13,000 / 23,000 generated photos per phone, file trees, transfer queue simulation |
| `src/i18n/` | in-house dictionary (`en.ts`, `fa.ts`), plurals, Persian digits, Solar Hijri months in Persian |
| `src/state/` | zustand stores (per-phone place, drawer, palette, toasts, prefs) and pure selection / queue helpers |
| `src/app/` | React Query hooks over the bridge, live event wiring, shortcuts, document attributes |
| `src/shell/` | title bar, nav rail / top tabs, transfer drawer, toasts, command palette, conflict dialog |
| `src/screens/` | Welcome, Overview, Photos, Files, App documents, Apps, Settings (basic), "coming next" |

## Mock backend switches (URL query, browser only)

| Param | Values |
| --- | --- |
| `mock` | `ready` (default, both phones), `empty`, `trust` (Trust / USB-debugging prompts), `limited` (iPhone without Apple's driver), `driverMissing`, `iphoneOnly` |
| `device` | `iphone`, `android` or a device id |
| `screen` | `overview`, `photos`, `files`, `appDocuments`, `apps`, `contacts`, `backup`, `toolbox`, `settings` |
| `lang`, `theme`, `density`, `nav` | `en`/`fa`, `light`/`dark`/`system`, `comfortable`/`compact`, `rail`/`tabs` |
| `latency`, `indexing`, `queue` | round-trip ms (default 40); `indexing=0` skips the first-indexing simulation; `queue=0` starts with an empty transfer queue |

The command palette (Ctrl+K) has a **Simulation** group: pull a phone's cable, plug it back, connect a new
iPhone or Android phone, tap Trust / Allow, copy a film into VLC with a name conflict, toggle the Apple
driver. The same controls are on `window.deviceDeckMock` (`unplug(id)`, `plugBack(id)`, `connectNew(id)`,
`approve(id)`, `setDrivers(list)`).

## Adopting another design direction

- Colours, type, radii, spacing, shadows and motion: edit `src/styles/tokens.css` (component CSS reads variables only).
- Side rail or top tabs: `DEFAULT_NAV_LAYOUT` in `src/state/prefs.ts` (also switchable in Settings and the palette).
- "Device Stage" (the phone's colour themes the app): the shell sets `--device-color` from `DeviceSummary.colorHex`;
  point accent tokens at it.

## Host notes

- `html` and `body` stay transparent when `window.chrome.webview` exists (`data-host="webview"`); a plain browser gets a neutral backdrop.
- The title bar marks drag regions with `app-region: drag` and keeps clear of the native caption buttons using the host's `--dd-caption-left` / `--dd-caption-right` / `--dd-titlebar-height` (physical sides, so the gap stays on the right in Persian too); a plain browser falls back to 140 px on the right.
- Images come from `https://device.local/…` exactly as in the contract; dropped files go through `postMessageWithAdditionalObjects` with `params.files = []`.
- The production build carries a CSP with `connect-src 'none'`: the page makes no network requests.

Keyboard: Ctrl+K / Ctrl+F palette, Ctrl+1–9 sections, Alt+←/→ back and forward; in grids and lists arrows, Shift ranges,
Ctrl+A, Esc, Space / Enter (open), F2 (rename), Delete, Ctrl+E (export photos).

Bundled font: Vazirmatn (SIL OFL 1.1) from `@fontsource-variable/vazirmatn`; Segoe UI Variable comes with Windows 11.
