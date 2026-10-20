# DeviceDeck — working rules

Windows 11 app that manages an iPhone and an Android phone (photos, files, app documents, apps, backups, tools).
Plan and design brief: https://claude.ai/code/artifact/e6bbc0f8-d924-42c0-86a0-f7f6ae279bfd (tabs "Design brief" and "Build plan").

## Layout and who owns what (parallel round 1)

| Path | Owner | Notes |
| --- | --- | --- |
| `src/DeviceDeck.Core/Abstractions/` | frozen | shared models + interfaces; propose changes in your report |
| `docs/bridge-contract.md` | frozen | JSON-RPC contract between the page and the host |
| `web/` | Web UI session | React + TypeScript + Vite, mock backend |
| `src/DeviceDeck.Shell/`, `src/DeviceDeck.Bridge/`, `src/DeviceDeck.Core/` (outside Abstractions), `tests/DeviceDeck.Bridge.Tests/` | Host session | WinUI 3 window, WebView2, JSON-RPC, device hub, transfer queue; the only session that edits `DeviceDeck.slnx` |
| `src/DeviceDeck.Ios/`, `tools/IosProbe/`, `tests/DeviceDeck.Ios.Tests/` | iPhone session | usbmux, lockdown, AFC, house_arrest, apps, photos |
| `src/DeviceDeck.Android/`, `tools/AndroidProbe/`, `tests/DeviceDeck.Android.Tests/` | Android session | adb, sync, packages, MediaStore |

Touch only your own paths. Need something in a frozen or foreign path? Write it in your report.

## Conventions

- .NET 10 (`net10.0`; the Shell `net10.0-windows10.0.19041.0`), nullable on, async all the way, `CancellationToken` on every I/O call.
- Providers throw `DeviceException` with a `DeviceErrorCodes` code; never return half-filled data silently.
- No phone is connected to this PC during round 1: test against fixtures (real command output / plist captured into `tests/**/Fixtures/`) and keep a probe CLI the owner runs later with a phone plugged in.
- Never commit third-party binaries (adb, scrcpy, DLLs): fetch scripts put them in git-ignored `third_party/`.
- Ports: never bind 10808 or 10085 (the owner's IRNetFree app holds them). Bundled adb uses server port 5137.
- Licences: MIT, Apache-2.0, BSD, or LGPL dynamically linked. No GPL code shipped (pymobiledevice3 is a reference only).
- Out of scope, always: jailbreak, iCloud / activation-lock / FRP bypass, firmware flashing, skipping the phone's Trust or permission prompts.

## Commits

- Author is the repo's configured git user only; no `Co-Authored-By` trailer.
- Small commits with plain messages on your own branch; never push, never merge into `main` — the owner merges.

## Build and test

```
dotnet build DeviceDeck.slnx
dotnet test DeviceDeck.slnx
cd web && npm ci && npm run build && npm test
```
