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

## Clean-up (round 2)

Storage analysis per phone and the clean-up actions. The analysis only reads. Nothing is deleted unless the page
sends the exact items the user confirmed, each with the size the page showed, and their total as `expectedBytes`.
The host deletes an item only when the last analysis listed it at that size and it is still the same on the phone;
anything that changed is skipped and counted. With `copyFirstTo` (a PC folder) the items are copied first through the
transfer queue (`keepBoth`, originals unconverted), and nothing is deleted unless every copy finished.
Deletes wait while an analysis runs. A report is dropped when its phone disconnects.

| Method | Params | Result |
| --- | --- | --- |
| `cleanup.status` | `{ deviceId }` | `CleanupStatus` |
| `cleanup.analyze` | `{ deviceId }` | `CleanupStatus` (starts one, or returns the one running) |
| `cleanup.cancel` | `{ deviceId }` | `{}` |
| `cleanup.deleteMedia` | `{ deviceId, items: { id, size }[], expectedBytes, copyFirstTo? }` | `CleanupResult` — Android only (MediaStore); an iPhone answers `not_supported`: copy to the PC, then delete in the Photos app |
| `cleanup.deleteFiles` | `{ deviceId, items: { root, path, size }[], expectedBytes, copyFirstTo? }` | `CleanupResult` |
| `cleanup.uninstallApps` | `{ deviceId, items: { id, size }[], expectedBytes, copyFirstTo? }` | `CleanupResult` — `size` is appBytes + dataBytes; copy first saves the APK (Android), not the app's data |
| `cleanup.trimCaches` | `{ deviceId }` | `CleanupResult` — Android `pm trim-caches`: every app's cache; `freedBytes` is free space after minus before |

| Event | Params |
| --- | --- |
| `cleanup.updated` | `CleanupStatus` (progress at most 4/s, every state change) |

```ts
type CleanupSection = "storage" | "apps" | "media" | "caches" | "appFiles" | "duplicates";
type CleanupStatus = {
  deviceId: string; state: "idle" | "running" | "done" | "failed" | "cancelled";
  progress: { section: CleanupSection; done: number; total: number } | null;   // bytes for duplicates, items otherwise
  report: CleanupReport | null;        // partial while running
  error: { code: string; message: string } | null; finishedAt: string | null;
};
type CleanupReport = {
  deviceId: string; platform: "ios" | "android"; startedAt: string;
  totalBytes: number | null; freeBytes: number | null;
  apps: { largest: AppInfo[]; count: number; totalBytes: number; canUninstall: boolean; canCopyFirst: boolean } | null;
  media: { largestVideos: PhotoAsset[]; largestPhotos: PhotoAsset[]; videoCount: number; videoBytes: number;
           photoCount: number; photoBytes: number; cloudOnlyCount: number; canDelete: boolean } | null;
  caches: { totalBytes: number | null; apps: { appId: string; name: string | null; bytes: number }[];
            trimmedBytes: number | null; trimmedAt: string | null } | null;          // Android: as the phone last counted (about daily)
  appFiles: { files: { root: string; path: string; name: string; size: number; modified: string | null;
                       appId: string | null; folder: string | null }[];
              totalBytes: number; minBytes: number; appNames: Record<string, string>; canDelete: boolean } | null;
  duplicates: { groups: { hash: string; size: number; assets: PhotoAsset[]; reclaimableBytes: number }[];  // oldest asset first
                reclaimableBytes: number; hashedCount: number; hashedBytes: number; canDelete: boolean } | null;
  notes: { section: CleanupSection; code: string; message: string }[];
};
type CleanupResult = { deleted: number; skipped: number; freedBytes: number; copyJobIds: string[]; copyIncomplete: boolean };
```

Duplicates are exact: same size and same whole-file hash (md5 on an Android phone, SHA-256 of the file read over AFC
on an iPhone, after a first pass over the opening 64 KiB). iCloud-only originals are left out.

## Battery history (round 2)

A snapshot is stored in `battery.db` each time a phone becomes ready (at most once per 30 minutes per phone).
`healthEstimated` is true for an iPhone: full-charge over design capacity, which differs from iOS Settings.

| Method | Params | Result |
| --- | --- | --- |
| `battery.devices` | — | `BatteryDevice[]` (every phone with history, connected or not, most recent first) |
| `battery.history` | `{ deviceId }` | `BatterySnapshot[]` (oldest first) |
| `battery.record` | `{ deviceId }` | `BatterySnapshot` (now, whatever the interval) |
| `battery.export` | `{ deviceId, destination, language?: "en" \| "fa" }` | `{ path }` — a self-contained HTML report (prints to PDF) in the folder |

| Event | Params |
| --- | --- |
| `battery.recorded` | `BatterySnapshot` |

```ts
type BatterySnapshot = {
  id: number; deviceId: string; deviceName: string; platform: "ios" | "android"; model: string; osVersion: string;
  at: string; levelPercent: number | null; isCharging: boolean; healthPercent: number | null; healthEstimated: boolean;
  cycleCount: number | null; designCapacityMah: number | null; fullChargeCapacityMah: number | null; temperatureCelsius: number | null;
};
type BatteryDevice = { deviceId: string; name: string; platform: "ios" | "android"; model: string; count: number;
                       firstAt: string; lastAt: string; latest: BatterySnapshot };
```
