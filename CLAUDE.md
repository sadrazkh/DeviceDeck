# DeviceDeck — working rules

Windows 11 app that manages an iPhone and an Android phone (photos, files, app documents, apps, backups, tools).
Plan and design brief: https://claude.ai/code/artifact/e6bbc0f8-d924-42c0-86a0-f7f6ae279bfd (tabs "Design brief" and "Build plan").
v0.1.0 is released (tag `v0.1.0`); round 2 builds v0.2.0.

## Round 2: six parallel feature sessions

Every session works in its own worktree on its own branch, cut from `main`. Each owns a feature end to end
(provider code, core, bridge, UI), so files overlap at the edges. The rules that keep merges small:

| Session | Feature | Its own files (new) |
| --- | --- | --- |
| desktop | sharp text, tray + notifications, drag out to Explorer, PC folders in Files, installer + auto-update, ARM64, small contract items | `src/DeviceDeck.Shell/**`, `web/src/shell/**`, `.github/workflows/**`, `tools/package.ps1` |
| photos | incremental photo backup to the PC, HEIC → JPG, adding photos to the phone | `*/Photos*`, `web/src/screens/photos/**` |
| appfiles | import / export of files for apps that share files, both platforms | `*/AppFiles*`, `web/src/screens/appdocs/**`, `web/src/screens/files/**` |
| cleanup | storage analysis, confirmed clean-up, battery history | `*/Cleanup*`, `*/Battery*`, `web/src/screens/cleanup/**`, `web/src/screens/battery/**` |
| backup | iPhone backup / restore / browse, contacts and messages export | `*/Backup*`, `*/Contacts*`, `web/src/screens/backup/**`, `web/src/screens/contacts/**` |
| toolbox | live log, crash reports, device report, HEIC converter for PC files, Android mirroring, Wi-Fi connection, ringtones | `*/Toolbox*`, `*/Mirror*`, `*/Wifi*`, `web/src/screens/toolbox/**` |

- **New capabilities = new files.** Add a new optional interface in its own file under `src/DeviceDeck.Core/Abstractions/`
  (for example `IPhotoBackupSource.cs`) that a device may also implement, instead of adding members to `IDevice`.
  Existing members of the round-1 interfaces don't change unless your brief says so.
- **Shared files, small edits only:** bridge method registration, `docs/bridge-contract.md` (add your section at the end),
  `web/src/bridge/methods.ts` and the mock backend (add your block at the end), the nav list, Settings.
  One self-contained block per feature, so the coordinator can merge them; never reformat or reorder someone else's part.
- `DeviceDeck.slnx`: only add a project if you really need a new one, and say so in your report.

## Conventions

- .NET 10 (`net10.0`; the Shell `net10.0-windows10.0.19041.0`), nullable on, async all the way, `CancellationToken` on every I/O call.
- Providers throw `DeviceException` with a `DeviceErrorCodes` code; never return half-filled data silently.
- Never delete anything on a phone or the PC without the user confirming in the UI: list what goes and how much space it frees.
- Never commit third-party binaries (adb, scrcpy, DLLs): fetch scripts put them in git-ignored `third_party/`.
- Ports: never bind 10808, 10809, 10085, 30808, 30809 or 30085 (the owner's IRNetFree app). Bundled adb uses server port 5137.
- Licences: MIT, Apache-2.0, BSD, or LGPL dynamically linked. No GPL code shipped (pymobiledevice3 is a reference only).
- Out of scope, always: jailbreak, iCloud / activation-lock / FRP bypass, firmware flashing, skipping the phone's Trust or permission prompts,
  getting around any protection the phone or an app puts on its data.

## Testing

- The owner's iPhone 16 (iOS 27) may be plugged in: read-only checks are fine; writes only as test files you delete again;
  never install, uninstall, reboot, restore or run a full backup on it, and never delete his photos or files.
- No Android phone is available this round: fixtures and the fake adb server.
- Never move the mouse, type into his desktop, or capture the screen. Check the running app through the WebView2 DevTools port
  (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>`), reading the DOM; close it with `CloseMainWindow()`.
- Timing tests assert order or overlap, not tight wall-clock windows; before you finish, run the suite with
  `DOTNET_PROCESSOR_COUNT=1` a few times (GitHub's runner is slower than this PC).

## Commits

- Author is the repo's configured git user only; no `Co-Authored-By` trailer.
- Small commits with plain messages on your own branch; never push, never merge into `main` — the coordinator merges.

## Build and test

```
dotnet build DeviceDeck.slnx
dotnet test DeviceDeck.slnx
cd web && npm ci && npm run build && npm test
```
