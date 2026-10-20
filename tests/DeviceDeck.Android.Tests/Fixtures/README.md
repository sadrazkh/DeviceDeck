# Android fixtures

Raw command output that the Android provider's parsers read, one folder per phone.

## `synthetic/` — NOT captured from a phone

Every file under `synthetic/` was written by hand from the documented formats and the AOSP sources that print
them (BatteryService.dumpInternal, DiskStatsService, PackageManagerShellCommand.runListPackages,
Settings.dumpPackageLPr, cmds/content, toybox ls/df/stat), because no phone was connected in round 1. Values
(serials, MACs, sizes, package versions) are invented. OEM-specific lines (Samsung's extra battery keys, MIUI
props, colour props) are guesses and the most likely to differ from the real thing.

## `captured/` — real phones

Replace guesses with the truth: plug a phone in and run

    androidprobe capture tests/DeviceDeck.Android.Tests/Fixtures/captured

It writes a folder with the same file names. Review it before committing: it holds the serial, file names and
the list of installed apps. The tests run their generic checks over every folder in both trees, so a captured
folder is tested as soon as it is added.

| File | Command |
| --- | --- |
| getprop.txt | `getprop` |
| settings-device-name.txt | `settings get global device_name` |
| date-z.txt | `date +%z` |
| df.txt | `df -k /data /storage/*` |
| ls-storage.txt | `ls -1 /storage` |
| sm-list-volumes.txt | `sm list-volumes public` |
| dumpsys-diskstats.txt | `dumpsys diskstats` |
| dumpsys-battery.txt | `dumpsys battery` |
| battery-sysfs.txt | power_supply sysfs loop (BatteryParser.SysfsCommand) |
| battery-sticky.txt | `dumpsys activity broadcasts \| grep -o 'android.os.extra.CYCLE_COUNT=[0-9]*'` |
| details-script.txt | DetailsBuilder.Script() (meminfo, uname, wlan0 MAC, IMEI attempt) |
| iphonesubinfo.txt | `service call iphonesubinfo 1 s16 com.android.shell` |
| pm-list-packages.txt | `pm list packages -f -U -i --show-versioncode` |
| pm-list-packages-system.txt | `pm list packages -s` |
| pm-list-media-provider.txt | `pm list packages providers.media` |
| dumpsys-package.txt | `dumpsys package packages` (synthetic ones are trimmed to a few packages) |
| ls-la-sdcard.txt | `ls -la /sdcard/` |
| stat-sdcard.txt | `find '/sdcard/' -mindepth 1 -maxdepth 1 -exec stat -L -c '%f\|%s\|%Y\|%n' {} +` |
| content-query-media.txt | `content query --uri content://media/external/file --projection ... --where 'media_type IN (1,3)'` |
| devices-l.txt | adb server `host:devices-l` |
