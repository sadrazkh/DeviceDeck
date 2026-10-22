# DeviceDeck.Android

The Android provider: `AndroidDeviceProvider` (`IDeviceProvider`, id `"android"`) and one `AndroidDevice`
(`IDevice`) per phone, over adb. Targets Android 10 (API 29) to 16 (API 36).

## adb

- `tools/fetch-platform-tools.ps1` downloads Google's `platform-tools-latest-windows.zip` into the git-ignored
  `third_party/platform-tools/`, checks adb.exe's Authenticode signature (Google LLC) and prints the version.
  Run it again with `-Force` to update.
- adb.exe is looked up in `AndroidProviderOptions.AdbPath` (when set, only there); otherwise, in this order
  (v0.6): the **bundled** tools, `third_party/platform-tools/adb.exe` (or `platform-tools/adb.exe`) next to the app
  or in any parent folder; the tools DeviceDeck **downloaded** itself, `tools\platform-tools\adb.exe` in its data
  folder (`DEVICEDECK_DATA`, else `%LOCALAPPDATA%\DeviceDeck`); then `DEVICEDECK_ADB_PATH`.
- **Download Android tools** (Drivers card, when no adb.exe is found): `platform-tools-latest-windows.zip` from
  `AndroidProviderOptions.PlatformToolsSource` (Google's URL; tests use a local fake server) into
  `tools\platform-tools.zip.part`, renamed only when complete; the zip must hold `platform-tools/adb.exe`,
  `AdbWinApi.dll` and `AdbWinUsbApi.dll` with every entry inside `platform-tools/`; it is unpacked into
  `tools\platform-tools.new`, where the three files must carry a valid Authenticode signature from **Google LLC**
  (WinVerifyTrust, no revocation lookups) and `adb.exe version` must answer; only then does it become
  `tools\platform-tools` and the private server starts from it. A failure or Cancel leaves only the previous install.
- **Status** (`GetDriverStatusAsync`): Ready when the server answers; Missing without adb.exe; Idle before the
  provider started ("not started yet", grey on the card); Starting while the tracker starts the server, also after it
  went away (never "Stopped": v0.5.3 showed "Stopped" while adb ran); Error with adb's own words when it won't start.
  Start/Restart on the card is `IDriverControl.StartDriverAsync`. Phones Windows sees that adb can't use (USB
  debugging off, no USB driver, unauthorized, offline) come from Windows' USB list (cfgmgr32, read only) and adb's
  device list, as `DriverStatus.Phones`.
- DeviceDeck runs **its own adb server on port 5137** (`adb -P 5137 start-server` with
  `ANDROID_ADB_SERVER_PORT=5137`). The default server on 5037 (Android Studio, scrcpy, another tool) is never
  touched, so the two can't kill each other over a version mismatch.
- **No network listeners.** The server is started with `ADB_MDNS=0` (adb's switch in
  `client/mdns_utils.cpp`), and `ADB_SERVER_SOCKET` / `ANDROID_ADB_SERVER_ADDRESS` are removed from its
  environment, so it listens on `tcp:5137` = 127.0.0.1 only. Measured with platform-tools 37.0.1: default →
  only `TCP 127.0.0.1:5137`; with mDNS on, adb also binds `UDP :::5353`, which is what makes Windows Firewall ask
  about adb.exe. USB and `adb connect ip:port` work without mDNS; `AndroidProviderOptions.EnableMdns = true`
  (probe: `--mdns`) turns discovery of Wireless-debugging phones back on. `androidprobe doctor` shows the state.
- When the provider is disposed it stops the server **if it started it** (`host:kill`, what `adb kill-server`
  sends). A server that was already answering on 5137 when the provider started — another DeviceDeck window, a
  probe run — is left alone. `AndroidProviderOptions.StopServerOnDispose = false` (probe: `--keep-server`)
  keeps it running.
- If adb.exe is missing but something already answers on 5137, the provider uses it.

### What comes from where

| Piece | How |
| --- | --- |
| Device list | our own `host:track-devices-l` client (`DeviceTracker`); reconnects and restarts the server when it dies |
| Shell commands | our own `shell,v2,raw:` client (`AdbChannel`): stdout and stderr apart, real exit codes, binary-safe |
| Installs | `exec:cmd package install -S` / `install-create` / `install-write` / `install-commit` streamed from the PC |
| Push / pull | AdvancedSharpAdbClient's `SyncService` (Apache-2.0), with progress |
| Reboot | `reboot:` / `reboot:recovery` / `reboot:bootloader` |

