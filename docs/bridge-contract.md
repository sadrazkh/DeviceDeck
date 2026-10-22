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

## App files (round 2)

The apps that share files with a computer, and their folders. Every folder is a file root: browse it with
`files.list`, change it with `files.mkdir` / `files.rename` / `files.delete`, copy in with `files.copyToDevice`
and out with `files.copyToPc`, exactly as any other root. Needs `appDocuments` (iPhone and Android).

| Method | Params | Result |
| --- | --- | --- |
| `appFiles.list` | `{ deviceId }` | `AppFilesOverview` |
| `appFiles.measure` | `{ deviceId, root, paths }` (1–1000 paths) | `FolderUsage`: files, folders and bytes under each path, all the way down |

Root keys of app folders:

| Key | Folder |
| --- | --- |
| `app:<bundleId>` | an iPhone app's Documents (house_arrest VendDocuments; File Sharing apps only) |
| `appmedia:<package>` | Android/media/&lt;package&gt; |
| `appdata:<package>` | Android/data/&lt;package&gt;, listed only when the phone lets adb read Android/data |
| `shared:Documents`, `shared:Download` | the shared Documents and Download folders |

- `files.copyToDevice` takes files and whole folders (copied recursively, empty folders included) into any folder
  of the root. A name that is already there asks once (`needsDecision`); `transfers.resolveConflict` with
  `applyToAll: true` answers every later clash in the job.
- `files.copyToPc` with `paths: ["/"]` copies a whole root into a folder named after the app (`VLC`,
  `com.whatsapp (Media)`), not `_`.
- `appFiles.measure` on a path that is gone answers zeros; a folder the phone won't list counts what it can read.
- Dragging rows out to File Explorer uses the desktop feature's `shell.dragOut { source: "files", deviceId, root, paths }`
  on these roots (see "Desktop (round 2)"): Explorer receives virtual files and the copy is a `dragOut` job.
- iPhone: opening house_arrest takes about a second, so the host keeps one connection per app for listings and
  small changes; file contents use a connection of their own per job.

```ts
type AppFilesOverview = {
  entries: AppFilesEntry[];            // apps, then shared folders (Android: Documents, Download)
  privateData: "notApplicable" | "allowed" | "blocked";  // can adb read Android/data on this phone?
};
type AppFilesEntry = {
  id: string;                          // bundle id or package name; a shared folder's root key
  name: string;                        // the app's name when the host knows it (iPhone), else the id (Android: show apps.list labels)
  kind: "app" | "sharedFolder";
  appId: string | null;                // for icon/{deviceId}/{appId}; null for a shared folder
  folders: { rootKey: string; label: string; empty: boolean }[];  // first = where a drop on the card goes
};
type FolderUsage = {
  items: { path: string; files: number; folders: number; bytes: number }[];  // one per path, in order
  files: number; folders: number; bytes: number;                             // totals
};
```
## Photos backup and adding photos (round 2)

Incremental backup of a phone's photos and videos to a PC folder, the count of new photos for the
"phone connected" notification, and adding photos to an iPhone app that shares files.

### Methods

| Method | Params | Result |
| --- | --- | --- |
| `photos.newCount` | `{ deviceId }` | `PhotoBackupCount` |
| `photos.backupStatus` | `{ deviceId }` | `PhotoBackupStatus` |
| `photos.backupConfigure` | `{ deviceId, destination?, folderPattern?, convertHeic?, includeHidden?, useDefaults? }` | `PhotoBackupStatus` |
| `photos.backup` | `{ deviceId }` | `{ jobId }` (the job already queued or running for that phone, if any) |
| `photos.backupForget` | `{ deviceId }` | `{ forgotten: number }` |
| `photos.importToApp` | `{ deviceId, appId, files: string[] }` | `{ jobId }` |

