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
- `Photos.sqlite` (and its `-wal`) is copied to `%LOCALAPPDATA%\DeviceDeck\ios\cache\<UDID>\` and reused while
  unchanged on the phone; the copy is deleted when the phone disconnects.
- Thumbnails, photo listing and icons share one AFC and one SpringBoard connection per phone, so a grid of
  requests does not open a lockdown session each.
- Pair records DeviceDeck makes are stored through usbmux (where Apple's tools find them); if usbmux will
  not keep one, it goes to `%LOCALAPPDATA%\DeviceDeck\ios\pair\`.

## Probe

`tools/IosProbe` builds `iosprobe.exe`. With no phone: `iosprobe doctor`. With a phone plugged in and
unlocked, see the command list in `iosprobe --help`.
