# Bridge contract (v0)

The React page and the .NET host talk JSON-RPC 2.0 over WebView2 web messages. This file is the
contract both sides build against; the C# shapes live in `src/DeviceDeck.Core/Abstractions`.
Frozen for the parallel round — propose changes in your report, don't edit it in a feature branch.

## Transport

| Direction | How |
| --- | --- |
| page → host | `window.chrome.webview.postMessage(JSON.stringify(request))` |
| host → page | `CoreWebView2.PostWebMessageAsJson(json)`; the page listens on `window.chrome.webview.addEventListener('message', e => e.data)` |
| dropped files | `window.chrome.webview.postMessageWithAdditionalObjects(JSON.stringify(request), fileList)` — the host reads each `CoreWebView2File.Path`; the request's `params.files` is `[]` and the host fills it from the additional objects |
| images | plain `<img src>` on the virtual host `https://device.local/` (served by `WebResourceRequested`, never base64 in JSON) |

Outside WebView2 (`window.chrome?.webview` undefined) the page uses its mock backend with the same methods.

- Requests: `{ "jsonrpc": "2.0", "id": 7, "method": "device.list", "params": {} }`
- Results: `{ "jsonrpc": "2.0", "id": 7, "result": … }`
- Events: notifications with no `id`: `{ "jsonrpc": "2.0", "method": "device.changed", "params": {…} }`
- JSON: camelCase properties, enums as camelCase strings (`"ios"`, `"awaitingTrust"`), `DeviceCapability` as an array of camelCase names (`["info","storage","photosRead"]`), dates ISO 8601, sizes in bytes.

## Errors

`{ "code": -32000, "message": "<one plain sentence>", "data": { "code": "device_locked", "detail": "<technical text>" } }`

`data.code` is one of `DeviceErrorCodes` (`device_not_found`, `device_disconnected`, `device_locked`,
`trust_required`, `unauthorized`, `not_supported`, `path_not_found`, `permission_denied`,
`already_exists`, `install_failed`, `driver_missing`, `protocol_error`) or `internal`.
Standard JSON-RPC codes for bad requests (`-32600`, `-32601`, `-32602`).

## Methods

`deviceId` is `DeviceSummary.id`. `jobId` methods start work in the transfer queue and return at once.

| Method | Params | Result |
| --- | --- | --- |
| `app.hello` | — | `{ version, isWebView: true }` |
| `drivers.status` | — | `DriverStatus[]` |
| `device.list` | — | `DeviceSummary[]` |
| `device.storage` | `{ deviceId }` | `StorageInfo` |
| `device.battery` | `{ deviceId }` | `BatteryInfo` |
| `device.details` | `{ deviceId }` | `DeviceDetails` |
| `photos.albums` | `{ deviceId }` | `PhotoAlbum[]` |
| `photos.list` | `{ deviceId, albumId?, offset, limit }` | `Page<PhotoAsset>` |
| `photos.export` | `{ deviceId, assetIds, destination, convertHeic: bool, folderPattern: "yyyy/MM" \| "" }` | `{ jobId }` |
| `photos.delete` | `{ deviceId, assetIds }` | `{ deleted: number }` |
| `photos.import` | `{ deviceId, files: string[] }` | `{ jobId }` |
| `files.roots` | `{ deviceId }` | `FileRoot[]` |
| `files.list` | `{ deviceId, root, path }` | `FileEntry[]` |
| `files.mkdir` | `{ deviceId, root, path }` | `FileEntry` |
| `files.rename` | `{ deviceId, root, from, to }` | `FileEntry` |
| `files.delete` | `{ deviceId, root, paths, recursive }` | `{ deleted: number }` |
| `files.copyToPc` | `{ deviceId, root, paths, destination }` | `{ jobId }` |
| `files.copyToDevice` | `{ deviceId, root, files: string[], path }` | `{ jobId }` |
| `apps.list` | `{ deviceId, includeSystem }` | `AppInfo[]` |
| `apps.install` | `{ deviceId, files: string[] }` | `{ jobId }` |
| `apps.uninstall` | `{ deviceId, appId }` | `{}` |
| `apps.export` | `{ deviceId, appId, destination }` | `{ jobId }` |
| `tools.screenshot` | `{ deviceId }` | `{ url }` (a `https://device.local/shot/…` image) |
| `tools.reboot` | `{ deviceId, target: "system" \| "recovery" \| "bootloader" }` | `{}` |
| `tools.shutdown` | `{ deviceId }` | `{}` |
| `transfers.list` | — | `TransferJob[]` |
| `transfers.pause` / `resume` / `cancel` | `{ jobId }` | `{}` |
| `transfers.resolveConflict` | `{ jobId, choice: "replace" \| "keepBoth" \| "skip", applyToAll }` | `{}` |
| `shell.pickFolder` | `{ title? }` | `{ path: string \| null }` |
| `shell.revealInExplorer` | `{ path }` | `{}` |
| `settings.get` | — | `Settings` |
| `settings.set` | `Partial<Settings>` | `Settings` |

