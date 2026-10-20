using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace DeviceDeck.Ios.Protocol.Lockdown;

/// <summary>
/// Finds the pair record for a phone: first by asking usbmux (ReadPairRecord), then in
/// %ProgramData%\Apple\Lockdown\&lt;UDID&gt;.plist where Apple Mobile Device Service keeps them on Windows,
/// then in DeviceDeck's own folder (records DeviceDeck made when usbmux would not store them).
/// </summary>
public sealed class PairRecordStore(IUsbmuxConnector usbmux, string? directory = null, string? localDirectory = null)
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apple", "Lockdown");

    public string Directory { get; } = directory ?? DefaultDirectory;

    public string? LocalDirectory { get; } = localDirectory;

    /// <summary>Null when the PC has never been trusted by this phone.</summary>
    public async Task<PairRecord?> LoadAsync(string udid, CancellationToken ct)
    {
        if (await ReadFromUsbmuxAsync(udid, ct).ConfigureAwait(false) is { } fromMux && TryParse(fromMux) is { } record)
            return record;
        foreach (var dir in new[] { Directory, LocalDirectory })
        {
            if (dir is null)
                continue;
            var path = Path.Combine(dir, udid + ".plist");
            if (!File.Exists(path))
                continue;
            try
            {
                if (TryParse(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false)) is { } fromFile)
                    return fromFile;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable here; try the next place.
            }
        }
        return null;
    }

    /// <summary>
    /// Stores a record DeviceDeck made by pairing. usbmux keeps it where Apple's tools find it; if usbmux
    /// refuses, or cannot give it back, it goes to DeviceDeck's own folder.
    /// </summary>
    public async Task SaveAsync(string udid, int usbmuxDeviceId, PlistDict record, CancellationToken ct)
    {
        var bytes = PlistSerializer.ToXml(record);
        var stored = false;
        try
        {
            await using (var client = await usbmux.ConnectAsync(ct).ConfigureAwait(false))
                stored = await client.SavePairRecordAsync(udid, usbmuxDeviceId, bytes, ct).ConfigureAwait(false);
            stored = stored && await ReadFromUsbmuxAsync(udid, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stored = false;
        }
        if (stored || LocalDirectory is null)
            return;
        System.IO.Directory.CreateDirectory(LocalDirectory);
        var path = Path.Combine(LocalDirectory, udid + ".plist");
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The pair record files on disk, for diagnostics. Never opens the keys.</summary>
    public IReadOnlyList<PairRecordFile> ListFiles()
    {
        var files = new List<PairRecordFile>();
        foreach (var dir in new[] { Directory, LocalDirectory })
        {
            if (dir is null || !System.IO.Directory.Exists(dir))
                continue;
            files.AddRange(new DirectoryInfo(dir)
                .EnumerateFiles("*.plist")
                .Where(f => !f.Name.Equals("SystemConfiguration.plist", StringComparison.OrdinalIgnoreCase))
                .Select(f => new PairRecordFile(Path.GetFileNameWithoutExtension(f.Name), f.FullName, f.Length, f.LastWriteTimeUtc)));
        }
        return files.OrderByDescending(f => f.ModifiedUtc).ToList();
    }

    private async Task<byte[]?> ReadFromUsbmuxAsync(string udid, CancellationToken ct)
    {
        try
        {
            await using var client = await usbmux.ConnectAsync(ct).ConfigureAwait(false);
            return await client.ReadPairRecordAsync(udid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Older usbmux builds do not know ReadPairRecord; the files are the fallback.
            return null;
        }
    }

    private static PairRecord? TryParse(byte[] bytes)
    {
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
}

public sealed record PairRecordFile(string Udid, string Path, long Size, DateTime ModifiedUtc);