The library's own install helpers lose the failure text (`InstallCommitAsync` throws with the lines *after* the
`Failure [...]` line), and its shell helpers have no exit code, so those two parts are our own small wire code.

## States

| adb says | `DeviceSummary.State` | Capabilities |
| --- | --- | --- |
| `unauthorized`, `authorizing` | `Unauthorized` (phone shows "Allow USB debugging?") | none |
| `device` | `Connecting` while getprop is read, then `Ready` | everything below |
| `offline`, `connecting`, others | `Connecting` | none |
| `recovery`, `sideload`, `rescue` | `Connecting` | reboot only |
| gone from the list | `Removed` event with `Disconnected` | — |

`Connection` is `Wifi` for `ip:port` serials (`adb connect`, `adb tcpip`) and mDNS serials
(`adb-…._adb-tls-connect._tcp`, Wireless debugging on Android 11+), otherwise `Usb`. `Id` is the adb serial.

Ready capabilities: Info, Storage, Battery, PhotosRead, PhotosDelete, PhotosImport, Files, AppsList,
AppsInstall, AppsUninstall, AppsExport, Screenshot, Reboot, Shutdown, RebootToRecovery, RebootToBootloader.

## Features

- **Summary**: `getprop` (model, retail name from `ro.product.marketname` / `ro.vendor.oplus.market.name` / …,
  Android version) and `settings get global device_name`. Colour from the few props that carry one
  (`ro.boot.hardware.color`, …) — best effort, null when unknown.
- **Storage**: `df -k /data /storage/*` + `dumpsys diskstats`. The total is rounded the way Settings does
  (`FileUtils.roundStorageSize`: 110 GB of /data on a 128 GB phone shows as 128 GB; the gap counts as "system").
  Categories come from diskstats' cached sizes, which the phone refreshes about daily while charging; until then
  the list is empty. SD cards (and USB drives, told apart with `sm list-volumes public`) are extra volumes.
- **Battery**: `dumpsys battery` (level, charging, temperature). Cycle count from the sticky
  `ACTION_BATTERY_CHANGED` extra `android.os.extra.CYCLE_COUNT` (Android 14+, read through
  `dumpsys activity broadcasts`) or power_supply sysfs; health % and capacities from
  `charge_full` / `charge_full_design` when the phone lets the shell read them. Otherwise null.