## Events

| Event | Params |
| --- | --- |
| `device.added` / `device.changed` | `DeviceSummary` |
| `device.removed` | `{ deviceId }` |
| `drivers.changed` | `DriverStatus[]` |
| `transfer.updated` | `TransferJob` (throttled to 10/s per job) |
| `photos.indexProgress` | `{ deviceId, done, total }` |

## Bridge-only shapes

```ts
type TransferJob = {
  id: string; deviceId: string;
  kind: "photosExport" | "photosImport" | "copyToPc" | "copyToDevice" | "appInstall" | "appExport";
  title: string;                       // "Exporting 1,284 photos to D:\\Photos\\2026"
  state: "queued" | "running" | "paused" | "waitingForDevice" | "needsDecision" | "done" | "failed" | "cancelled";
  totalItems: number; doneItems: number; failedItems: number;
  totalBytes: number; doneBytes: number;
  bytesPerSecond: number | null; etaSeconds: number | null;
  conflict: { itemName: string; existingSize: number; newSize: number } | null;  // set when state = needsDecision
  error: { code: string; message: string } | null;
  createdAt: string; finishedAt: string | null;
};

type Settings = {
  language: "en" | "fa"; theme: "system" | "light" | "dark"; density: "comfortable" | "compact";
  photoExportFolder: string; photoFolderPattern: "yyyy/MM" | ""; convertHeic: boolean;
  fileCopyFolder: string; conflictRule: "ask" | "replace" | "keepBoth" | "skip";
};
```

## Image URLs (`https://device.local/`)

| Path | Serves |
| --- | --- |
| `thumb/{deviceId}/{assetId}?s=256` | `IPhotoLibrary.GetThumbnailAsync` (JPEG, disk-cached) |
| `photo/{deviceId}/{assetId}` | the original, for the viewer (HEIC converted to JPEG on the fly) |
| `icon/{deviceId}/{appId}` | `IAppManager.GetIconAsync` (PNG) |
| `shot/{deviceId}/{shotId}` | a screenshot taken by `tools.screenshot` |

Ids in paths are URI-encoded. A missing image is a 404; the page shows a placeholder.

## Desktop (round 2)

Added by the desktop session. C#: `src/DeviceDeck.Bridge/Methods/DesktopMethods.cs`, `src/DeviceDeck.Bridge/IDesktopShell.cs`,
`src/DeviceDeck.Core/Notifications/INotificationService.cs`; TypeScript: the "Desktop (round 2)" blocks of
`web/src/bridge/types.ts` and `methods.ts`.

### Methods

| Method | Params | Result |
| --- | --- | --- |
| `device.rename` | `{ deviceId, name }` (1–64 characters, trimmed) | `DeviceSummary` (also announced with `device.changed`) |
| `transfers.retry` | `{ jobId }` | `{}`: a failed or cancelled job, or one done with failed items, runs again; anything else fails with `internal` |
| `transfers.clear` | `{ jobIds? }` | `{ cleared }`: finished jobs (done, failed, cancelled) leave the list and the database; all of them, or those listed |
| `notifications.show` | `{ title, body?, tag?, deviceId?, command?, buttons?: [{ label, command }], args? }` | `{}` (at most three buttons) |
| `pc.roots` | — | `PcRoot[]`: home, desktop, downloads, documents, pictures, videos, music, then the drives |
| `pc.list` | `{ path, showHidden? }` | `PcEntry[]` (hidden and system items only with `showHidden`) |
| `pc.mkdir` | `{ path }` | `PcEntry`; `already_exists` when the name is taken |
| `pc.rename` | `{ path, name }` (a bare name) | `PcEntry` |
| `shell.openFile` | `{ path }` | `{}`: opens a PC file with its default app |
| `shell.dragOut` | `{ source: "photos", deviceId, items }` · `{ source: "files", deviceId, root, items }` · `{ source: "pc", paths }` | `{ effect: "copy" \| "none", droppedInside: { x, y } \| null }`, when the drag ends (no timeout) |
| `app.updateStatus` | — | `UpdateStatus` |
| `app.checkForUpdates` | — | `UpdateStatus` after the check (a new version starts downloading) |
| `app.applyUpdate` | — | `{}`; the app quits and Velopack restarts it on the new version |

