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
