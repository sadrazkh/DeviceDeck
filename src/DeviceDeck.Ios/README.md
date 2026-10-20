# DeviceDeck.Ios — the iPhone provider

Talks to iPhones through Apple Mobile Device Service (usbmux on `127.0.0.1:27015`) with our own
protocol layer — see [DECISION.md](DECISION.md) for why. No native binaries; the only package is
`Microsoft.Data.Sqlite` 10.0.13 (MIT) for reading a copy of the phone's `Photos.sqlite`.

## Layout

| Folder | What |
| --- | --- |
| `Plist/` | XML and binary (`bplist00`) property lists, read and write |
| `Protocol/Usbmux/` | usbmux plist protocol: ListDevices, Listen, Connect, ReadBUID, Read/SavePairRecord |
| `Protocol/Lockdown/` | lockdownd (port 62078): QueryType, GetValue, StartSession + TLS, StartService, Pair; pair records |
| `Protocol/Afc/` | Apple File Conduit packets and client (streamed read/write with progress) |
| `Protocol/Services/` | house_arrest (VendDocuments), installation_proxy, springboardservices, diagnostics_relay |
| `Protocol/DeviceConnector.cs` | opens lockdown sessions and services for one phone |
| `Device/` | `IosDevice` (state machine), file system, apps, actions, pairing, lockdown → model mappings |
| `Device/Photos/` | `Photos.sqlite` schema mapping, the photo index, the read-only `IPhotoLibrary` |
| `IosDeviceProvider.cs` | `IDeviceProvider` (`Id = "ios"`): usbmux Listen loop, attach/detach, reconnect |
| `AppleMobileDeviceService.cs` | driver status for the Drivers card (installed? answering? version) |

## Device states

`Connecting` (usbmux attached, handshake running) → one of:

- `AwaitingTrust` — no pair record, or the phone no longer knows this PC. The provider sends lockdown
  `Pair`, so the phone shows *Trust This Computer?*; it retries every 3 s until the user answers.
  If the user taps *Don't Trust*, it stays here until the phone is plugged in again.
- `Locked` — lockdown answered `PasswordProtected` (phone not unlocked since boot, or locked while pairing).
  Checked again every 3 s.
- `Ready` — session works and services start. Capabilities: Info, Storage, Battery, PhotosRead, Files,
  AppDocuments, AppsList, AppsInstall, AppsUninstall, Reboot, Shutdown.

Not advertised: PhotosDelete, PhotosImport, AppsExport, Screenshot (needs Developer Mode), RebootToRecovery,
Backup. Those members return `DeviceException(not_supported)`.

## Notes for the host

- `photos` asset ids are DCIM paths (`DCIM/100APPLE/IMG_0001.HEIC`) — URI-encode them in image URLs.
- `GetThumbnailAsync` returns the phone's own JPEG from `/PhotoData/Thumbnails/V2` when it exists, else
  null: the host should then make one from the original.
- `Photos.sqlite` (and its `-wal`) is copied to `%LOCALAPPDATA%\DeviceDeck\ios\cache\<UDID>\` on its own AFC
  connection; the 150 MB main file is copied again only when it changed, usually just the WAL is. A dropped
  connection or a checkpoint during the copy is retried. The copy is deleted when the phone disconnects.
- Hidden photos (ZHIDDEN) are not in "all", any other album or any count; they are listed only in the album
  with id `hidden` (kind User until `AlbumKind.Hidden` exists). **Privacy default for the host and UI:** keep the
  Hidden album collapsed and its thumbnails blurred until the user reveals it, as iOS asks for Face ID.
- `StartAsync` returns with the phones already plugged in listed (seeded from ListDevices), not empty.
- Storage: iOS 27 has no app-usage key, so `apps` is summed from installation_proxy sizes (fetched in the
  background once the phone is Ready; about 7–12 s on a phone with 300+ apps); `videos` is split from `photos`
  once the photo index exists. Free space is `AmountDataAvailable` (writable now); Settings → iPhone Storage
  shows ~2.5 GB more free because it counts purgeable data, which no lockdown or AFC value reports.
- Battery: health is NominalChargeCapacity / DesignCapacity (92% on an iPhone 16 whose Settings shows 88%; no
  percentage key is exposed on iOS 27). Battery temperature is not exposed on iOS 27 and stays null there.
- Colour: recent iPhones report a numeric DeviceEnclosureColor code; `DeviceColors` maps (model, code) to hex,
  with a verified flag per row (iPhone 16 code 18 = Teal, verified).
- Thumbnails, photo listing and icons share one AFC and one SpringBoard connection per phone, so a grid of
  requests does not open a lockdown session each.
- Pair records DeviceDeck makes are stored through usbmux (where Apple's tools find them); if usbmux will
  not keep one, it goes to `%LOCALAPPDATA%\DeviceDeck\ios\pair\`.

## Measured on a real iPhone 16 (iOS 27.0, USB)

| What | Result |
| --- | --- |
| Photos.sqlite (145 MB + 6 MB WAL) copy and read | 4.7–5.0 s (≈31 MB/s) |
| Pull (phone → PC), Media or app Documents | 33–35 MB/s |
| Push (PC → phone), Media root | 25.8 MB/s |
| Push (PC → phone), an app's Documents (VLC) | 16–17.5 MB/s (the phone's limit: 256 KiB–4 MiB writes all land here) |
| App list with sizes (115 user apps) | 6.7 s |

## Probe

`tools/IosProbe` builds `iosprobe.exe`. With no phone: `iosprobe doctor`. With a phone plugged in and
unlocked, see the command list in `iosprobe --help`.