PC errors use the usual codes: `path_not_found`, `permission_denied`, `already_exists`. `shell.dragOut`, `shell.openFile` and
`app.*` need the window; elsewhere (tests) they fail with `not_supported`.

**Drag out.** On `dragstart` the page calls `preventDefault()` and `shell.dragOut` with what the user is dragging (`items` are
`{ id, name, size, isDirectory, modified }`: an asset id or a file's path in its root). The host starts a native drag while the button
is still down. File Explorer receives virtual files; when it asks for the first file's contents a `dragOut` job starts in the
transfer queue and copies each file while Explorer reads it (pause, a pulled cable and retries as for any job; Explorer's copy
waits). Dragged phone folders are walked on the phone. PC files travel as a plain file drop. A drop back on DeviceDeck's own window
is cancelled and returned as `droppedInside` (CSS pixels): the page decides (the PC pane copies phone files into the folder under
the pointer; the phone pane copies PC files into its open folder).

### Events

| Event | Params |
| --- | --- |
| `notification.activated` | `{ command, deviceId, tag, args }`: a click on a notification or one of its buttons; the window is already in front. `open` = show (the page selects `deviceId`) |
| `app.updateChanged` | `UpdateStatus` |

### Shapes

```ts
// DeviceSummary gains:
stateDetail: string | null;   // one plain sentence when the state needs the user: "Don't Trust was tapped · unplug the iPhone and plug it in again, then tap Trust"
// DeviceCapability gains "rename" (bit 40): the phone can be renamed from the PC.
// TransferKind gains "dragOut".
// Settings gains:
closeToTray: boolean;      // default true: closing the window keeps DeviceDeck in the notification area (Quit in its menu)
notifyOnConnect: boolean;  // default true: a Windows notification when a phone connects while the window isn't in front
autoUpdate: boolean;       // default true: check GitHub Releases a minute after start and every 6 h, download in the background

type PcRoot = { path: string; label: string; kind: "home" | "desktop" | "downloads" | "documents" | "pictures" | "videos" | "music"
  | "drive" | "removableDrive" | "networkDrive" | "opticalDrive"; totalBytes: number | null; freeBytes: number | null };
type PcEntry = { path: string; name: string; isDirectory: boolean; size: number; modified: string | null; readOnly: boolean; hidden: boolean };
type UpdateStatus = { state: "notInstalled" | "idle" | "checking" | "upToDate" | "downloading" | "ready" | "error";
  currentVersion: string; newVersion: string | null; percent: number | null; error: string | null; lastChecked: string | null };
```

`notInstalled`: the portable zip (only a copy installed by the Setup can update itself).

### Notifications from C#

Any feature can show a Windows notification through `BridgeServices.Notifications` (`INotificationService`):

```csharp
services.Notifications.Show(new AppNotification("12 new photos", "On Nima's iPhone")
{
    Tag = $"photos:{deviceId}",                       // a later one with the same tag replaces it
    DeviceId = deviceId,                              // the page selects this phone on a click
    Buttons = [new NotificationButton("Back up", "photos.backup")],
});
services.Notifications.Activated += (_, click) => { if (click.Command == "photos.backup") { /* … */ } };
```

`Show` never throws. Clicks arrive on a background thread after the window is in front, and also reach the page as
`notification.activated`. Tests use `RecordingNotificationService` (records, and `Click(n, "photos.backup")` plays a click).
The window uses Windows App SDK app notifications, or a balloon from its tray icon where those aren't available.

### Video URL

| Path | Serves |
| --- | --- |
| `video/{deviceId}/{assetId}?size={PhotoAsset.size}` | the original of a video with HTTP Range (200, 206 or 416; `Accept-Ranges: bytes`), so `<video>` can seek. The file is copied off the phone once into a cache and each range is answered as soon as its bytes are there; the last three videos stay cached. Without `size` the answer waits for the whole copy. |
