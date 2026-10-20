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
  on these roots; the copy is a `copyToPc` job.
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