- **Details**: model, manufacturer, Android version, security patch, build, serial, IMEI (only when
  `service call iphonesubinfo 1` answers — stock Android 10+ refuses the shell, so the row is left out), Wi-Fi MAC
  (the address in use; with MAC randomisation that's the per-network one), chipset, RAM, ABI, baseband, kernel.
- **Files**: roots `internal` (/sdcard) and `sd:<id>` (/storage/<id>). Listings run
  `find <dir>/ -mindepth 1 -maxdepth 1 -exec stat -L -c '%f|%s|%Y|%n' {} +` (exact sizes, epoch times,
  symlinks followed, names with spaces, `|` and any Unicode), falling back to parsing `ls -la`. Transfers use
  one sync connection per opened root. Paths can't climb out of the root (`..` is refused).
- **Photos**: `content query --uri content://media/external/file` for images and videos (Android 10+'s
  `external` volume covers SD cards too). Albums are buckets plus All, Videos and Favourites. Thumbnails are
  decoded from the original with SkiaSharp (JPEG/PNG/WebP/GIF/BMP, EXIF orientation applied, JPEG DCT scaling);
  videos, HEIC and anything Skia can't read use MediaStore's own thumbnail
  (`content read --uri content://media/external/{images|video}/media/<id>/thumbnail`). Delete is
  `content delete` (MediaProvider removes the file too), with `rm` + scan for anything left indexed.
  Import pushes to `DCIM/DeviceDeck`, picks a free name and waits for the row to appear.
- **Apps**: `pm list packages -f -U -i --show-versioncode`, `pm list packages -s`, `dumpsys package packages`
  (version name, install time, installer), `dumpsys diskstats` (sizes; `du -sk` of the code path when diskstats
  has none). Labels and icons are read **from the APK in place**: the ZIP central directory and only the needed
  entries are fetched with `dd` range reads (no full pull), then AndroidManifest.xml (binary XML) and
  resources.arsc (dense, sparse, 16-bit-offset and compact entries) are parsed. Icons: the best PNG/WebP at
  xhdpi or above, else an adaptive icon drawn with SkiaSharp (colour, bitmap, inset, layer-list and vector
  drawable layers, gradients), masked to a rounded square, 192 px PNG. Labels/icons are cached by
  package + APK path + version code (memory and `%LOCALAPPDATA%\DeviceDeck\Cache\Android`).
  `apps.list` waits up to 20 s for uncached labels, then shows package names and keeps loading in the
  background for next time.
- **Install**: `.apk`, several split `.apk`s, `.apks` (bundletool or SAI), `.xapk` (with OBB files, pushed to
  `Android/obb/<package>/`), unencrypted `.apkm`. ABI splits the phone can't run are dropped. A failed session
  is abandoned. **Uninstall** `pm uninstall`. **Export** `pm path` + pull of every split.
- **Actions**: reboot (system / recovery / bootloader), shutdown (`reboot -p`), screenshot (`screencap -p`
  over shell v2, so the PNG bytes arrive unchanged).

## Media scan by Android version

Files adb creates under /sdcard must reach MediaStore before the gallery (and our photo list) shows them.

| Android | Storage under /sdcard | What DeviceDeck does |
| --- | --- | --- |
| 10 (API 29) | sdcardfs: MediaProvider is **not** told about writes | `am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file:///storage/emulated/0/… -p com.android.providers.media` after every push, rename and delete |
| 11–16 (API 30–36) | FUSE: MediaProvider indexes a file when a writer closes it, so adb pushes through /sdcard are picked up on their own | photo import still sends the same broadcast, addressed to the MediaProvider module (`com.google.android.providers.media.module` on Google builds, `com.android.providers.media.module` on AOSP) — `MediaReceiver` in the module still handles `MEDIA_SCANNER_SCAN_FILE` with a `file:` URI in current AOSP main — then polls `content query --where _data=…` for up to 3 s |

The broadcast is explicit (`-p <package>`) because implicit broadcasts to manifest receivers are dropped since
Android 8. Paths are canonicalised to `/storage/emulated/0/...` (how MediaStore keys rows) and percent-encoded.
`content call --method scan_file` was not used: it needs a permission the shell doesn't reliably hold.

## Errors

Everything thrown is a `DeviceException`. adb's `device unauthorized` → `unauthorized` ("unlock the phone and tap
Allow"), `device offline` → `device_disconnected`, unknown device → `device_not_found`, no server →
`driver_missing`, sync/shell errno text → `path_not_found` / `permission_denied` / `already_exists`, every
`INSTALL_FAILED_*` → `install_failed` with a plain sentence (Xiaomi's "Install via USB" being off shows as
`INSTALL_FAILED_USER_RESTRICTED` and says so).

## App folders (round 2)

`IAppFilesSource` lists, in one shell round trip, each package folder under `Android/media` and, when adb may
read it, `Android/data` (with whether it holds any file), then `Documents` and `Download`. Each is a root of its
own (`appmedia:<package>`, `appdata:<package>`, `shared:Documents`, `shared:Download`) on the normal sync file
system. Whether `Android/data` is readable is probed (`ls` must succeed and list something), never assumed;
when it isn't, the page says so and shows the media folders only. Measuring is one `find … -exec stat` call.
Not checked on a real phone yet (none this round): fixtures and `FakeSharedStorage` in the tests.

## Not done / limits

- Contacts, messages, backup/restore, screen mirroring (scrcpy) and logcat are not implemented
  (`not_supported` by capability).
- `AppInfo.DataBytes` is 0 until diskstats has cached sizes (an app's data folder isn't readable without root).
- Body colour, Samsung's extra battery keys and battery sysfs access are guesses until real phones are captured.
- A file name containing a newline breaks one listing line.
- Thumbnails pull the whole original (capped at 48 MB, then MediaStore's thumbnail); the host caches them on disk.