- **What counts as new.** Each phone has its own record (`photo-backup.db` in the app's data folder) of the
  photos copied from it, by a stable identity: the iPhone's Photos.sqlite `ZUUID` (the DCIM path when the
  database can't be read), on Android the file's path plus its size (an edited photo is a new version).
  A photo whose identity changed is still recognised by its phone path, or by name + size + date taken.
  A Live Photo counts once. Photos only in iCloud are counted apart (`cloudOnly`) and never copied; the
  iPhone's Hidden album is left out (`hiddenLeftOut`) unless `includeHidden` is on.
- **Backup job** (`kind: "photosBackup"`, through the transfer queue: pause, resume, cancel, waits for an
  unplugged phone, carries on after a restart). One item per photo; a Live Photo's still and video are one
  item and are written under one name (`IMG_0001.HEIC` + `IMG_0001.MOV`). Files go into date folders under
  the destination by `folderPattern` (taken date in the PC's time zone; no date → `Undated`).
  Never asks about conflicts:
  - bytes already anywhere in the destination (same size, then same SHA-256) are not written again, only
    remembered (`duplicates` in the run);
  - a different file under the same name makes `IMG_0001 (1).HEIC` (both halves of a Live Photo move together);
  - files are written as `<name>.ddpart` and renamed, then get the photo's date as created/modified time.
- **HEIC → JPG** (`convertHeic`): Windows Imaging Component decodes HEIC only with the Microsoft Store's
  *HEIF Image Extensions* (and *HEVC Video Extensions*). The JPEG (quality 0.92, sRGB, pixels upright) gets
  the photo's EXIF from the HEIC — date, camera, GPS, Apple's maker note — with Orientation 1 and the JPEG's
  pixel size. **Fallback:** without the codec the file is kept as HEIC, the run counts it in `keptHeic`, and
  `heicConversion` turns `missingCodec`; the UI says how to get the extensions. Videos and JPEGs are copied as they are.
- **Options per phone.** `photos.backupConfigure` saves a phone's own options; a field left out keeps its
  value, `useDefaults: true` clears them all first. A phone without its own options uses Settings:
  destination = `photoBackupFolder` + `\<phone name>`, `photoBackupPattern`, `photoBackupConvertHeic`,
  `includeHidden` false. The destination is pinned to the phone on its first backup.
- `photos.backupForget` forgets the record only; files on the PC are never touched. The next backup reads
  every photo again and remembers the ones already in the folder without writing them.
- **`photos.importToApp`** (`kind: "photosToApp"`): there is no supported way to write into an iPhone's
  Photos library over USB. This copies PC pictures and videos (a folder brings its pictures and videos,
  flattened) into the Documents of an app that shares files (`AppInfo.fileSharing`, file root `app:<appId>`),
  e.g. Documents by Readdle or LocalSend. A taken name gets `(1)`; nothing in the app is replaced. The user
  opens that app on the iPhone (or Files › On My iPhone › the app) and saves them to Photos from there.
  Android keeps `photos.import` (into `DCIM/DeviceDeck`, scanned into the gallery).

### Host-side call for the notification

`HostServices.PhotoBackup` is a `DeviceDeck.Core.Photos.PhotoBackupService`:
`CountNewAsync(deviceId, ct)` returns the same `PhotoBackupCount` as `photos.newCount` without a bridge round
trip. Call it once the device is `ready`; it throws `DeviceException` like any provider call (`device_locked`
for a locked iPhone). On an iPhone the first call after plugging in reads the photo index (seconds; the
Photos screen and the backup reuse it for a minute). `StartAsync(deviceId, ct)` starts (or returns) the
backup job, and the `RunFinished` event fires once per finished run.

### Shapes

```ts
type PhotoBackupCount = {
  deviceId: string;
  newCount: number; newPhotos: number; newVideos: number;   // newCount = newPhotos + newVideos
  newBytes: number;          // sizes the phone already reported (an iPhone's newest few may show 0)
  cloudOnly: number;         // new, but only in iCloud: not copyable over USB, not in newCount
  hiddenLeftOut: number;     // new, in the iPhone's Hidden album, left out by the options
};

type PhotoBackupOptions = {
  destination: string;       // full PC folder
  folderPattern: string;     // "" or yyyy, MM, dd joined by / - _ . or a space; "/" = sub-folder. Default "yyyy/MM"
  convertHeic: boolean;
  includeHidden: boolean;
};

type PhotoBackupRun = {
  jobId: string; destination: string;
  state: "running" | "done" | "failed" | "cancelled";
  planned: number;           // photos the run set out to copy
  copied: number;            // written to the PC
  duplicates: number;        // already in the destination, now remembered
  failed: number; bytes: number;
  keptHeic: number;          // HEIC kept because this PC couldn't convert
  startedAt: string; finishedAt: string | null;
};

type PhotoBackupStatus = {
  deviceId: string;
  count: PhotoBackupCount;
  options: PhotoBackupOptions;
  customized: boolean;       // the phone has options of its own (false = Settings)
  backedUp: number;          // photos this PC remembers copying from the phone
  lastRun: PhotoBackupRun | null;
  activeJobId: string | null; // a backup queued, running, paused or waiting for the phone
  heicConversion: "available" | "missingCodec" | "unknown";
};
```

`TransferJob.kind` gains `"photosBackup"` and `"photosToApp"`. `Settings` gains
`photoBackupFolder: string` (default `Pictures\DeviceDeck Backup`), `photoBackupPattern: string`
(default `"yyyy/MM"`) and `photoBackupConvertHeic: boolean` (default `false`).

## Clean-up (round 2)

Storage analysis per phone and the clean-up actions. The analysis only reads. Nothing is deleted unless the page
sends the exact items the user confirmed, each with the size the page showed, and their total as `expectedBytes`.
The host deletes an item only when the last analysis listed it at that size and it is still the same on the phone;
anything that changed is skipped and counted. With `copyFirstTo` (a PC folder) the items are copied first through the
transfer queue (`keepBoth`, originals unconverted), and nothing is deleted unless every copy finished.
Deletes can't be started while an analysis runs. Since v0.6 a delete is a job that runs on after its dialog closed; one
that ends while an analysis runs is applied to that analysis' report when it finishes. A report is dropped when its
phone disconnects.

| Method | Params | Result |
| --- | --- | --- |
| `cleanup.status` | `{ deviceId }` | `CleanupStatus` |
| `cleanup.analyze` | `{ deviceId }` | `CleanupStatus` (starts one, or returns the one running) |
| `cleanup.cancel` | `{ deviceId }` | `{}` |
| `cleanup.deleteMedia` | `{ deviceId, items: { id, size }[], expectedBytes, copyFirstTo? }` | `PhotoDeleteStarted` since v0.4.1 (a `photosDelete` job; see "Deleting photos as a transfer" at the end); `not_supported` on a phone without `photosDelete` |
| `cleanup.deleteFiles` | `{ deviceId, items: { root, path, size }[], expectedBytes, copyFirstTo? }` | `DeleteStarted` since v0.6: a `filesDelete` job (see "Every delete as a transfer") |
| `cleanup.uninstallApps` | `{ deviceId, items: { id, size }[], expectedBytes, copyFirstTo? }` | `DeleteStarted` since v0.6: an `appsUninstall` job — `size` is appBytes + dataBytes; copy first saves the APK (Android), not the app's data |
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
## Toolbox (round 2)

Shapes are in `src/DeviceDeck.Bridge/Toolbox/ToolboxParams.cs` and `web/src/bridge/toolbox.ts`. Files the toolbox saves go to
`Documents\DeviceDeck\{Logs, Crash reports, Reports}` unless `destination` names a folder; nothing is ever overwritten
(a second copy gets " (2)"). Screenshots from the mirror window go to `Pictures\DeviceDeck`.

| Method | Params | Result |
| --- | --- | --- |
| `toolbox.features` | `{ deviceId }` | `{ log, crashReports, mirror, ringtone, wifiSync, report, wirelessDebugging }` (booleans) |
| `log.start` | `{ deviceId }` | `{ sessionId }`; lines arrive as `log.lines`; a page reload stops the session |
| `log.stop` / `log.clear` | `{ sessionId }` | `{}` |
| `log.export` | `{ sessionId, minLevel?, search?, destination? }` | `{ path, count }` (UTF-8 text of the kept lines that pass the filter) |
| `crash.list` | `{ deviceId }` | `CrashReportList` |
| `crash.read` | `{ deviceId, id }` | `{ text, truncated, size }` (first 2 MB) |
| `crash.export` | `{ deviceId, ids, destination? }` | `{ folder, files }` |
| `crash.delete` | `{ deviceId, ids }` | `{ deleted }`; only when `canDelete` (iPhone); the page confirms first, listing names and sizes |
| `report.preview` | `{ deviceId, language: "en" \| "fa", revealSerials }` | `{ html }` (self-contained, no scripts) |
| `report.save` | `{ deviceId, language, revealSerials, format: "html" \| "pdf", destination? }` | `{ path, count }` |
| `heic.status` | — | `{ available }` (Windows can decode HEIC) |
| `heic.convert` | `{ files: string[], quality: 1–100, destination? }` (files or folders; dropped File objects fill `files`) | `{ jobId, total }`; progress as `toolbox.job` |
| `toolbox.cancelJob` | `{ jobId }` | `{}` |
| `mirror.open` | `{ deviceId }` | `{}`; the host opens (or brings forward) the phone's mirror window |
| `wifi.status` | — | `{ wirelessDebugging }` (Android provider running) |
| `wifi.pair` | `{ host, port, code }` | `WirelessResult`; wrong code = `unauthorized`, nothing there = `device_not_found` |
| `wifi.connect` | `{ host, port }` | `WirelessResult` (`serial` = the new device id) |
| `wifi.disconnect` | `{ deviceId }` | `{}` |
| `wifi.qrStart` | — | `{ sessionId, name, password, payload }` (`payload` = `WIFI:T:ADB;S:<name>;P:<password>;;` for the QR); stages as `wifi.qr` |
| `wifi.qrCancel` | `{ sessionId }` | `{}` |
| `wifi.iphoneSync` / `wifi.setIphoneSync` | `{ deviceId }` / `{ deviceId, enabled }` | `{ enabled: boolean \| null }` |
| `ringtone.create` | `{ deviceId, files: [path], startSeconds, endSeconds, name?, setAsDefault }` | `{ devicePath, isDefault, bytes }`; Android only (an iPhone is `not_supported`) |

The mirror window's page (`mirror.html?device=<id>`) talks to its own bridge (`MirrorBridge`): `app.hello`, `settings.get`,
`mirror.start { deviceId, maxSize?, bitRate?, maxFps? }` → `{ sessionId, deviceName, codec: "h264" }`,
`mirror.input { sessionId, kind, … }` (send as a notification: no reply, applied strictly in order),
`mirror.stop { sessionId }`, `mirror.screenshot { deviceId }` → `{ path }`, `shell.revealInExplorer`.

| Event | Params |
| --- | --- |
| `log.lines` | `{ sessionId, lines: LogLine[], dropped }` (every 100 ms, at most 1,000 lines; `dropped` lines skipped for the page are still in the export) |
| `log.ended` | `{ sessionId, error: { code, message } \| null }` (the phone went away or stopped sending) |
| `toolbox.job` | `ToolboxJob` (at most 10 a second, plus the final state) |
| `wifi.qr` | `{ sessionId, stage: "waiting" \| "pairing" \| "connecting" \| "done" \| "failed" \| "cancelled" \| "expired", serial, message, error }` |
| `mirror.video` | `{ sessionId, kind: "session" \| "config" \| "frame", pts, key, width, height, data }` (`data` = base64 H.264 Annex B) |
| `mirror.ended` | `{ sessionId, error }` |

```ts
type LogLine = { seq: number; time: string | null; level: "verbose" | "debug" | "info" | "notice" | "warning" | "error" | "fault";
                 process: string; pid: number | null; tag: string | null; message: string };
type CrashReport = { id: string; fileName: string; process: string; kind: "crash" | "hang" | "memory" | "panic" | "tombstone" | "diagnostic";
                     time: string | null; size: number; readable: boolean };
type CrashReportList = { reports: CrashReport[]; canDelete: boolean; unreadable: string[] /* "tombstones", "anr" */ };
type ToolboxJob = { id: string; kind: "heic"; state: "running" | "done" | "failed" | "cancelled"; total: number; done: number;
                    failed: number; current: string | null; outputs: string[]; errors: string[] };
type WirelessResult = { serial: string | null; message: string };
type MirrorInput =
  | { kind: "touch"; action: "down" | "up" | "move" | "cancel"; pointerId: number /* -2 = mouse */; x; y; width; height; pressure }
  | { kind: "key"; down: boolean; keyCode: number /* Android KeyEvent */ } | { kind: "text"; text: string }
  | { kind: "scroll"; x; y; width; height; hScroll; vScroll } | { kind: "command"; command: "backOrScreenOn" | "rotate" | "resetVideo" | … };
```

Capabilities: an iPhone now has `syslog` and `crashReports`; an Android phone `syslog`, `crashReports` and `screenMirror`.

## Backup, contacts and messages (round 2, backup session)

iPhone backups use Apple's own format and folder layout (mobilebackup2), so iTunes, Apple Devices and other tools read
them too. Android backups (no helper app yet) are the user's APKs and shared media in a plain folder. Contacts and
messages are read from iPhone backups only; for Android the page explains that a helper app is needed.

| Method | Params | Result |
| --- | --- | --- |
| `backup.list` | `{ deviceId? }` | `BackupInfo[]` (newest first; DeviceDeck's folder plus iTunes' / Apple Devices' folders) |
| `backup.device` | `{ deviceId }` | `BackupDeviceInfo` |
| `backup.start` | `{ deviceId, full? }` | `BackupJob` |
| `backup.restore` | `{ deviceId, backupId, confirmName, password?, restoreSettings? }` | `BackupJob` |
| `backup.setEncryption` | `{ deviceId, enable, password?, oldPassword? }` | `BackupJob` |
| `backup.jobs` | — | `BackupJob[]` (running ones and the last 50 finished) |
| `backup.cancel` | `{ jobId }` | `{}` |
| `backup.prunePlan` | `{ deviceId }` | `PrunePlan` (backups beyond `settings.backupKeep`; empty `backups` when none) |
| `backup.deletePlan` | `{ backupIds }` | `PrunePlan` (what deleting those would free) |
| `backup.delete` | `{ backupIds }` | `{ deleted, freedBytes }` |
| `backup.unlock` | `{ backupId, password }` | `{}` |
| `backup.lock` | `{ backupId }` | `{}` |
| `backup.domains` | `{ backupId }` | `BackupDomain[]` (apps first, then system domains) |
| `backup.files` | `{ backupId, domain, path }` | `BackupEntry[]` (children of `path`; `""` is the domain's top) |
| `backup.extract` | `{ backupId, items: { domain, path }[], destination }` | `BackupJob` |
| `contacts.list` | `{ backupId }` | `Contact[]` |
| `contacts.export` | `{ backupId, format: "vCard" \| "csv", destination, contactIds? }` | `BackupJob` (the job's `destination` is the file written) |
| `messages.conversations` | `{ backupId }` | `Conversation[]` (newest first) |
| `messages.list` | `{ backupId, conversationId, offset, limit }` | `Page<Message>` (oldest first; limit 1–1000) |
| `messages.export` | `{ backupId, conversationIds, format: "html" \| "pdf", destination }` | `BackupJob` |

| Event | Params |
| --- | --- |
| `backup.updated` | `BackupJob` (state changes at once, progress at most 4 a second per job) |

Rules the host enforces:

- **One phone, one job.** A backup, restore or encryption change of a phone that already has one queued, running or
  waiting fails with `busy`. Extracts and exports run alongside.
- **A pulled cable pauses a backup.** The job goes `waitingForDevice` and starts again by itself when the same phone is
  ready; the phone then sends only what is missing (iPhone: what changed since its last complete backup; Android: the
  same folder continues, files already there are skipped). A restore or encryption change that loses its phone fails.
- **Restore** needs `confirmName` equal to the phone's name (case and surrounding spaces ignored), else `-32602`.
  Only complete iPhone backups restore; an encrypted one needs `password`, checked before the phone is asked; a backup
  from a newer iOS than the phone's is `refused`. The phone restarts at the end (`device.removed`, then `device.added`).
- **Nothing is deleted unasked.** After a backup, `BackupJob.prune` lists the phone's backups beyond `backupKeep` and
  what deleting them frees; only `backup.delete` (after the user confirmed that list) deletes. Backups in iTunes' or
  Apple Devices' folders are never deleted (`permission_denied`). Deleting the newest iPhone backup also deletes the
  folder the phone updates (they hold the same backup), so the next backup is a full one.
- **Encrypted backups** are opened with `backup.unlock`; the unwrapped keys stay in memory until `backup.lock` or the
  app closes. Passwords are never stored. Reading a locked one fails with `password_required`, a wrong password with
  `wrong_password`. Nothing decrypted is written to disk except what the user extracts or exports.
- **Turning encryption on or off** goes through the phone: the job sits in phase `waitingForPhone` while the phone asks
  for its passcode. On: `{ enable: true, password }`. Off: `{ enable: false, oldPassword }`. Change: both.
- **PDF** export needs the window's WebView2; without it (a test host) `messages.export` with `pdf` is `not_supported`.

Error codes added to `data.code`: `wrong_password`, `password_required`, `busy`, `backup_missing` (a file listed in the
backup isn't in it), `backup_corrupt` (unfinished or damaged backup), `no_space`, `refused` (the phone said no: Find My
on during a restore, Cancel Sync tapped, a newer iOS).

New settings: `backupFolder` (full path; default `%USERPROFILE%\DeviceDeck\Backups`) and `backupKeep` (1–20, default 3).

```ts
type BackupInfo = {
  id: string;                       // opaque: "dd:<folder>" or "apple<n>:<folder>"
  deviceId: string; deviceName: string; platform: "ios" | "android";
  model: string | null; marketingName: string | null; osVersion: string | null;
  createdAt: string | null; sizeBytes: number;   // dated copies share unchanged files, so they take less disk than the sum
  encrypted: boolean; state: "complete" | "incomplete" | "inProgress";
  source: "deviceDeck" | "apple"; path: string;  // the folder on this PC
  apps: number; locked: boolean;                 // locked = encrypted and not unlocked in this session
};

type BackupDeviceInfo = {
  deviceId: string; platform: "ios" | "android";
  folder: string; keep: number;
  keepsHistory: boolean;            // false on exFAT/FAT32/network drives: each backup replaces the last
  freeBytes: number | null;
  encrypted: boolean | null; cloudBackupEnabled: boolean | null;   // iPhone, when the phone answered
  needsHelperApp: boolean;          // Android: contacts and messages need a helper app (not in this version)
  lastBackup: BackupInfo | null;
};

type BackupJob = {
  id: string; deviceId: string | null; deviceName: string;
  kind: "backup" | "restore" | "encryption" | "extract" | "export";
  title: string;
  state: "queued" | "running" | "waitingForDevice" | "done" | "failed" | "cancelled";
  phase: "starting" | "waitingForPhone" | "preparing" | "transferring" | "finishing" | null;
  percent: number | null;           // iPhone: the phone's own estimate; otherwise files or bytes done
  bytes: number; files: number; totalBytes: number | null; totalFiles: number | null;
  bytesPerSecond: number | null;
  backupId: string | null;          // the backup made, restored or read
  destination: string | null;       // extract: the folder (or the file, for one file); export: the file or folder
  prune: PrunePlan | null;          // a finished backup: older backups beyond the keep count
  error: { code: string; message: string } | null;   // also set on a done extract when some files were missing
  createdAt: string; finishedAt: string | null;
};

type PrunePlan = { deviceId: string; keep: number; backups: BackupInfo[]; freedBytes: number };

type BackupDomain = {
  domain: string;                   // "HomeDomain", "AppDomain-net.whatsapp.WhatsApp", "media" (Android) …
  kind: "app" | "appGroup" | "appPlugin" | "system";
  appId: string | null; name: string;   // the app's name when known; otherwise the domain
  files: number; bytes: number;
};

type BackupEntry = { domain: string; path: string; name: string; isDirectory: boolean; size: number; modified: string | null };

type Contact = {
  id: string; displayName: string;
  firstName: string | null; middleName: string | null; lastName: string | null; prefix: string | null; suffix: string | null;
  nickname: string | null; organization: string | null; department: string | null; jobTitle: string | null;
  note: string | null; birthday: string | null;      // "yyyy-MM-dd" or "--MM-dd"
  phones: LabeledValue[]; emails: LabeledValue[]; urls: LabeledValue[];
  addresses: { label: string; street: string | null; city: string | null; region: string | null; postalCode: string | null; country: string | null }[];
  hasPhoto: boolean; modified: string | null;
};
// label: mobile, iphone, home, work, main, homeFax, workFax, otherFax, pager, other, school, homePage, or the user's own text
type LabeledValue = { label: string; value: string };

type Conversation = {
  id: string; title: string;
  members: { handle: string; name: string | null }[];   // names from the same backup's contacts
  isGroup: boolean; service: string;                       // "iMessage", "SMS", "RCS"
  messageCount: number; attachmentCount: number; lastMessageAt: string | null; preview: string | null;
};

type Message = {
  id: string; kind: "text" | "event"; date: string | null; fromMe: boolean;
  handle: string | null; senderName: string | null; text: string | null; service: string;
  attachments: { id: string; name: string; mimeType: string | null; size: number; inBackup: boolean }[];
};
```

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
type UpdateStatus = { state: "idle" | "checking" | "upToDate" | "downloading" | "ready" | "available" | "error";
  currentVersion: string; newVersion: string | null; percent: number | null; error: string | null; lastChecked: string | null;
  /* round 4: */ install; channel; architecture; lastResult; retryAt; whatsNew };
```

Round 4 replaced `notInstalled` with `install: "portable"` (the zip, which checks GitHub but can't update itself) and added
`available`; see "Updates you can see (round 4)" at the end.

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

## Drops from File Explorer (round 3, ui)

WinUI hosts WebView2 through composition: its WebView2 control takes no drops and passes none to the browser, so the
page never receives a drop from File Explorer by itself. The host takes the drag and plays it in the page.

- **Where the host takes it.** An OLE drop target (`RegisterDragDrop`) on WinUI's content window
  (`Microsoft.UI.Content.DesktopChildSiteBridge`, the window File Explorer's drag loop finds under the pointer), and
  XAML's drag events on the root grid (`AllowDrop`), in case Windows routes the drag through WinUI's own drag and drop.
  Both read the dragged paths (CF_HDROP / StorageItems) and go through `Desktop/DropIn/DropInForwarder.cs`.
  Data without files (DeviceDeck's own drag out passing over its window) is refused and never reaches the page.
- **Host → page** (`PostWebMessageAsJson`, host-internal like the drag regions, not JSON-RPC):

  ```ts
  { $dd: "nativeDrag", phase: "enter" | "over" | "leave" | "drop", x?: number, y?: number,  // page (CSS) px
    files?: { name: string; path: string; directory: boolean; handle: number }[] }          // on enter and drop
  ```

  On `drop` each file's read-only `FileSystemFileHandle` (`CreateWebFileSystemFileHandle`) goes along as additional
  object number `handle` (folders: -1), so the page gets real `File` objects (a tool can read their bytes).
- **The page** (`web/src/components/nativeDrop.ts`) plays the messages as HTML drag events at (x, y): `dragenter` /
  `dragleave` as the element under the point changes, then `dragover`, and `drop` (or `dragleave` when the drop is
  not taken), so every existing drop zone handles it as in a browser. Files it passes back to the bridge
  (`files.copyToDevice`, `photos.import`, `apps.install`, …) go as their **paths** in `params.files`.
- **Page → host** after each `enter` / `over`: `{ $dd: "nativeDragFeedback", accept: boolean }` — whether the element
  under the pointer takes files (its `dragover` was cancelled and did not set `dropEffect` to `none`). That is the
  effect File Explorer shows (copy or no-drop), and what OLE `Drop` returns.
- **Dev only** (`DEVICEDECK_DEV=1`): `{ $dd: "dropSelfTest", x, y, paths, cancel? }` from the page makes the host call
  its registered OLE drop target (DragEnter, DragOver ×3, Drop or DragLeave) with File Explorer's own data object for
  `paths`; it answers `{ $dd: "dropSelfTest", report }` (the window tree, which window took it, each call's result).
  `DEVICEDECK_DEV_URL` points the dev host at another dev server than `http://localhost:5173/`.
  `npm run test:host` (web/) runs these checks against `DeviceDeck.exe` over DevTools.
## Photos into Photos with LocalSend (round 3)

There is still no supported way to write into an iPhone's Photos library over USB. The free **LocalSend** app
(App Store; Apache-2.0) receives files over its own protocol (v2, MIT; https://github.com/localsend/protocol) and, on
iOS and Android, saves images and videos straight into Photos (its default). DeviceDeck is a LocalSend **sender** only:
it runs no LocalSend server and announces nothing but its own multicast "hello" when asked to look on Wi-Fi.

- **Over the cable first.** usbmux Connect to the phone's port 53317 (`IDevicePortTunnel`, iPhone only) reaches
  LocalSend's server on the phone without Wi-Fi. LocalSend must be open in front: iOS suspends it otherwise.
  Port refused = `notListening` (not open); open but silent = `notAnswering` (in the background).
- **Then Wi-Fi.** A multicast announcement on 224.0.0.167:53317 (answers come back by multicast, since DeviceDeck has
  no server for their `/register`), and `POST /register` to every host of the PC's private /24 networks. Receivers
  are matched to the phone by alias (= the phone's name) or as the only iPhone found (`likely`); the user picks.
- **Certificates.** LocalSend's HTTPS certificate is self-signed: its SHA-256 (hex) is the fingerprint. Discovery takes
  it from the TLS handshake and checks it against the one `/info` (`/register`) states; the job pins it, and a different
  certificate fails with `localsend_fingerprint`. A receiver with encryption off is reached over HTTP.
  **Mutual TLS:** LocalSend (2.2, seen on iOS 27) asks for the sender's certificate in the handshake (TLS 1.3 only) and
  drops a connection without one right after it. DeviceDeck shows its own self-signed certificate, kept per PC in
  `%LocalAppData%\DeviceDeck\localsend-identity.pfx`; DeviceDeck's fingerprint is that certificate's SHA-256 (upper-case
  hex, as LocalSend's), so the phone sees the same sender every time and can keep it as a favourite.
- **The job** (`kind: "localSend"`, through the transfer queue): one item per file (a folder brings its pictures and
  videos). Each run of the job is one LocalSend session: one `/prepare-upload` with every file still to go (one Accept on
  the phone, or none with Quick Save on), then one `/upload` per item; HEIC, Live Photos (still + video) and videos go as
  they are, with their sha256 (files up to 256 MB). A PIN, a decline, a busy phone or no answer ends the session: every
  remaining item fails at once with that code and `localsend.retry` (with the PIN) asks again for what didn't go.
  Pause, cancel and an unplugged phone send `/cancel`; a session left open (the phone was gone) is cancelled before the
  next one. Sends to the same phone take turns. A send left unfinished when DeviceDeck closed is cancelled on the next
  start (never resumed by itself: the phone would ask Accept out of the blue).

### Methods

| Method | Params | Result |
| --- | --- | --- |
| `localsend.discover` | `{ deviceId, wifi?: boolean, rescan?: boolean }` | `LocalSendDiscovery`. Probes the cable each call (a few seconds at most); `wifi: true` starts a Wi-Fi search in the background (about 8 s; at most once a minute, or now with `rescan: true`) — call again to see what it found. |
| `localsend.send` | `{ deviceId, target: LocalSendTarget, files: string[] }` | `{ jobId }` (files from a drop or picker, like `photos.import`) |
| `localsend.retry` | `{ jobId, pin?: string }` | `{}` — runs a failed (or cancelled, or partly failed) send again for the files that didn't go |
| `localsend.job` | `{ jobId }` | `LocalSendJobStatus` with `files` |

`photos.importToApp` (round 2) now answers `{ jobId, folder, parts }`: each send goes into a new dated folder in the
app's Documents (`DeviceDeck 2026-10-21`, then `DeviceDeck 2026-10-21 (2)`), split into sub-folders of 200
(`1-200`, `201-400`, …) when bigger, so Files › On My iPhone › app › folder › Select › Select All › Share › Save N Images
saves them in one go.

### Events

| Event | Params |
| --- | --- |
| `localsend.updated` | `LocalSendJobStatus` without `files`, when a send's phase or error changes (progress stays in `transfer.updated`) |

### Shapes

```ts
type LocalSendRoute = "usb" | "wifi";
type LocalSendTarget = { route: LocalSendRoute; port: number; protocol: "https" | "http"; fingerprint: string | null;
  address?: string | null;  // wifi: the receiver's IP
  alias?: string | null; deviceModel?: string | null };
type LocalSendReceiver = { id: string; route: LocalSendRoute; alias: string; deviceModel: string | null; deviceType: string | null;
  address: string | null; port: number; protocol: "https" | "http"; fingerprint: string; likely: boolean };
type LocalSendDiscovery = { deviceId: string;
  usb: "found" | "notListening" | "notAnswering" | "unavailable"; usbDetail: string | null; usbReceiver: LocalSendReceiver | null;
  wifi: LocalSendReceiver[]; wifiSearching: boolean; wifiSearchedAt: string | null };
type LocalSendPhase = "connecting" | "waitingForAccept" | "sending" | "done" | "failed" | "cancelled";
type LocalSendJobStatus = { jobId: string; deviceId: string; phase: LocalSendPhase; receiverAlias: string | null; pinGiven: boolean;
  error: { code: string; message: string } | null;
  files: { index: number; name: string; size: number; state: "queued" | "sending" | "done" | "failed" | "skipped";
           error: { code: string; message: string } | null }[] | null };
type PhotosToAppStarted = { jobId: string; folder: string; parts: { path: string; count: number }[] };
// TransferKind gains "localSend".
```

Error codes (`TransferJob.error.code`, item errors): `localsend_not_listening` (open LocalSend and keep it open),
`localsend_connection_lost` (dropped or stalled mid-file), `localsend_pin_required`, `localsend_pin_wrong`,
`localsend_rejected` (declined on the phone), `localsend_busy` (409, 429), `localsend_checksum` (422),
`localsend_session_lost` (403 on upload: cancelled on the phone), `localsend_fingerprint`, `localsend_failed` (anything else).
## Photo backup (round 3, backup session)

Additions to "Photos backup and adding photos (round 2)". C#: `src/DeviceDeck.Core/Photos/**`,
`PhotosBackupHandler.cs`, `PhotosBackupMethods.cs`; TypeScript: the photo backup types in `types.ts` and `methods.ts`.

### Date range

`photos.backupConfigure` takes `range?: PhotoBackupRange`; `PhotoBackupOptions` gains `range` and `PhotoBackupCount`
gains `outOfRange`. The range is the phone's own (Settings has none): everything until it is set, and
`useDefaults: true` brings everything back. A photo is in range by the date it was taken; a photo without any date
is always in. An iPhone photo newer than the Photos.sqlite copy has no date in the list; it counts as in range until
the backup asks the phone for its date, and is left out then if it is older.

```ts
type PhotoBackupRange = {
  kind: "all" | "lastDays" | "since";
  days: number | null;      // lastDays: 1–36500 (the panel offers 30)
  since: string | null;     // since: "yyyy-MM-dd", from midnight on this PC
};
// PhotoBackupOptions gains  range: PhotoBackupRange;
// PhotoBackupCount gains    outOfRange: number;   // new, but taken before the range: left out, in no other count
```

A field the kind doesn't use is dropped (`{ kind: "since", since: "2026-09-01", days: 7 }` is stored without `days`);
a missing or bad field is `-32602`. A job queued before ranges existed has no `range` in its request and copies everything.

### Speed

Nothing changes on the wire. A `photosBackup` job copies three photos at once (the queue runs several items of one
job side by side for handlers that ask for it, and saves item states in batches); on an iPhone the originals come over
a pool of up to four AFC connections kept open for the whole job (closed after 20 s idle) instead of a lockdown session
per file, with two FileReads in flight per file (one at a time for the rest of the connection's life if a phone drops a
read ahead). The destination folder is read once per run, the ledger is written in batches, and a HEIC is read once to
be converted. Measured with `tools/PhotoBench --fake` (240 photos, 1.07 GB, a 34 MB/s link): 57.7 s → 32.6 s, 618 → 8
connections.

### Free space

Before any start (the panel's button, the "Back up now" notification, or the automatic start) the bytes to copy are
compared with the free space on the destination's drive. The bytes are the sizes the phone reported, an average of
those for the ones it hasn't said yet, HEIC counted 1.6× when it becomes JPG; the margin is 1 GB or a tenth of that,
whichever is more. `photos.backup` then fails with:

- `no_space` — "Not enough space on E:\ for 312 new photos: about 8.9 GB needed with room to spare, 2.1 GB free. Free
  some space, or choose another folder." (only when something is new);
- `path_not_found` — "The backup folder's drive E:\ isn't there. Connect it, or choose another folder." (an unplugged
  disk, an offline share).

Nothing is queued then. `PhotoBackupStatus` gains `space`, so the panel says it before the button is pressed (with
"Choose another folder"); Settings asks about its default folder:

| Method | Params | Result |
| --- | --- | --- |
| `photos.backupSpace` | `{ folder }` (a full path) | `PhotoBackupSpace` with `neededBytes: 0` |

```ts
type PhotoBackupSpace = {
  root: string;                 // "E:\\" or the share
  freeBytes: number | null;     // null: the drive isn't there, or (a share) can't say
  totalBytes: number | null;
  neededBytes: number;          // the new photos plus the margin; 0 when nothing is new
  enough: boolean;
  nearlyFull: boolean;          // under 20 GB, or under a tenth of the drive, left
  driveMissing: boolean;
};
// PhotoBackupStatus gains  space: PhotoBackupSpace;
```

### When this phone connects

`photos.backupConfigure` takes `onConnect?: "nothing" | "notify" | "backUp"`; `PhotoBackupStatus` gains `onConnect`
(default `"notify"`). It is the phone's own and `useDefaults` leaves it alone. The host's `PhotoBackupWatcher`
(`src/DeviceDeck.Core/Photos/PhotoBackupWatcher.cs`, made by the main window) acts once per connection when the phone
turns `ready`:

- `nothing` — nothing.
- `notify` — when it has new photos, a Windows notification "128 new photos on Nima's iPhone" ("120 photos · 8 videos
  · about 2.3 GB") with **Back up now** (command `photos.backupNow`; the host starts the backup). Only for a phone
  plugged in while DeviceDeck runs, like the "connected" notification. Unplugging takes it back.
- `backUp` — the backup starts by itself, also for a phone that was plugged in when DeviceDeck started and while the
  window is in the notification area; with the phone's range and options, through the transfer queue like any other.
  Nothing new: nothing happens. A destination whose drive isn't there or is too full: no job, and a notification
  "Nima's iPhone's photos weren't backed up" with the reason and **Open DeviceDeck** (`photos.openBackup`; the page
  opens the phone's Back up panel).

A run started by a notification or by the phone ends with a notification: "312 photos backed up" ("From Nima's
iPhone · 1.2 GB") with **Open folder** (`photos.openFolder`, `args.folder`; the host opens only a folder a backup used),
"Photos from … are up to date" when everything new was already in the folder, or "The backup of … stopped" with the
error. A run started from the panel ends as before (the page's toast). Texts follow `settings.language` (English, Persian).

```ts
// PhotoBackupStatus gains  onConnect: "nothing" | "notify" | "backUp";
// PhotoBackupRun gains     trigger: "manual" | "notification" | "automatic";
```

The page follows a click: `photos.openBackup` selects the phone, opens Photos and its Back up panel; `photos.backupNow`
opens the transfers. When a `photosBackup` job appears that the page didn't start, the phone's status is fetched again,
and again when it finishes (the Photos screen's badge).

### Edited iPhone photos

`photos.backupConfigure` takes `saveEdited?: boolean` (iPhone; off by default; one of the phone's own options, so
`useDefaults` turns it off). `PhotoBackupOptions` gains `saveEdited`, `PhotoBackupCount` gains `newEdits`, and
`PhotoBackupRun` gains `edited` and `editsMissing`.

- **Which.** Photos.sqlite's `ZASSET.ZHASADJUSTMENTS` (iOS 14+; `ZGENERICASSET` before) says a photo was edited, and
  `ZADJUSTMENTTIMESTAMP` when; that time is the edit's stamp. A database without these columns reports no edits.
- **Where.** The rendered version is looked for, read-only, in
  `PhotoData/Mutations/<the photo's DCIM folder>/<its name without extension>/Adjustments/FullSizeRender.*` on the
  Media root (an image for a photo, a movie for a video). When the phone has no such file the backup says so
  (`editsMissing`) and doesn't guess; it looks again only after a new edit.
- **On the PC.** Next to the copy of the original, as `IMG_1234 (edited).heic` (`.jpg` when HEIC becomes JPG; the
  render's own extension otherwise), with the photo's date. The original's copy is never touched.
- **A later edit** (a new stamp) makes an item of its own: the edited version replaces the one an earlier backup wrote,
  when that file is still as written (size, and the photo's date as its write time); a file someone changed on the PC is
  kept and the new edit gets a free name (`IMG_1234 (edited) (1).heic`).
- `newEdits` counts new photos' edits and new edits of photos already copied; the panel's button says "Save 3 edited
  versions" when nothing else is new. A Live Photo's edited video is not saved (only the still's render).
- Asset ids of rendered versions (`PhotoData/Mutations/DCIM/…`) are readable through the photo library like DCIM ones;
  nothing else under PhotoData is.

### Already in the folder (after "forget history", or a new device id)

Before a photo is copied, when the destination has a file of its size whose name (without a clash number like ` (2)`)
is the photo's, a sample of the phone's file is compared with it: SHA-256 over the size and the first, middle and last
64 KB (the whole file up to 192 KB), read with AFC FileSeek on an iPhone (`IPhotoRangeReader`, a new optional interface).
A match is remembered as there already (`duplicates`), a Live Photo's video checked the same way next to it; anything
else is copied and compared whole as before. A JPEG made from a HEIC keeps the HEIC's size and sample in the ledger's
hash cache, so it is found again without converting. So a library already on the PC isn't downloaded again; only the
samples are read. A photo whose edited version is to be saved is copied as before. Android has no part reads yet: it
copies and compares whole, as before.

### Leftover temp files

The backup's temp files have names nothing else uses: `<name>.original.ddpart`, `.motion.ddpart`, `.edited.ddpart`
and `.backup.ddpart` (a download always starts over, so none is ever resumed from). When the app starts and after
every run, those in the folders backups went to are removed, but only where no backup is queued, running, paused or
waiting for that folder, and only when not written in the last minute. Nothing else is touched: not another copy's
`.ddpart`, not a file outside the backup folders. The host logs what went and how much space it freed.

## Updates you can see (round 4, updates)

Added by the updates session. C#: `src/DeviceDeck.Bridge/IDesktopShell.cs` (the update block), `src/DeviceDeck.Bridge/ReleaseNotes.cs`,
the update block of `DesktopMethods.cs`, `src/DeviceDeck.Shell/Desktop/Update*.cs`; TypeScript: `web/src/bridge/releaseNotes.ts`, the
`UpdateStatus` block of `types.ts`, the round-4 block of `methods.ts`; the page's part is `web/src/shell/Update*`, `WhatsNewDialog.tsx`,
`ReleaseNotesView.tsx`, `releaseNotes.ts`, `updates.ts`.

### Methods

| Method | Params | Result |
| --- | --- | --- |
| `app.releaseNotes` | `{ version }` (`0.4.0`, `0.4.0-beta.1`; no `v`) | `ReleaseNotes` |
| `app.openReleasePage` | `{ version? }` | `{}`: opens `https://github.com/sadrazkh/DeviceDeck/releases/tag/v{version}` (or `…/releases/latest`) in the browser; nothing else is ever opened |
| `app.whatsNewShown` | `{ version }` | `{}`: "What's new" for that version was shown; `whatsNew` goes back to null and isn't offered by itself again |

A version that isn't `MAJOR.MINOR.PATCH[-prerelease]` is refused with invalid params. Outside the window: `not_supported`.

### Shapes

```ts
type UpdateStatus = {
  state: "idle" | "checking" | "upToDate" | "downloading" | "ready" | "available" | "error";
  currentVersion: string;
  newVersion: string | null;        // what the last check found (ready: downloaded; available: on GitHub)
  percent: number | null;           // while downloading
  error: string | null;             // one English sentence for the log; the page shows its own words
  lastChecked: string | null;       // also from an earlier run
  install: "setup" | "portable";    // setup updates itself (Velopack); portable is the zip: it only checks GitHub
  channel: "stable" | "prerelease"; // prerelease when the version has a "-"
  architecture: "x64" | "arm64";
  lastResult: "upToDate" | "newVersion" | "error" | null;
  retryAt: string | null;           // with error: GitHub asked to wait until then (its rate limit)
  whatsNew: string | null;          // the first run of a newer version: show "What's new in v{whatsNew}" once
};
type ReleaseNotes = {
  version: string; url: string;     // url: the version's GitHub release page
  title: string | null;             // "DeviceDeck v0.4.0"
  markdown: string | null;          // as on GitHub; null when none could be had (show a link to url)
  published: string | null;
  source: "github" | "cache" | "app" | "none";
};
```

### How it behaves

- **Setup copy** (`install: "setup"`): as in round 2 (Velopack; a minute after start, then every 6 hours while `autoUpdate` is on;
  `downloading` → `ready`; `app.applyUpdate` restarts into it). A Windows notification once per version.
- **Portable zip** (`install: "portable"`): a minute after start and then every 12 hours while `autoUpdate` is on, and on Check now,
  it asks `GET https://api.github.com/repos/sadrazkh/DeviceDeck/releases/latest` (with prereleases: `/releases?per_page=10`, the
  newest), unauthenticated. A newer version → `available` (also at the next start, from the last check), with a notification once
  per version whose Download button opens the release page. Nothing is downloaded.
- **GitHub's API** (`updates/github.json` in the data folder): every answer is kept with its ETag and asked for again with
  `If-None-Match` (an unchanged release is a 304). When GitHub says the limit is used up (`X-RateLimit-Remaining: 0` with
  `X-RateLimit-Reset`, or a 403/429 with `Retry-After`; a minute when it says neither) nothing is asked until then, also after a
  restart. An automatic check that would have to wait changes nothing; Check now answers `error` with `retryAt`. A failed check
  keeps a version already found (`available` stays, `lastResult: "error"`).
- **Release notes** for a version: the tag's release from GitHub (cached as above); when GitHub can't be reached, the cached copy
  (`cache`); with none, the notes that came with this build (`docs/releases/v{version}.md`, embedded) or with the downloaded update
  (Velopack's package notes) (`app`); else `none` and the page shows a link to `url`.
- **What's new once after an update**: `updates/state.json` remembers the version that ran last. The first run of a newer one sets
  `whatsNew` (an update from v0.3.0, which kept no history, counts when the data folder was already there; a first install doesn't).
  The page shows the dialog and calls `app.whatsNewShown`.
- **Environment** (the Shell): `DEVICEDECK_UPDATE_FEED=off` (no automatic checks) or a URL used instead of the API's repository
  address (a test server; checks then run in a dev build too); `DEVICEDECK_WHATSNEW=1` shows What's new now, `0` never;
  `DEVICEDECK_UPDATE_SOURCE` a folder or address with `vpk pack`'s output that a Setup copy updates from instead of GitHub Releases
  (to try a whole update on a test install). A dev build (`DEVICEDECK_DEV`) never checks or opens What's new by itself.

### The page

- Title bar: "Update ready" (`ready`) or "v0.4.0 on GitHub" (`available`) opens What's new; "Downloading 42%" while it downloads;
  nothing otherwise. The in-app banner (in the toast stack) shows once per version (remembered in the page's storage).
- What's new: the Persian `<details>` block when the UI is Persian (right-to-left), the English part otherwise. Only a markdown
  subset becomes elements (headings, paragraphs, lists with one level of nesting, quotes, code, rules, bold, italics, inline code,
  `https://github.com` links); raw HTML is dropped (script-like elements with their contents), images are dropped, other links
  keep their words. Nothing is given to the page as HTML.
- A click on the update's notification itself (not its button) opens What's new.
- Mock: `?update=checking|downloading|downloadThenReady|ready|error|rateLimited|upToDate|portable|portableNew|whatsNew` and
  `?notes=english|hostile|missing|cache` (`web/src/bridge/mock/updates.ts`).
## WhatsApp (round 4, whatsapp session)

WhatsApp's chats on an iPhone are read from a backup on this PC (DeviceDeck's, iTunes' or Apple Devices'; encrypted
ones once `backup.unlock`ed), through the same sessions as contacts and messages. WhatsApp keeps them in its app
group: `AppDomainGroup-group.net.whatsapp.WhatsApp.shared/ChatStorage.sqlite` (WhatsApp Business:
`…group.net.whatsapp.WhatsAppSMB.shared`), media under `Message/Media/…`. On Android the chats can't be opened
(`Databases/msgstore.db.crypt14`/`.crypt15` are encrypted with a key only the phone has); DeviceDeck copies WhatsApp's
folders to the PC instead, for WhatsApp itself to restore.

| Method | Params | Result |
| --- | --- | --- |
| `whatsapp.status` | `{ backupId }` | `WhatsAppStatus` |
| `whatsapp.chats` | `{ backupId }` | `WhatsAppChat[]` (both apps, newest first) |
| `whatsapp.messages` | `{ backupId, chatId, offset, limit }` | `Page<WhatsAppMessage>` (oldest first; limit 1–1000; `total` = `chat.messageCount`) |
| `whatsapp.thumbnail` | `{ backupId, mediaId, size? }` | `{ dataUrl: string \| null }` (WhatsApp's own thumbnail, else the photo or sticker made small; size 64–1024, default 256) |
| `whatsapp.media` | `{ backupId, mediaId }` | `{ dataUrl, mimeType, size }` (a voice note to play; files over 32 MB are `not_supported`: export them) |
| `whatsapp.export` | `{ backupId, format: "html" \| "pdf", destination, chatIds? }` | `BackupJob` (kind `export`; no `chatIds` = every chat) |
| `whatsapp.exportMedia` | `{ backupId, destination, chatIds? }` | `BackupJob` (kind `export`) |
| `whatsapp.android` | `{ deviceId }` | `WhatsAppAndroidInfo` (Android only, else `not_supported`) |
| `whatsapp.backupAndroid` | `{ deviceId, destination }` | `{ jobId, folder }` (a transfer job of kind `whatsAppMedia`) |

- **Exports** run as backup jobs (`backup.updated`, Backup screen). One chat writes `<chat>.html` (or `.pdf`) and its
  media into `<chat> files` right in `destination`, and `BackupJob.destination` is that page. Several chats go into
  `destination\WhatsApp - <phone> - <backup date>` (that folder is `destination`), one page per chat. Media files
  keep WhatsApp's names and get their message's date. `exportMedia` writes
  `destination\WhatsApp media - <phone> - <backup date>\<chat>\…`, one folder per chat, only files the backup holds.
  Files listed but not in the backup end the job `done` with a `backup_missing` warning in `error`.
- **Android copy**: `whatsapp.backupAndroid` picks the dated folder `destination\WhatsApp - <phone> - yyyy-MM-dd`
  (`… (1)` when taken) and queues one transfer job that copies `Media`, `Databases` and `Backups` of
  `Android/media/com.whatsapp/WhatsApp` (and `com.whatsapp.w4b/WhatsApp Business`; `/WhatsApp` and
  `/WhatsApp Business` at the top of the storage for versions before 2021) into `<folder>\WhatsApp\…` and
  `<folder>\WhatsApp Business\…`. Nothing on the phone is changed. No WhatsApp folder: the job fails `path_not_found`.
- **Not in the backup**: `whatsapp.status` says `found: false` (and `installed`, from the backup's app list and
  domains); `whatsapp.chats` is `[]`. An Android backup is `not_supported`. A database whose tables DeviceDeck doesn't
  know is `backup_corrupt`.
- **Other WhatsApp versions**: every table and column is looked up first. One that isn't there reads as empty and is
  named in `status.missing` (`"ZWAMESSAGE.ZSTARRED"`, `"ZWAGROUPMEMBER"`), so the page can say some details are
  missing instead of failing. A `ChatStorage.sqlite-wal` next to the database is folded in, in memory.

```ts
type WhatsAppAccount = "whatsApp" | "business";
type WhatsAppStatus = { backupId: string; found: boolean; installed: boolean; accounts: WhatsAppAccountInfo[]; missing: string[] };
type WhatsAppAccountInfo = { account: WhatsAppAccount; name: string; chats: number; messages: number;
  media: number; mediaInBackup: number; mediaBytes: number };
type WhatsAppChat = {
  id: string;                     // "w:<row>" or "b:<row>" (Business)
  account: WhatsAppAccount; title: string;
  jid: string | null;             // "989121234567@s.whatsapp.net", "…@g.us"
  phone: string | null;           // "+989121234567", one-to-one chats
  isGroup: boolean; messageCount: number; mediaCount: number; lastMessageAt: string | null;
  preview: string | null; previewMedia: WhatsAppMediaKind | null; archived: boolean;
};
type WhatsAppMessageKind = "text" | "media" | "event" | "deleted" | "other";   // other: a poll, a call, something newer
type WhatsAppMessage = {
  id: string; kind: WhatsAppMessageKind; date: string | null; fromMe: boolean;
  senderJid: string | null; senderName: string | null;   // groups only
  text: string | null;                                     // or a photo's caption
  starred: boolean; quote: WhatsAppQuote | null; media: WhatsAppMedia | null;
};
type WhatsAppQuote = { messageId: string | null; fromMe: boolean; senderName: string | null; text: string | null; mediaKind: WhatsAppMediaKind | null };
type WhatsAppMediaKind = "image" | "video" | "audio" | "voice" | "document" | "sticker" | "gif" | "location" | "contact" | "other";
type WhatsAppMedia = {
  id: string;                     // for whatsapp.thumbnail / whatsapp.media
  kind: WhatsAppMediaKind; name: string; mimeType: string | null; size: number; durationSeconds: number | null;
  inBackup: boolean; hasThumbnail: boolean; latitude: number | null; longitude: number | null;
};
type WhatsAppAndroidInfo = { deviceId: string; apps: WhatsAppAndroidApp[] };
type WhatsAppAndroidApp = {
  package: "com.whatsapp" | "com.whatsapp.w4b"; account: WhatsAppAccount; name: string;
  folder: string; hasFolder: boolean; installed: boolean | null;
  mediaFiles: number | null; mediaBytes: number | null;
  chatBackups: { name: string; size: number; modified: string | null }[];   // msgstore*.crypt14/15, newest first
};
```

Also in this round: `SqliteImage.FromBytes` opens WAL-mode database images (a phone's databases are in WAL mode;
before, an encrypted backup's databases failed with "unable to open database file").
## Deleting photos (round 4, photodelete)

An iPhone's photos can now be deleted from the PC the way File Explorer does: through Windows' camera connection
(Windows Portable Devices over PTP, This PC › Apple iPhone › Internal Storage › DCIM), so the phone's Photos library
removes them itself. Never over AFC: removing DCIM files behind Photos' back leaves Photos.sqlite pointing at missing
files. An iPhone reports `photosDelete` only while Windows lists it as a camera (by its UDID) and its camera roll says
its files can be deleted (`WPD_OBJECT_CAN_DELETE`); `photos.deleteSupport` says why not otherwise. Android: as before
(MediaStore), now with a result per item too.

| Method | Params | Result |
| --- | --- | --- |
| `photos.deleteSupport` | `{ deviceId, recheck?: boolean }` | `PhotoDeleteSupport` — `recheck` asks the phone again (a few seconds over USB) |
| `photos.deleteItems` | `{ deviceId, items: { id, size }[], expectedBytes, copyFirstTo? }` | `PhotoDeleteStarted` since v0.4.1, at once (see "Deleting photos as a transfer" at the end) |

- Exactly the items sent, each at the size the page showed, `expectedBytes` their total (`-32602` otherwise). Every item
  is matched first; if one doesn't match exactly (gone, changed, iCloud only, ambiguous), nothing is deleted and the
  others say `notTried`. Then batches of five; the delete stops after the first batch with a failure.
- iPhone matching: a DCIM path maps to the object Windows lists by folder + name + size when Windows shows the phone's
  own folders (100APPLE), and by name + size when it shows month folders (202510_a, recent iOS) — then only when the
  name is unique both on the phone and in Windows' list. A Live Photo's video goes in the same call as its still.
  Only files under the camera roll's folders are ever deleted; never a folder, never recursively.
- `copyFirstTo`: the photos are copied through the queue first (`photos.export`, plus, on an iPhone, a `files.copyToPc`
  of each Live Photo's video into the same folder as its still); nothing is deleted unless every copy finished.
- One line per deleted object (DCIM path and size, never content) in `logs\photo-deletes-yyyyMMdd.log`.
- `cleanup.deleteMedia` now works on an iPhone with `photosDelete` too, through the same per-item delete (since v0.4.1
  as a job, like `photos.deleteItems`; `CleanupResult` keeps no per-item fields).

```ts
type PhotoDeleteBlock = "none" | "checking" | "iCloudPhotos" | "readOnly" | "locked" | "notCamera" | "converted" | "unsupported";
type PhotoDeleteDestination = "unknown" | "recentlyDeleted" | "permanent";
type PhotoDeleteSupport = {
  canDelete: boolean; reason: PhotoDeleteBlock;
  detail: string | null;          // one technical sentence of what the check saw (for a details fold)
  goesTo: PhotoDeleteDestination; // iPhone: "permanent" as users report, until a delete on this phone shows otherwise
  observed: boolean;              // goesTo comes from a delete on this phone in this session
  checkedAt: string | null; iCloudPhotos: boolean | null;   // a delete then reaches iCloud and the other devices
};
type PhotoDeleteResult = {
  id: string; fileName: string; size: number;   // size: what it freed (a Live Photo's video included)
  outcome: "deleted" | "mismatch" | "failed" | "notTried";
  code: string | null; message: string | null;  // why not, or a note (its edit, its video)
  also: string[];                               // files that went with it ("IMG_0001.MOV")
  wentTo: PhotoDeleteDestination;               // seen on the phone afterwards: file gone, or kept and marked deleted
};
type PhotoDeleteReport = { items: PhotoDeleteResult[]; deleted: number; freedBytes: number; copyJobIds: string[];
                           copyIncomplete: boolean; goesTo: PhotoDeleteDestination };
```

Reasons: `iCloudPhotos` / `readOnly` (the phone marks its photos read-only for the PC; the first when the library shows
iCloud Photos), `locked` (Windows shows no storage, or an empty one, until the phone is unlocked and trusted),
`notCamera` (Windows doesn't list it), `converted` (Settings › Photos › Transfer to Mac or PC is Automatic, so Windows sees
JPGs, not the phone's files), `unsupported` (no camera connection on this PC or this phone), `checking`.

## Deleting photos as a transfer (v0.4.1, deletejob)

A confirmed delete, from Photos or from Clean-up, is a transfer job (`kind: "photosDelete"`): the confirmation dialog
closes as soon as it is queued, a toast says "Deleting in the background", and the Transfers drawer shows the job with
its stage and a bar. Every rule of the delete itself is unchanged (exact match or nothing goes, batches of five, stop at
the first failure, never folders, one log line per object; the confirmation still says "deleted for good").

| Method | Params | Result |
| --- | --- | --- |
| `photos.deleteItems` | `{ deviceId, items: { id, size }[], expectedBytes, copyFirstTo? }` | `PhotoDeleteStarted` at once. Bad params `-32602`; a phone that can't delete `not_supported` (no job then) |
| `cleanup.deleteMedia` | as before | `PhotoDeleteStarted` at once. Since v0.6 the job itself leaves alone what changed since the analysis (results `mismatch`, code `changed`, counted in the job's `skipped`): `skipped` here is 0 and `jobId` never null |
| `photos.deleteJob` | `{ jobId }` | `PhotoDeleteJobStatus`: the results so far, all of them once the job ended; `-32602` for another kind of job |

```ts
type TransferJob = { …; stage?: TransferStage | null };   // every job: null unless it runs in stages
type TransferStage = { name: "copying" | "reading" | "deleting" | "checking"; done: number; total: number };  // 0 of 0: not known yet
type PhotoDeleteStarted = { jobId: string | null; skipped: number };
type PhotoDeleteJobStatus = { jobId: string; deviceId: string; source: "photos" | "cleanup";
  state: TransferState; stage: TransferStage | null;
  items: PhotoDeleteResult[];          // one per confirmed item, in order; not reached yet: notTried
  deleted: number; freedBytes: number; goesTo: PhotoDeleteDestination;
  copyJobIds: string[]; copyIncomplete: boolean; skipped: number; error: { code: string; message: string } | null };
// TransferKind gains "photosDelete".
```

- **Stages.** `copying` (copy to this PC first: the copy jobs' items done of all; the copies are jobs of their own
  beside it), `reading` (the phone's library, then on an iPhone Windows' view of its camera roll: objects read of the
  objects to read), `deleting` (items done of all, a batch at a time), `checking` (iPhone: what the phone shows
  afterwards). Then `done`, or `failed` when nothing was deleted (the error says why), or `cancelled`.
- **The job's numbers.** `totalItems` is the items confirmed, `doneItems` the deleted ones, `failedItems` the ones not
  deleted (refused, or left alone because they didn't match); the rest weren't tried. `doneBytes` is what the deleted
  ones freed (a Live Photo's video included). The title bar's ring leaves deletes out of its bytes.
- **Cancel** (`transfers.cancel`): while copying or reading, nothing has been deleted and nothing will be (unfinished
  copy jobs are cancelled with it); while deleting, the batch on its way finishes and no other starts; the results say
  which went. Checking only reads and always finishes (the drawer offers no Stop then).
- **Never by itself.** A delete job can't be paused, resumed or retried (`transfers.pause` / `resume` / `retry` answer an
  error): deleting what is left needs a new selection and a new confirmation. It never waits for its phone: a phone that
  goes away (or isn't there when its turn comes) fails it, and it doesn't start when the phone is back.
- **A restart in the middle.** A delete job DeviceDeck closed in the middle of (or crashed in) is ended on the next
  start, before anything runs: state `cancelled`, `error.code` `interrupted`, the message "N deleted, M not tried"
  (and "K may be gone" for a batch that was on its way: those items say `failed` / `interrupted`; look on the phone).
  One that hadn't started yet ends the same way ("before anything was deleted"). Nothing is ever deleted on a restart.
  Results after a restart come from the saved items (`photos.deleteJob`), without the notes only the run knew.
- **One at a time per phone.** Delete jobs take turns on a phone, in a lane of their own beside the two copy workers
  (a delete may wait for its own copies). A queued one shows "Waiting for the delete before it".
- **When it ends** the page reads the photo lists, albums, storage and the backup status again; Clean-up's report drops
  what went (whichever screen started the delete). A toast says what went, with **Results**.

Reading Windows' view fast (iPhone): objects are read in bulk (`IPortableDevicePropertiesBulk::QueueGetValuesByObjectList`,
200 at a time) where the driver does it, one by one where it doesn't, and one by one for any object a bulk answer left
out. Only the folders that can hold the items are read: the phone's own folder when Windows shows it (100APPLE), else
the month folder of each item's date taken (`YYYYMM_x` or `YYYY__MM`; two months at a month's edge, for any time zone,
UTC−12…+14), plus any folder not named after a month. A still that isn't found there (Windows filed it under another
month, or the listing was out of date) makes the delete read every folder, fresh, and match again. The listing is kept
per phone for 5 minutes: dropped when the phone locks or goes away and after a delete that didn't go cleanly; after a
clean delete only the deleted objects leave it. Objects a delete takes from a kept listing are read again before
anything is deleted (anything changed: read fresh); every object is still read again just before its batch.
Matching is unchanged, with one difference by design: Windows' side of "the name is unique" is checked across the
folders read (the phone's side, every DCIM file over AFC, is checked as before).
## Hidden photos in the photo backup (round 4, hiddenbackup)

Added by the hiddenbackup session. Replaces round 2's `includeHidden` switch. C#: `src/DeviceDeck.Core/Photos/PhotoBackupHidden.cs`
(the rules), the photo backup files, `AppSettings` (a block at the end); TypeScript: `web/src/bridge/hiddenBackup.ts`,
`web/src/screens/photos/HiddenBackupOptions.tsx`.

An iPhone's Hidden album can be left out (the default), backed up as well as the other photos, or backed up alone; when
it is backed up, its photos go into a folder of their own under the destination, dated inside the same way, or into the
same date folders as the others. Every combination:

| `hidden` | `hiddenLayout` | Normal photos | Hidden photos |
| --- | --- | --- | --- |
| `exclude` (default) | (any) | `<destination>\2026\10\IMG_1234.HEIC` | left out (`hiddenLeftOut`) |
| `include` | `separate` (default) | `<destination>\2026\10\…` | `<destination>\Hidden\2026\10\…`, `<destination>\Hidden\Undated\…` |
| `include` | `together` | `<destination>\2026\10\…` | `<destination>\2026\10\…` (as `includeHidden: true` did) |
| `only` | `separate` | left out (`normalLeftOut`) | `<destination>\Hidden\2026\10\…` |
| `only` | `together` | left out (`normalLeftOut`) | `<destination>\2026\10\…` |

With `folderPattern: ""` the hidden ones go straight into `<destination>\Hidden\`. A Live Photo's video and a photo's
edited version always go next to the photo, so into the hidden tree with it.

```ts
type PhotoBackupHidden = "exclude" | "include" | "only";
type PhotoBackupHiddenLayout = "separate" | "together";
// PhotoBackupOptions: includeHidden is gone; it gains
//   hidden: PhotoBackupHidden; hiddenLayout: PhotoBackupHiddenLayout; hiddenFolder: string;   // "Hidden"
// PhotoBackupCount gains
//   newHidden: number;       // how many of newCount are hidden (0 unless included)
//   normalLeftOut: number;   // new photos that aren't hidden, left out by hidden: "only"
// photos.backupConfigure takes hidden?, hiddenLayout?, hiddenFolder?; includeHidden? is still taken
//   (true = include + together, false = exclude; hidden wins when both are sent).
// Settings gains (the defaults of an iPhone without its own; useDefaults brings them back):
//   photoBackupHidden: PhotoBackupHidden;              // "exclude"
//   photoBackupHiddenLayout: PhotoBackupHiddenLayout;  // "separate"
//   photoBackupHiddenFolder: string;                   // "Hidden"; the page suggests "پنهان" in Persian
```

- **Per phone, in the ledger**, like the other options: `devices.hidden`, `hidden_layout`, `hidden_folder` in
  `photo-backup.db` (schema 3). A phone that isn't an iPhone always backs up as `exclude` (Android has no Hidden album).
- **Migration.** Opening a schema 2 file turns `include_hidden = 1` into `include` + `together` (existing users keep
  their behaviour: hidden photos mixed in) and `include_hidden = 0` into `exclude`. `include_hidden` is still written
  (on for `include` and `only`) for an older copy of the app. A job queued before this has no `hidden` in its request:
  its `includeHidden` decides, hidden ones mixed in.
- **The folder name** is one folder name: trimmed; 1–64 characters; no `\ / : * ? " < > |`, control characters or
  direction marks (ZWNJ is fine); not `.`/`..` and no trailing dot; not a device name (`CON`, `NUL`, `COM1`, …, also
  with an extension); not `Undated` and not only digits and the pattern's separators (it would sit among the year
  folders). Anything else is `-32602`, from `photos.backupConfigure` and from `settings.set`.
- **Incremental across changes.** A photo is placed once, when it is first copied. Hidden or unhidden on the phone
  after its backup, it is not copied again and nothing on the PC moves: its copy stays in the tree it went to (a later
  edit of it goes next to that copy). Switching `together` ↔ `separate` (or renaming the folder) leaves what was copied
  where it is; only new photos follow the new choice. The panel says so in one line.
- **Duplicates across both trees.** The destination index covers the whole destination, the hidden folder included:
  a photo already in the other tree (same size and SHA-256, or the name + sample check) is remembered there, not copied
  again; no empty folder is made for it.
- **Counts follow the options**: `newCount` (and the Photos screen's badge) counts hidden photos only when they are
  backed up; the free-space check and the `newBytes` estimate take only what will be copied; the automatic start on
  connect and the "Back up now" notification use the same options. The notifications never say how many are hidden.
- **The page.** The Back up panel (iPhone) shows "Normal and hidden photos" (Normal only · Both · Hidden only), then
  "Hidden photos go" (In a folder of their own · With the other photos), the folder's name with a suggestion ("Hidden",
  or "پنهان" in Persian), an example path (`E:\Backup\Sadal\Hidden\2026\10\IMG_1234.HEIC`, each folder bidi-isolated),
  the one-line note above when the phone has backups, and a privacy note: hidden photos are copied as ordinary files,
  so anyone who can open the folder on the PC can see them. How many of the new photos are hidden is shown only once the
  Hidden album is revealed on the Photos screen. Settings › Photo backup has the same choices as the defaults.
- **PhotoBench**: `--hidden exclude|separate|together|only`, `--hidden-layout separate|together` (for `only`),
  `--hidden-folder <name>`; it prints where the files went.

### Reading ahead (v0.4.2, deletejob2)

| Method | Params | Result |
| --- | --- | --- |
| `photos.deletePrepare` | `{ deviceId, reason? }` | `{}` at once, always (also for a phone that can't delete, or an unknown id) |
| `photos.deleteReach` | `{ deviceId }` | `PhotoDeleteReach`: which items this PC can't delete (below) |

An iPhone that can delete reads Windows' view of its whole camera roll ahead, in the background: read-only, on a
low-priority thread, cancelable, into the per-phone listing a delete uses. It starts when Clean-up's analysis finishes,
and when the page calls `photos.deletePrepare`: Photos calls it as soon as something is selected and when the delete
dialog opens. It doesn't start while a delete runs, while one runs already, or while the kept listing is complete and
less than 2½ minutes old. A delete stops it and carries on from what it read (its "reading" count starts there).
Locking or unplugging the phone stops it and drops what it read. It is no transfer (nothing in the drawer).

Measured on the owner's iPhone 16 (iOS 27, read-only probes `wpd-dates`, `wpd-dryrun`):
- The first read of an object takes ~27-47 ms. Bulk reads are no faster: 48 ms an object in bulk, 47 ms one by one.
  Windows' driver then keeps what it read until the phone is unplugged, and that holds across connections and
  processes: every object again in 0.1 s. Whole roll: 3,217 objects in 86 s the first time.
- Delete → Deleting for 39 items: about 0.3 s once the roll was read ahead. Without a read ahead, at that speed:
  ~95 s for 39 items spread over 11 months, or ~150 s when a still is missing and every folder is read.
- The month folders (`YYYYMM_a`, and `_b` beside some) follow the date taken. All 3,217 objects' own creation dates
  are in their folder's month, and so are all 245 sampled objects matched to a phone file by name. Photos.sqlite's
  ZDATECREATED and ZADDEDDATE agreed for every one of those. ZMODIFICATIONDATE agreed for only 87%.
  `_b` held two items of a month that also has `_a`; its meaning isn't known, so every folder of the month is read.
- Windows lists only 2,015 of the phone's 4,376 DCIM files by name. Nothing added before August 2025 (1,509 assets,
  moved over from an earlier iPhone) is in its view, and that includes 38 of the 39 largest videos. Those can't be
  deleted from a PC. The delete now says so once it has read every folder: "Windows doesn't list … anywhere in the
  iPhone's camera roll, so this PC can't delete it. Delete it in the Photos app on the iPhone."

### Items only the iPhone can delete (v0.4.2, deletejob2)

`photos.deleteReach` answers from the read ahead above, so the page can mark these items before anyone selects them:

```ts
interface PhotoDeleteReach {
  deviceId: string;
  state: 'unsupported' | 'checking' | 'ready';
  done: number;          // objects read of all, while checking (0 of 0 before the folders are listed)
  total: number;
  notListed: string[];   // ready: the asset ids Windows lists nowhere
  checkedAt: string | null;
}
```

- **Never a guess.** `ready` only from a complete listing of Windows' view (the kept listing, read through, less than
  5 minutes old). Until then `checking` with how far the read got, and `notListed` is empty. Asking while no read
  ahead runs starts one, so the page asks only where a delete is near: Clean-up's results, a selection on Photos,
  the delete's confirmation. It polls once a second while `checking`.
- **What is listed:** assets on the phone (not iCloud-only, not empty) that the same mapping a delete uses finds in no
  folder at all. An item it can't tell (a name used twice, no Photos.sqlite size) is never listed. Worked out once
  per listing.
- While a delete runs, the last answer stands. A delete that read every folder leaves a complete listing too.
- `unsupported` for a phone that can't delete from the PC (or isn't ready). Android with `photosDelete`: `ready` with
  nothing listed (MediaStore deletes what it shows).

The page: Clean-up marks each such item "Delete on the iPhone" (why in a tooltip) and adds "N of M can only be deleted
on the iPhone" to the section's summary; the checkbox stays, so the item can still be copied. Delete acts on the
others; the confirmation lists the rest apart ("can't be deleted from this PC — delete them in the Photos app"); with
only such items selected there is no Delete, the reason and Copy instead. Photos does the same from the selection:
the confirmation lists them apart and deletes the rest; with nothing else selected, Delete is off with the reason.
While checking, a quiet "Checking which items this PC can delete…" with a bar. Mock: `?reach=some` (every fourth
iPhone item), with `?deletespeed=slow` for a read ahead of about 15 s.

## Every delete as a transfer (v0.6)

Files and folders (Files, App files, Clean-up) and apps (Apps, Clean-up) are deleted the way photos are: the call answers
with a job at once, the confirmation closes, and the Transfers drawer shows the job's stage with a bar, Stop, and
**Results**. Starting a delete reads nothing from the phone, photos included (`photos.deleteItems` items may carry
`name`, the file name the page showed): on the owner's iPhone, reading the library first kept the dialog open 12 s.

| Method | Params | Result |
| --- | --- | --- |
| `files.deleteItems` | `{ deviceId, items: FileDeleteItem[], expectedBytes }` | `DeleteStarted`. Bad params `-32602` (empty, a repeat, a wrong total, a whole shared folder `"/"`); a root the phone doesn't share `not_supported` |
| `apps.uninstallItems` | `{ deviceId, items: AppUninstallItem[], expectedBytes, copyFirstTo? }` | `DeleteStarted`. `not_supported` when the phone can't remove apps (or can't copy them first) |
| `transfers.items` | `{ jobId }` | `TransferItemView[]`: any job's items as saved, in order; for a delete, its result per item |

```ts
type FileDeleteItem = { root: string; path: string; size: number; isDirectory?: boolean };  // a folder: what it holds, or 0
type AppUninstallItem = { id: string; size: number; name?: string | null };                 // size: appBytes + dataBytes
type DeleteStarted = { jobId: string };
type TransferItemView = { index: number; name: string; source: string; size: number;
  state: "queued" | "running" | "done" | "failed" | "skipped"; code: string | null; message: string | null };
// TransferKind gains "filesDelete" and "appsUninstall". TransferJob gains skippedItems (a delete's items left alone
// because they changed since the user chose them; not in doneItems).
```

- **Stages.** `reading` (Clean-up only: what changed since the analysis; those items end `skipped`, code `changed`),
  `copying` (copy first: CopyToPc per root, or AppExport per app; nothing goes unless every copy finished), then
  `deleting` (files) or `removing` (apps): items done of the items kept, one at a time. A folder goes with everything
  in it; a file still listed afterwards failed.
- **Just before each item** goes, a file is looked at again (jobs take turns, so it may have changed since Delete): one
  of another size is left alone (`skipped`, code `changed`), one that is gone too (code `missing`; an app no longer
  installed the same). Clean-up's report drops what went and what is gone; only what went counts as freed.
- **An item the phone refuses** fails (`permission_denied`, …) and the job goes on with the next;
  the job fails only when nothing went (its error: "Nothing was deleted. name: why"). A phone that goes away stops it;
  the item on its way may or may not have gone (`failed` / `interrupted`).
- **Cancel** stops before the next item: the one on its way finishes. While copying, nothing has gone and the copies
  are cancelled. The rest stay `queued` with "Not tried: the delete was stopped."
- **Never by itself**, one at a time per phone, and a restart in the middle: as for photos (above).
- **When it ends** the page reads the lists again (files, app files, apps, app folders, storage); Clean-up's report drops
  what went, whichever screen deleted it (a folder takes the listed files inside it along).
- `files.delete`, `apps.uninstall` and `photos.delete` stay for older callers; the page no longer uses them.

## Drivers card (v0.6, drivers)

v0.5.3 showed "Android tools · Stopped" while DeviceDeck's adb server ran: the page asked `drivers.status` once, while
the hub was still starting the providers (`adb start-server` takes a second or more), and the hub's 15 s poll compared
its answers only with each other, so its first answer ("ready") became the baseline and was never published. Now:

- The hub remembers the statuses it last gave anyone (a `drivers.status` answer or a `drivers.changed`) and publishes
  `drivers.changed` whenever a later check differs. It checks every 15 s, after each provider has started, when a phone
  comes, changes or goes, and when a provider says its driver changed (`IDriverControl.DriverStatusChanged`: adb's
  tracker started or lost the server, tools were installed, Apple Mobile Device Service came or went).
- The page also asks again every 30 s while the card is on screen, on `device.added` / `device.removed`, and on
  **Check again**; a `drivers.changed` cancels a `drivers.status` still on its way, so an older answer can't overwrite it.

`DriverStatus` gains two states and three fields, each left out of the JSON when null:

```ts
type DriverState = 'ready' | 'missing' | 'stopped' | 'error' | 'starting' | 'idle';
interface DriverStatus {
  providerId: string; state: DriverState; version: string | null; detail: string | null;
  reason?: 'amdsMissing' | 'amdsStopped' | 'amdsError' | 'adbMissing' | 'adbStarting' | 'adbNotStarted' | 'adbFailed'
    | 'startFailed' | 'checkFailed';
  source?: 'bundled' | 'downloaded' | 'environment' | 'configured';   // where adb.exe came from
  phones?: { name: string; problem: 'usbDebuggingOff' | 'driverMissing' | 'unauthorized' | 'offline' | 'notSeenByAdb';
             maker: string | null }[];
}
```

- `starting`: DeviceDeck is starting the driver now (adb's server, also again after it went away). `idle`: not running
  yet and nothing is wrong (the provider hasn't started). Both are grey on the card, never red. Android no longer
  answers `stopped`; the iPhone's `stopped` means Apple Mobile Device Service is installed but not running.
- `phones`: Android phones Windows' USB list (cfgmgr32, read only) or adb's list shows that can't be used yet. Apple's
  devices and non-phone USB devices are never listed; `notSeenByAdb` only after 10 s and only while adb lists no USB
  phone at all.

| Method | Params | Result |
| --- | --- | --- |
| `drivers.start` | `{ providerId }` | `DriverStatus[]` after starting: Android starts (or restarts) its private adb server; iPhone runs `sc start "Apple Mobile Device Service"` through Windows' administrator prompt (UAC). Errors: `driver_missing` (nothing to start, won't start: the message says why), `permission_denied` (the prompt was declined), `not_supported`. Waits for the user at the prompt: no timeout on the page. |
| `drivers.download` | `{ providerId: "android" }` | `DriverDownloadStatus`: starts downloading Google's platform-tools (or returns the download already running) |
| `drivers.downloadStatus` | `{ providerId }` | `DriverDownloadStatus` |
| `drivers.cancelDownload` | `{ providerId }` | `{}`; nothing is left behind |
| `drivers.openLink` | `{ link }` | `{}`. Only these names: `appleDevicesStore` (`ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K`, falling back to `appleDevicesWeb` when the Store app isn't there), `appleDevicesWeb`, `itunes`, `samsungUsbDriver`, `googleUsbDriver`, `oemUsbDrivers`, `platformToolsLicense`. Anything else is invalid params. |

| Event | Params |
| --- | --- |
| `drivers.download` | `DriverDownloadStatus`, at most every 100 ms while downloading, and at each step |

```ts
interface DriverDownloadStatus {
  providerId: string;
  state: 'idle' | 'downloading' | 'verifying' | 'installing' | 'done' | 'failed' | 'cancelled';
  receivedBytes: number; totalBytes: number | null;
  error: string | null;                                               // one English sentence while failed
  errorCode: 'network' | 'corrupt' | 'invalid' | 'unverified' | 'disk' | null;
  version: string | null;                                             // once done: "37.0.1"
}
```

The download (`AndroidProviderOptions.PlatformToolsSource`, Google's
`platform-tools-latest-windows.zip`, about 8 MB) goes to `tools\platform-tools.zip.part` in DeviceDeck's data folder and
is renamed when complete; the zip must hold `platform-tools/adb.exe`, `AdbWinApi.dll` and `AdbWinUsbApi.dll` with every
entry inside `platform-tools/`; the three files must be signed by Google LLC (Authenticode) and `adb.exe version` must
answer before `tools\platform-tools` is replaced. adb.exe is then found in this order: bundled, downloaded,
`DEVICEDECK_ADB_PATH`.

The page shows one Drivers card (Welcome screen and Settings): a row per driver with an icon, a chip (green Ready, grey
Not started, amber Needs action, red Problem), one line and at most one primary button; Android's row lists the phone
hints with the steps to take. Mock: `?drivers=ready|iosMissing|iosStopped|adbMissing|adbDownloading|adbIdle|adbStarting|adbFailed|androidNoDriver`.
