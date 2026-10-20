# Third-party notices

DeviceDeck ships or builds on the components below. Each stays under its own licence.

## Bundled in the release zip

| Component | Version | Licence |
| --- | --- | --- |
| .NET runtime | 10 | MIT |
| Windows App SDK (WinUI 3) | 2.5.1 | Microsoft Software License Terms (redistributable) |
| Microsoft.Data.Sqlite, SQLitePCLRaw, SQLite | 10.0.13 | MIT, Apache-2.0, public domain |
| AdvancedSharpAdbClient | 3.6.16 | Apache-2.0 |
| SkiaSharp (Skia) | 3.119.4 | MIT (Skia: BSD-3-Clause) |
| Android SDK Platform-Tools (adb) from Google | latest at build time | Android Software Development Kit License; Google's own notices are in `platform-tools/NOTICE.txt` |
| scrcpy-server (Genymobile), pushed to the phone for screen mirroring | 5.0.1 | Apache-2.0; the licence is in `scrcpy/LICENSE` |
| React, React DOM | 19.3.1 | MIT |
| Radix UI | 1.8.1 | MIT |
| TanStack Query, TanStack Virtual | 5.104.5, 3.14.21 | MIT |
| Zustand | 5.0.15 | MIT |
| Framer Motion | 14.4.0 | MIT |
| Fluent UI System Icons (@fluentui/react-icons) | 2.0.344 | MIT |
| Vazirmatn font (@fontsource-variable/vazirmatn) | 5.3.0 | SIL Open Font License 1.1 |

## Used at run time, not bundled

- Apple Mobile Device Support (Apple's USB driver and usbmux service), installed by iTunes or the Apple Devices app.
- Microsoft Edge WebView2 Runtime, part of Windows 11.

## Protocol references, no code copied

- [libimobiledevice](https://github.com/libimobiledevice/libimobiledevice) (LGPL-2.1), [pymobiledevice3](https://github.com/doronz88/pymobiledevice3) (GPL-3.0) and [Netimobiledevice](https://github.com/artehe/Netimobiledevice) (MIT) documented the iPhone protocols that `src/DeviceDeck.Ios` implements on its own.
- [scrcpy](https://github.com/Genymobile/scrcpy) (Apache-2.0): its `doc/develop.md` and `control_msg.c` describe the mirroring protocol that `src/DeviceDeck.Android/Mirror` speaks; only the unmodified server file is shipped.
- The QR encoder in `web/src/screens/toolbox/qr.ts` follows the structure of [Project Nayuki's QR Code generator](https://www.nayuki.io/page/qr-code-generator-library) (MIT), written from the ISO/IEC 18004 specification.
