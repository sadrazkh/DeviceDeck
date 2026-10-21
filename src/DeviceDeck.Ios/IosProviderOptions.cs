namespace DeviceDeck.Ios;

public sealed class IosProviderOptions
{
    private static readonly string LocalRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceDeck", "ios");

    /// <summary>Where Apple Mobile Device Service keeps pair records (%ProgramData%\Apple\Lockdown).</summary>
    public string? ApplePairRecordDirectory { get; init; }

    /// <summary>Where DeviceDeck keeps a pair record it made itself when usbmux would not store it.</summary>
    public string LocalPairRecordDirectory { get; init; } = Path.Combine(LocalRoot, "pair");

    /// <summary>Temp copies of Photos.sqlite, one folder per phone.</summary>
    public string CacheDirectory { get; init; } = Path.Combine(LocalRoot, "cache");

    /// <summary>How often a phone that is waiting for Trust or locked is checked again.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait before listening again after Apple Mobile Device Service drops.</summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Send lockdown Pair when there is no pair record, so the phone shows "Trust This Computer?".</summary>
    public bool AutoPair { get; init; } = true;

    // Round 4 (photodelete)

    /// <summary>
    /// Windows Portable Devices, to delete photos the way File Explorer does. Null (the default here; the provider's
    /// parameterless constructor sets the real one): photos can't be deleted and PhotosDelete is never reported.
    /// </summary>
    public Wpd.IWpdBackend? Wpd { get; init; }

    /// <summary>Where photo-deletes-yyyyMMdd.log goes (one line per photo deleted); null keeps the lines in memory only.</summary>
    public string? PhotoDeleteLogDirectory { get; init; } = PhotoDelete.PhotoDeleteLog.DefaultFolder;

    /// <summary>How long to wait before asking Windows again whether it sees the phone as a camera, after it just connected.</summary>
    public TimeSpan PhotoDeleteRetryInterval { get; init; } = TimeSpan.FromSeconds(3);
}
