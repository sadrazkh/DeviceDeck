using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Info;

internal static class SummaryBuilder
{
    public const DeviceCapability ReadyCapabilities =
        DeviceCapability.Info | DeviceCapability.Storage | DeviceCapability.Battery
        | DeviceCapability.PhotosRead | DeviceCapability.PhotosDelete | DeviceCapability.PhotosImport
        | DeviceCapability.Files | DeviceCapability.AppDocuments
        | DeviceCapability.AppsList | DeviceCapability.AppsInstall | DeviceCapability.AppsUninstall | DeviceCapability.AppsExport
        | DeviceCapability.Screenshot | DeviceCapability.ScreenMirror | DeviceCapability.Syslog | DeviceCapability.CrashReports
        | DeviceCapability.Reboot | DeviceCapability.Shutdown | DeviceCapability.RebootToRecovery | DeviceCapability.RebootToBootloader
        | DeviceCapability.Rename;

    /// <summary>adb's state word for a device that is fully usable.</summary>
    public const string OnlineState = "device";

    /// <summary>What we know before (or without) reading getprop: the adb line only.</summary>
    public static DeviceSummary FromEntry(AdbDeviceEntry entry)
    {
        // track-devices-l gives model:SM_S918B (spaces become underscores).
        string model = entry.Model?.Replace('_', ' ') ?? "Android phone";
        var (state, capabilities) = entry.State switch
        {
            "unauthorized" => (DeviceState.Unauthorized, DeviceCapability.None),
            "authorizing" => (DeviceState.Unauthorized, DeviceCapability.None),
            // In recovery or sideload adb can still reboot the phone.
            "recovery" or "sideload" or "rescue" => (DeviceState.Connecting, DeviceCapability.Reboot | DeviceCapability.RebootToBootloader | DeviceCapability.RebootToRecovery),
            _ => (DeviceState.Connecting, DeviceCapability.None),
        };
        return new DeviceSummary(
            entry.Serial,
            model,
            DevicePlatform.Android,
            model,
            null,
            "",
            TrackDevicesParser.IsWifiSerial(entry.Serial) ? ConnectionKind.Wifi : ConnectionKind.Usb,
            state,
            DeviceMode.Full,
            capabilities,
            StateDetail: state == DeviceState.Unauthorized ? UnauthorizedDetail : null);
    }

    public static DeviceSummary Ready(AdbDeviceEntry entry, DeviceProfile profile) =>
        FromEntry(entry) with
        {
            Name = profile.DisplayName,
            Model = profile.Model,
            MarketingName = profile.MarketingName,
            OsVersion = profile.AndroidVersion,
            State = DeviceState.Ready,
            Capabilities = ReadyCapabilities,
            ColorHex = profile.ColorHex,
            StateDetail = null,
        };

    /// <summary>DeviceSummary.StateDetail while the phone shows "Allow USB debugging?".</summary>
    public const string UnauthorizedDetail = "Tap Allow on the phone · if nothing appears, unplug it and plug it in again";
}
