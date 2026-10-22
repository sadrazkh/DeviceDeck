namespace DeviceDeck.Android;

public sealed class AndroidProviderOptions
{
    /// <summary>
    /// The adb server port DeviceDeck owns. 5137 keeps it clear of the default 5037 (Android Studio, other tools)
    /// and of ports other apps on the owner's PC hold.
    /// </summary>
    public const int DefaultServerPort = 5137;

    /// <summary>adb.exe to use; null = third_party/platform-tools next to the app or in a parent folder.</summary>
    public string? AdbPath { get; init; }

    public int ServerPort { get; init; } = DefaultServerPort;

    /// <summary>
    /// Stop the adb server when the provider is disposed, if this provider started it. A server that was already
    /// running when the provider started (another DeviceDeck window, a probe) is always left alone.
    /// </summary>
    public bool StopServerOnDispose { get; init; } = true;

    /// <summary>
    /// Run adb's mDNS discovery in the server we start, so phones with Wireless debugging are found (and paired
    /// ones auto-connected) on the local network. Off by default: its backend opens network sockets, which makes
    /// Windows Firewall ask about adb.exe, and USB plus `adb connect ip:port` work without it. Applies to a server
    /// this provider starts; one already running keeps the setting it was started with.
    /// </summary>
    public bool EnableMdns { get; init; }

    /// <summary>Tests: replaces running `adb start-server`.</summary>
    internal Func<CancellationToken, Task>? ServerStarter { get; init; }

    /// <summary>
    /// Where app labels and icons read from APKs are cached between runs; null turns the disk cache off.
    /// Default: %LOCALAPPDATA%\DeviceDeck\Cache\Android.
    /// </summary>
    public string? CacheDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceDeck", "Cache", "Android");

    /// <summary>How long apps.list waits for labels it hasn't cached yet before falling back to package names.</summary>
    public TimeSpan AppLabelBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>scrcpy-server for mirroring; null = DEVICEDECK_SCRCPY_SERVER, then scrcpy/ next to the app, then third_party/scrcpy.</summary>
    public string? ScrcpyServerPath { get; init; }

    // Drivers card (v0.6): Android tools downloaded by DeviceDeck itself, and the phones Windows sees that adb can't use.

    /// <summary>Google's platform-tools for Windows, always the latest.</summary>
    public const string GooglePlatformTools = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";

    /// <summary>
    /// DeviceDeck's tools folder: a download installs into its platform-tools subfolder. Default: tools in DeviceDeck's data
    /// folder (DEVICEDECK_DATA, else %LOCALAPPDATA%\DeviceDeck).
    /// </summary>
    public string? ToolsDirectory { get; init; } = Path.Combine(
        Environment.GetEnvironmentVariable("DEVICEDECK_DATA") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceDeck"),
        "tools");

    /// <summary>Where "Download Android tools" fetches platform-tools from (tests: a local server).</summary>
    public Uri PlatformToolsSource { get; init; } = new(GooglePlatformTools);

    /// <summary>Tests: the folder the bundled tools are looked for from (default: the app's folder).</summary>
    internal string? BaseDirectory { get; init; }

    /// <summary>Tests: replaces reading DEVICEDECK_ADB_PATH.</summary>
    internal Func<string?>? EnvironmentAdbPath { get; init; }

    /// <summary>Tests: replaces `adb start-server` once an adb.exe was found (a downloaded fake one).</summary>
    internal Func<string, CancellationToken, Task>? PathServerStarter { get; init; }

    /// <summary>Tests: the organisation that signed a file (default: its Authenticode signer, null when unsigned or invalid).</summary>
    internal Func<string, string?>? SignerOf { get; init; }

    /// <summary>Tests: what `adb.exe version` says (default: runs it).</summary>
    internal Func<string, CancellationToken, Task<string?>>? ToolsVersionOf { get; init; }

    /// <summary>Tests: the USB devices Windows sees (default: Windows' device list; nothing elsewhere).</summary>
    internal Func<IReadOnlyList<Usb.UsbNode>>? UsbScan { get; init; }

    /// <summary>How long Windows must show a working adb interface that adb doesn't list before the card says so.</summary>
    internal TimeSpan NotSeenByAdbAfter { get; init; } = TimeSpan.FromSeconds(10);
}
