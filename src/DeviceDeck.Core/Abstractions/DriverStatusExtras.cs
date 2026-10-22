using System.Text.Json.Serialization;

namespace DeviceDeck.Core.Abstractions;

// The Drivers card (v0.6): what the page needs beyond State and Detail to word each row itself and offer the one thing
// to do. Each extra is left out of the JSON when null, so a provider that sets none answers exactly as before.

public sealed partial record DriverStatus
{
    /// <summary>Why the state is what it is, as one of <see cref="DriverReasons"/>; the page words it in its own language.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(1)] // after the four the contract always had
    public string? Reason { get; init; }

    /// <summary>Where the driver's files came from, one of <see cref="DriverSources"/> (Android's adb).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(1)] // after the four the contract always had
    public string? Source { get; init; }

    /// <summary>Phones plugged in that this driver can't use yet, and why (Android: USB debugging off, no USB driver…).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(1)] // after the four the contract always had
    public IReadOnlyList<DriverPhoneHint>? Phones { get; init; }
}

/// <summary>A phone Windows (or adb) sees that can't be used yet.</summary>
/// <param name="Name">What Windows or adb calls it ("Galaxy S23", "Android phone").</param>
/// <param name="Problem">One of <see cref="DriverPhoneProblems"/>.</param>
/// <param name="Maker">"samsung", "google", "xiaomi"… from the USB vendor id, when known: the page links the maker's USB driver.</param>
public sealed record DriverPhoneHint(string Name, string Problem, string? Maker = null);

/// <summary>Values of <see cref="DriverStatus.Reason"/>.</summary>
public static class DriverReasons
{
    /// <summary>Apple Mobile Device Support isn't installed.</summary>
    public const string AmdsMissing = "amdsMissing";

    /// <summary>Apple Mobile Device Service is installed but not answering (stopped).</summary>
    public const string AmdsStopped = "amdsStopped";

    /// <summary>Apple Mobile Device Service answered something unexpected.</summary>
    public const string AmdsError = "amdsError";

    /// <summary>No adb.exe: not bundled, not downloaded, no DEVICEDECK_ADB_PATH.</summary>
    public const string AdbMissing = "adbMissing";

    /// <summary>DeviceDeck is starting its private adb server now.</summary>
    public const string AdbStarting = "adbStarting";

    /// <summary>The private adb server isn't running and DeviceDeck hasn't been asked to start it yet.</summary>
    public const string AdbNotStarted = "adbNotStarted";

    /// <summary>The private adb server won't start; Detail has adb's own words.</summary>
    public const string AdbFailed = "adbFailed";

    /// <summary>A provider threw while it started; Detail has the message.</summary>
    public const string StartFailed = "startFailed";

    /// <summary>Asking the provider for its status threw; Detail has the message.</summary>
    public const string CheckFailed = "checkFailed";
}

/// <summary>Values of <see cref="DriverStatus.Source"/>.</summary>
public static class DriverSources
{
    /// <summary>Shipped with DeviceDeck (platform-tools next to the app, or third_party in a developer's checkout).</summary>
    public const string Bundled = "bundled";

    /// <summary>Downloaded by DeviceDeck into its data folder (tools\platform-tools).</summary>
    public const string Downloaded = "downloaded";

    /// <summary>The DEVICEDECK_ADB_PATH variable.</summary>
    public const string Environment = "environment";

    /// <summary>A path given in code (tests, probes).</summary>
    public const string Configured = "configured";
}

/// <summary>Values of <see cref="DriverPhoneHint.Problem"/>.</summary>
public static class DriverPhoneProblems
{
    /// <summary>Windows sees an Android phone (as a camera or media device) but it offers no adb: USB debugging is off.</summary>
    public const string UsbDebuggingOff = "usbDebuggingOff";

    /// <summary>The phone offers adb but Windows has no driver for it (Samsung and others need the maker's USB driver).</summary>
    public const string DriverMissing = "driverMissing";

    /// <summary>adb sees the phone, waiting for "Allow USB debugging?" on it.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>adb sees the phone but it doesn't answer (offline): unplug and plug in again.</summary>
    public const string Offline = "offline";

    /// <summary>Windows has adb's interface for it, working, yet adb lists no phone.</summary>
    public const string NotSeenByAdb = "notSeenByAdb";
}
