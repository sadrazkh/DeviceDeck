namespace DeviceDeck.Core.Abstractions;

// Shared models every provider returns and the bridge serialises (camelCase JSON, enums as camelCase strings).
// Frozen for the parallel round: propose changes in your report instead of editing this file.

public enum DevicePlatform { Ios, Android }

public enum ConnectionKind { Usb, Wifi }

/// <summary>Full = the platform's own protocol (usbmux/lockdown, adb). Limited = MTP/PTP through Windows Portable Devices.</summary>
public enum DeviceMode { Full, Limited }

public enum DeviceState
{
    /// <summary>Seen on the bus, not yet usable (handshake running).</summary>
    Connecting,
    /// <summary>iPhone: no pairing record yet, the phone shows "Trust This Computer?".</summary>
    AwaitingTrust,
    /// <summary>Android: the phone shows "Allow USB debugging?".</summary>
    Unauthorized,
    /// <summary>iPhone: paired but locked; some services refuse until it is unlocked.</summary>
    Locked,
    Ready,
    Disconnected,
}

[Flags]
public enum DeviceCapability : long
{
    None = 0,
    Info = 1L << 0,
    Storage = 1L << 1,
    Battery = 1L << 2,
    PhotosRead = 1L << 3,
    PhotosDelete = 1L << 4,
    PhotosImport = 1L << 5,
    Files = 1L << 6,
    AppDocuments = 1L << 7,
    AppsList = 1L << 8,
    AppsInstall = 1L << 9,
    AppsUninstall = 1L << 10,
    AppsExport = 1L << 11,
    Backup = 1L << 12,
    Restore = 1L << 13,
    Contacts = 1L << 14,
    Messages = 1L << 15,
    Screenshot = 1L << 16,
    ScreenMirror = 1L << 17,
    Syslog = 1L << 18,
    CrashReports = 1L << 19,
    Reboot = 1L << 20,
    Shutdown = 1L << 21,
    RebootToRecovery = 1L << 22,
    RebootToBootloader = 1L << 23,

    // Round 2 additions: one block per feature, bits far apart so parallel branches can't pick the same one.

    /// <summary>The phone's name can be changed from the PC (device.rename); the device implements <see cref="IDeviceRenamer"/>.</summary>
    Rename = 1L << 40,
}

/// <param name="Id">Stable across reconnects: the iPhone's UDID or the Android serial.</param>
/// <param name="StateDetail">One plain sentence about the state when it needs the user ("Don't Trust was tapped · unplug and plug in again"); null otherwise.</param>
public sealed record DeviceSummary(
    string Id,
    string Name,
    DevicePlatform Platform,
    string Model,
    string? MarketingName,
    string OsVersion,
    ConnectionKind Connection,
    DeviceState State,
    DeviceMode Mode,
    DeviceCapability Capabilities,
    string? ColorHex = null,
    string? StateDetail = null);

/// <param name="Key">One of: photos, videos, apps, media, system, other.</param>
public sealed record StorageCategory(string Key, long Bytes);

public sealed record StorageVolume(string Key, string Label, long TotalBytes, long FreeBytes, IReadOnlyList<StorageCategory> Categories);

/// <summary>Internal storage first; an Android SD card is a second volume.</summary>
public sealed record StorageInfo(IReadOnlyList<StorageVolume> Volumes);

public sealed record BatteryInfo(
    int? LevelPercent,
    bool IsCharging,
    int? HealthPercent,
    int? CycleCount,
    double? TemperatureCelsius,
    int? DesignCapacityMah,
    int? FullChargeCapacityMah);

/// <param name="Sensitive">IMEI, serial and the like: the UI masks them until Reveal.</param>
public sealed record DetailRow(string Key, string Label, string Value, bool Sensitive = false);

public sealed record DeviceDetails(IReadOnlyList<DetailRow> Rows);

/// <param name="Path">Absolute path inside the root it was listed from, '/' separated.</param>
public sealed record FileEntry(string Path, string Name, bool IsDirectory, long Size, DateTimeOffset? Modified, bool ReadOnly = false);

public enum AppSource { Store, Sideloaded, System }

/// <param name="Id">Bundle id (iOS) or package name (Android).</param>
/// <param name="FileSharing">iOS: UIFileSharingEnabled, so house_arrest can vend its Documents.</param>
public sealed record AppInfo(
    string Id,
    string Name,
    string Version,
    long AppBytes,
    long DataBytes,
    AppSource Source,
    bool FileSharing,
    DateTimeOffset? InstalledAt);

public enum PhotoKind { Photo, Video, LivePhoto, Screenshot }

public enum AlbumKind { All, Camera, Screenshots, Videos, Live, Favorites, User, Folder }

public sealed record PhotoAlbum(string Id, string Name, AlbumKind Kind, int Count);

/// <param name="InCloudOnly">Optimised storage: the original is in iCloud, not on the phone; not exportable over USB.</param>
/// <param name="LivePairId">Id of the video half of a Live Photo, when there is one.</param>
public sealed record PhotoAsset(
    string Id,
    string FileName,
    PhotoKind Kind,
    DateTimeOffset? TakenAt,
    long Size,
    int? Width,
    int? Height,
    double? DurationSeconds,
    bool IsFavorite,
    bool InCloudOnly,
    string? LivePairId);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

public enum RebootTarget { System, Recovery, Bootloader }

public sealed record InstallProgress(string Stage, double? Percent);

/// <summary>Thrown by providers; the bridge maps Code to the JSON-RPC error's data.code (see docs/bridge-contract.md).</summary>
public sealed class DeviceException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class DeviceErrorCodes
{
    public const string NotFound = "device_not_found";
    public const string Disconnected = "device_disconnected";
    public const string Locked = "device_locked";
    public const string TrustRequired = "trust_required";
    public const string Unauthorized = "unauthorized";
    public const string NotSupported = "not_supported";
    public const string PathNotFound = "path_not_found";
    public const string PermissionDenied = "permission_denied";
    public const string AlreadyExists = "already_exists";
    public const string InstallFailed = "install_failed";
    public const string DriverMissing = "driver_missing";
    public const string Protocol = "protocol_error";
}
