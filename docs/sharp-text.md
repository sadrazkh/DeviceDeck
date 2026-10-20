# Sharp text in the window

DeviceDeck's page is transparent so Windows 11's Mica shows through. Chromium (inside WebView2) only uses
ClearType (LCD, subpixel) anti-aliasing for text in a composited layer it knows is opaque; everything painted
into the transparent page root gets grayscale anti-aliasing instead. That looks soft, or slightly haloed, next to
other apps, and more so on some monitors than others. Screenshots don't show it, because a screenshot of
grayscale text looks fine; the difference is how the pixels sit on the panel's subpixels.

## What the window does

- The content area (`main#content`) is opaque, and the screens sit in `.layer` inside it: a square, opaque layer
  of its own (`backface-visibility: hidden` promotes it without a transform), clipped by `main`'s rounded corner.
- Mica shows only behind the title bar and the navigation rail. Their text is grayscale, like the text on Mica in
  File Explorer, Settings and every other Windows 11 app.
- Sticky headers and overlays (the transfer drawer, dialogs, menus) use their solid colours in the host, and the
  acrylic blur is off there (`--blur-acrylic: none` in tokens.css). The drawer has the same opaque inner layer.
- All of this is under `:root[data-host='webview']`; in a plain browser the page root is opaque anyway.
- The window is per-monitor DPI aware (PerMonitorV2 in app.manifest). The WinUI WebView2 control sets the
  browser's rasterization scale from the XAML root, so moving the window to a monitor with another scale re-renders
  text at that scale instead of stretching it; the Shell logs the scale it sees on each change.

## How it was checked

Through the WebView2 DevTools port (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>`), a
trace with the `disabled-by-default-cc.debug` category gives `LayerTreeHostImpl:snapshot` events; each layer carries
`lcd_text_disallowed_reason`.

| Layer | Before | After |
| --- | --- | --- |
| Page root (title bar, rail, and before: everything) | `background-color-not-opaque` | grayscale on Mica, by design |
| `.layer` (every screen) | — (did not exist) | `none`, `contents_opaque: true` |
| Photos month header, Apps table header (sticky) | `background-color-not-opaque` | `none` |
| Transfer drawer | `background-color-not-opaque` | `none` (its `.pane`) |

Things that turned LCD text off again while trying: a translucent border on a rounded opaque layer
(`contents-not-opaque`), `will-change: transform` (`will-change-transform`), and relying on "text over an opaque
background" inside a layer with overflowing content (`contents-not-opaque` as soon as a screen scrolls).
A new screen gets ClearType automatically as long as it renders inside `.layer`; a new overlay needs an opaque
background and no backdrop filter.

## If one monitor still looks different

Chromium uses one ClearType setting for all monitors. If the two monitors differ (one rotated, or a BGR panel), run
the ClearType Text Tuner (`cttune`) and tune on the monitor DeviceDeck usually sits on.
