# iPhone protocol library: decision

**Decision: write our own thin protocol layer** (`src/DeviceDeck.Ios/Protocol/`), in C#, with no
third-party runtime dependency except `Microsoft.Data.Sqlite` (MIT) for reading `Photos.sqlite`.
Netimobiledevice stays a reference (MIT). If we ever copy a file from it, we keep its licence header
and add it to a `THIRD-PARTY-NOTICES.md` next to this file.

Date: 2026-10-20. Evaluated: Netimobiledevice 2.5.2 (NuGet, published 4 Jun 2025), source at
https://github.com/artehe/Netimobiledevice (`main`).

## What we need vs. what Netimobiledevice 2.5.2 has

| Need | Netimobiledevice 2.5.2 | Notes |
| --- | --- | --- |
| usbmux listen + connect | yes (`Usbmuxd/`) | `CreateUsingUsbmux` is synchronous; the async variant takes no `CancellationToken` (uses an internal timeout) |
| pair records from `%ProgramData%\Apple\Lockdown` | yes (`Lockdown/Pairing/`) | |
| lockdown `GetValue` incl. `com.apple.disk_usage`, battery domain; TLS session | yes (`LockdownClient`, `ServiceConnection`) | TLS through `SslStream`, TLS 1.2/1.3; `StartSslAsync` takes no `CancellationToken` |
| AFC | yes (`Afc/`, `Services/AfcService.cs`) | |
| house_arrest `VendDocuments` | yes (`Services/HouseArrestService.cs`) | |
| installation_proxy Browse / Install / Uninstall | yes (`Services/InstallationProxyService.cs`) | |
| springboardservices `getIconPNGData` | yes (`GetIconPngDataAsync`) | |
| diagnostics_relay IORegistry / Restart / Shutdown | yes (`Diagnostics/DiagnosticsService.cs`) | mixed: some sync-only overloads, some async |

Feature coverage is not the problem. These are:

1. **Unmaintained and unlisted.** nuget.org says: "The owner has unlisted this package" and "This
   package has no owners and is not being actively maintained." Nobody will fix it when iOS 19/20
   changes lockdown, AFC or Photos.sqlite. We would be maintaining it either way.
2. **Dependencies we do not want.** 2.5.2 targets `net8.0` only and pulls in `Unofficial.WinTun`
   (a binding to the WinTun virtual-adapter driver, for iOS 17+ RemoteXPC tunnels; the package page
   shows no licence), `Zeroconf` (Bonjour, for Wi-Fi discovery) and
   `Microsoft.Extensions.Logging.Abstractions`. None is needed for USB; WinTun is a native
   driver binding with unclear licensing, which our rules (no unvetted native binaries, licence
   allow-list) push against.
3. **API shape.** Our rule is async all the way with a `CancellationToken` on every I/O call.
   Netimobiledevice mixes synchronous socket calls (`Stream.ReadExactly`, `sock.Connect`,
   `AuthenticateAsClient`) with async ones, and its connect/TLS entry points take no token.
   Wrapping it would hide cancellation, not provide it.
4. **Errors.** Our providers throw `DeviceException` with a `DeviceErrorCodes` code. Netimobiledevice
   throws its own exception tree (`NotPairedException`, `PasswordRequiredException`, …); we would
   need a translation layer on every call.
5. **Testability.** We test against captured fixtures with no phone. Owning the framing code lets
   every layer run over an injected `Stream`, so usbmux, lockdown, AFC and the plist-RPC services
   are tested byte-for-byte without a device.

**Why not vendor/fork the source?** Coverage is fine, but the useful part (usbmux, plist, lockdown,
AFC, five small plist-RPC services) is a small slice of a library that also carries backup,
RemoteXPC/Remoted, Bonjour, syslog, profiles and crash reports. A fork means carrying all that, or
trimming and refactoring it for cancellation and `DeviceException` — at which point it is our code
with someone else's structure. The protocols we need are small and well documented:

| Layer | Size (approx.) | Public description used |
| --- | --- | --- |
| plist (XML + binary `bplist00`) | 600 lines | Apple's `CFBinaryPList.c` format notes, plist DTD |
| usbmux (plist protocol v1 over 127.0.0.1:27015) | 250 lines | libusbmuxd/usbmuxd protocol notes |
| lockdown (length-prefixed plists, `StartSession` → TLS, `StartService`) | 350 lines | libimobiledevice docs, The iPhone Wiki |
| AFC (`CFA6LPAA` packets) | 450 lines | The iPhone Wiki "AFC" |
| house_arrest, installation_proxy, springboard, diagnostics_relay | 50–100 lines each | The iPhone Wiki |

pymobiledevice3 (GPL-3) and libimobiledevice (LGPL) are read as protocol descriptions only; no code is
copied from either.

## Consequences

- No native binaries, no extra NuGet packages beyond `Microsoft.Data.Sqlite`.
- TLS uses `System.Net.Security.SslStream` (SChannel on Windows). The pair record's PEM host
  certificate and key are loaded with `X509Certificate2.CreateFromPem` and re-imported through PKCS#12
  so SChannel can use the private key.
- When a later phase needs backup (mobilebackup2) or iOS 17+ developer services over RemoteXPC
  (Screenshot, ScreenMirror), revisit Netimobiledevice's `Backup/` and `Remoted/` as MIT code to vendor
  with notices, rather than writing those from scratch.
