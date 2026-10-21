# DeviceDeck — working rules

Windows 11 app that manages an iPhone and an Android phone (photos, files, app documents, apps, backups, tools).
Plan and design brief: https://claude.ai/code/artifact/e6bbc0f8-d924-42c0-86a0-f7f6ae279bfd (tabs "Design brief" and "Build plan").
v0.2.0 is released (tag `v0.2.0`); round 3 builds v0.3.0.

## Round 3: three parallel sessions

Every session works in its own worktree on its own branch, cut from `main`. Each owns a feature end to end
(provider code, core, bridge, UI), so files overlap at the edges. The rules that keep merges small:

| Session | Feature | Its own files |
| --- | --- | --- |
| ui | file browser toolbar and breadcrumbs that never overlap (Files and App documents, every width, Persian); drop from Explorer proven end to end, with visible drop hints; App documents and Files in the layout test | `web/src/screens/files/**`, `web/src/screens/appDocuments/**`, `web/src/components/DropZone.tsx`, `web/tests/e2e/layout*.ts`, drop handling in `src/DeviceDeck.Shell/**` |
| backup | photo backup that starts by itself when a phone connects (when the user turns that on), a "from date" range, faster downloads, a free-space check, edited iPhone photos | `*/Photo*Backup*`, the iOS photo library and AFC code, `src/DeviceDeck.Core/Transfers/TransferQueue.cs` (speed only), `web/src/screens/photos/BackupPanel.tsx`, `PhotosBackupSettings.tsx`, `tools/PhotoBench/**` |
| localsend | photos straight into the iPhone's Photos app through the LocalSend app (over the USB cable via usbmux, or Wi-Fi); a better "save them all" path without LocalSend | new `*/LocalSend/**`, `web/src/screens/photos/ImportToAppDialog.tsx` and new dialog files, `PhotosToAppHandler.cs` |

- **New capabilities = new files.** Add a new optional interface in its own file under `src/DeviceDeck.Core/Abstractions/`
  that a device may also implement, instead of adding members to `IDevice`.
- **Shared files, small edits only:** bridge method registration, `docs/bridge-contract.md` (add your section at the end),
  `web/src/bridge/methods.ts`, `web/src/bridge/client.ts` (NO_TIMEOUT), the mock backend, `web/src/i18n/*`, Settings,
  `TransferModels.cs` (new job kinds at the end), `HostServices.cs`, `BridgeServices.cs`, `tests/**/TestHost.cs`.
  One self-contained block per feature at the end, so the coordinator can merge them; never reformat or reorder someone else's part.
- `web/src/screens/photos/backupQueries.ts` is shared by backup and localsend: each edits only its own functions.
- `DeviceDeck.slnx`: only add a project if you really need a new one, and say so in your report.
- Ports this round: Playwright `PW_PORT` ui 5201, backup 5202, localsend 5203; DevTools ports 9361, 9362, 9363.

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

- **No phone until the final phase.** The owner connects his iPhone only at the end of the round, after the merge.
  Build and test everything with the fake providers, fixtures and test servers. Whatever needs the real phone, write into
  your report as an exact checklist (commands, expected results); the coordinator runs it at the end.
- In that final phase, on the owner's iPhone 16 (iOS 27): read-only checks are fine; the photo backup test copies
  **only the last month of photos** into a folder under `E:\DeviceDeck-test\`, never the whole library; LocalSend may send
  **3 generated, clearly labelled test images** into Photos (the owner deletes them himself). Never install, uninstall,
  reboot, restore or run a full iPhone backup on it, and never delete his photos or files.
- The PC's C: drive has little free space (about 12 GB): never put photo copies, backups or other large test output there.
- No Android phone: fixtures and the fake adb server.
- Never move the mouse, type into his desktop, or capture the screen. Check the running app through the WebView2 DevTools port
  (`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>`), reading the DOM; close it with `CloseMainWindow()`.
  Only one DeviceDeck window can run at a time (single-instance key): if a copy is already running, never quit it — say so.
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
