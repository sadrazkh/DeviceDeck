# DeviceDeck

A Windows 11 app for an iPhone and an Android phone: photos, files, the documents inside apps, apps, backups and a toolbox, behind a Mica-glass interface. Working name.

- Plan, design brief and roadmap: https://claude.ai/code/artifact/e6bbc0f8-d924-42c0-86a0-f7f6ae279bfd
- Page ↔ host contract: [docs/bridge-contract.md](docs/bridge-contract.md)
- Working rules and who owns what: [CLAUDE.md](CLAUDE.md)

## Layout

```
src/DeviceDeck.Shell     WinUI 3 window, Mica, WebView2 host
src/DeviceDeck.Bridge    JSON-RPC between the page and the host
src/DeviceDeck.Core      shared abstractions, device hub, transfer queue
src/DeviceDeck.Ios       iPhone provider (usbmux, lockdown, AFC, house_arrest, apps, photos)
src/DeviceDeck.Android   Android provider (adb, sync, packages, MediaStore)
web/                     React UI + mock backend
tools/IosProbe, tools/AndroidProbe   command-line probes to run with a phone plugged in
tests/                   unit tests with captured fixtures
```

## Requirements

- Windows 11, .NET 10 SDK, Node 22
- iPhone: Apple Mobile Device Support (comes with iTunes or the Apple Devices app)
- Android: USB debugging on; adb is fetched into `third_party/` by `tools/fetch-platform-tools.ps1`
