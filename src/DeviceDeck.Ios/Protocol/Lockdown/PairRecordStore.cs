using DeviceDeck.Ios.Protocol.Usbmux;

namespace DeviceDeck.Ios.Protocol.Lockdown;

/// <summary>
/// Finds the pair record for a phone: first by asking usbmux (ReadPairRecord), then in
/// %ProgramData%\Apple\Lockdown\&lt;UDID&gt;.plist, where Apple Mobile Device Service keeps them on Windows.
/// </summary>
public sealed class PairRecordStore(IUsbmuxConnector usbmux, string? directory = null)
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apple", "Lockdown");

    public string Directory { get; } = directory ?? DefaultDirectory;

    /// <summary>Null when the PC has never been trusted by this phone.</summary>
    public async Task<PairRecord?> LoadAsync(string udid, CancellationToken ct)
    {
        byte[]? bytes = null;
        try
        {
            await using var client = await usbmux.ConnectAsync(ct).ConfigureAwait(false);
            bytes = await client.ReadPairRecordAsync(udid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Older usbmux builds do not know ReadPairRecord; the file is the fallback.
        }

        if (bytes is null)
        {
            var path = Path.Combine(Directory, udid + ".plist");
            if (!File.Exists(path))
                return null;
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }

        try
        {
            return PairRecord.Parse(bytes);
        }
        catch (FormatException)
        {
            // A half-written or foreign file: treat as "not paired" so the trust flow can run again.
            return null;
        }
    }

    /// <summary>The pair record files on disk, for diagnostics. Never opens the keys.</summary>
    public IReadOnlyList<PairRecordFile> ListFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        return new DirectoryInfo(Directory)
            .EnumerateFiles("*.plist")
            .Where(f => !f.Name.Equals("SystemConfiguration.plist", StringComparison.OrdinalIgnoreCase))
            .Select(f => new PairRecordFile(Path.GetFileNameWithoutExtension(f.Name), f.FullName, f.Length, f.LastWriteTimeUtc))
            .OrderByDescending(f => f.ModifiedUtc)
            .ToList();
    }
}

public sealed record PairRecordFile(string Udid, string Path, long Size, DateTime ModifiedUtc);
