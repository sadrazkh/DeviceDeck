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
