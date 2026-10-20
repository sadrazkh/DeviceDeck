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

    /// <summary>Ask installation_proxy for app sizes as soon as a phone is ready (for the storage categories).</summary>
    public bool PrefetchAppSizes { get; init; } = true;
}
