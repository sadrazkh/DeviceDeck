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
