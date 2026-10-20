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
    /// Where app labels and icons read from APKs are cached between runs; null turns the disk cache off.
    /// Default: %LOCALAPPDATA%\DeviceDeck\Cache\Android.
    /// </summary>
    public string? CacheDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceDeck", "Cache", "Android");

    /// <summary>How long apps.list waits for labels it hasn't cached yet before falling back to package names.</summary>
    public TimeSpan AppLabelBudget { get; init; } = TimeSpan.FromSeconds(20);
}
