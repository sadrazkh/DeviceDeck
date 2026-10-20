using System.Globalization;
using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Cleanup;

/// <summary>
/// Clean-up on an Android phone, all through the shell: md5sum next to the files (nothing is copied to the PC),
/// one `find` over the shared app folders, the cache sizes the phone keeps in diskstats, and `pm trim-caches`.
/// Everything except <see cref="TrimCachesAsync"/> only reads.
/// </summary>
internal sealed class AndroidCleanup(IAdbChannel channel, Func<int> sdk)
{
    /// <summary>Folders of shared storage where apps (and downloads) keep big files, under /sdcard.</summary>
    public static readonly string[] AppFolders = ["/Android/data", "/Android/media", "/Download", "/Documents"];

    private const string SharedRoot = "/sdcard";

    /// <summary>One shell call hashes at most this many files, or this many bytes, so progress keeps moving.</summary>
    private const int BatchFiles = 100;
    private const long BatchBytes = 2_000_000_000;

    /// <summary>Far more than any phone holds: trim-caches frees caches until this much is free, so all of them.</summary>
    internal const string TrimAll = "999999999999999";

    public async Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct)
    {
        var items = await ResolveAsync(assetIds, ct).ConfigureAwait(false);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long done = 0;
        foreach (var batch in Batches(items, prefixBytes))
        {
            ct.ThrowIfCancellationRequested();
            var result = await channel.ShellAsync(HashScript(batch.Select(i => i.Path).ToList(), prefixBytes), ct).ConfigureAwait(false);
            foreach (var (index, hash) in ParseHashes(result.Output))
            {
                if (index >= 0 && index < batch.Count)
                {
                    hashes[batch[index].Id.ToString(CultureInfo.InvariantCulture)] = hash;
                }
            }

            done += batch.Sum(i => prefixBytes is { } p ? Math.Min(p, i.Size) : i.Size);
            progress?.Report(done);
        }

        return hashes;
    }

    /// <summary>
    /// For each file: "&lt;index&gt; &lt;md5&gt;", or nothing when it can't be read. Indexes, not paths, map the answers back,
    /// so odd file names can't confuse the parse. md5sum reads stdin, so it prints only the digest and "-".
    /// </summary>
    internal static string HashScript(IReadOnlyList<string> paths, long? prefixBytes)
    {
        var hash = prefixBytes is { } p
            ? $"h=$(head -c {p.ToString(CultureInfo.InvariantCulture)} \"$f\" | md5sum)"
            : "h=$(md5sum < \"$f\")";
        var script = new StringBuilder("i=0; for f in");
        foreach (var path in paths)
        {
            script.Append(' ').Append(ShellQuote.Quote(path));
        }

        script.Append($"; do [ -r \"$f\" ] && [ -f \"$f\" ] && {hash} && echo \"$i ${{h%% *}}\"; i=$((i+1)); done 2>/dev/null; true");
        return script.ToString();
    }

    internal static IEnumerable<(int Index, string Hash)> ParseHashes(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && parts[1].Length == 32 && parts[1].All(Uri.IsHexDigit))
            {
                yield return (index, parts[1].ToLowerInvariant());
            }
        }
    }

    private static IEnumerable<List<MediaItem>> Batches(IReadOnlyList<MediaItem> items, long? prefixBytes)
    {
        var batch = new List<MediaItem>();
        long bytes = 0;
        foreach (var item in items)
        {
            var cost = prefixBytes is { } p ? Math.Min(p, item.Size) : item.Size;
            if (batch.Count > 0 && (batch.Count >= BatchFiles || bytes + cost > BatchBytes))
            {
                yield return batch;
                batch = [];
                bytes = 0;
            }

            batch.Add(item);
            bytes += cost;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>Asset ids (MediaStore _id) → their rows, read straight from MediaStore.</summary>
    private async Task<IReadOnlyList<MediaItem>> ResolveAsync(IReadOnlyList<string> assetIds, CancellationToken ct)
    {
        var ids = assetIds
            .Select(id => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n >= 0)
            .Distinct()
            .ToList();
        var items = new List<MediaItem>(ids.Count);
        foreach (var chunk in ids.Chunk(500))
        {
            var where = $"_id IN ({string.Join(',', chunk)}) AND media_type IN (1,3)";
            var result = await channel.ShellAsync(MediaQuery.Command(sdk(), where), ct).ConfigureAwait(false);
            if (!result.Succeeded && !result.Output.Contains("Row:", StringComparison.Ordinal))
            {
                throw AdbErrors.FromShell("content", result);
            }

            items.AddRange(MediaQuery.Parse(result.Output, sdk()));
        }

        var order = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
        return items.OrderBy(i => order.GetValueOrDefault(i.Id, int.MaxValue)).ToList();
    }

    public async Task<AppFileScan> FindLargeAppFilesAsync(long minBytes, int limit, CancellationToken ct)
    {
        var result = await channel.ShellAsync(FindCommand(minBytes), ct).ConfigureAwait(false);
        var files = ParseFind(result.Output, minBytes)
            .OrderByDescending(f => f.Size)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        return new AppFileScan(files, ParseUnreadable(result.Error));
    }

    /// <summary>
    /// toybox find rounds sizes up to whole KiB for -size, so ask for a little less and filter exactly afterwards.
    /// stat prints "size mtime path" for each match in a few calls (-exec … {} +).
    /// </summary>
    internal static string FindCommand(long minBytes)
    {
        var kib = Math.Max(0, minBytes / 1024 - 1);
        var roots = string.Join(' ', AppFolders.Select(f => ShellQuote.Quote(SharedRoot + f)));
        return $"find {roots} -type f -size +{kib.ToString(CultureInfo.InvariantCulture)}k -exec stat -c '%s %Y %n' {{}} +";
    }

    internal static IEnumerable<AppFileHit> ParseFind(string output, long minBytes)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var first = line.IndexOf(' ');
            var second = first < 0 ? -1 : line.IndexOf(' ', first + 1);
            if (second < 0
                || !long.TryParse(line.AsSpan(0, first), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(line.AsSpan(first + 1, second - first - 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var mtime))
            {
                continue;
            }

            var full = line[(second + 1)..];
            if (size < minBytes || !full.StartsWith(SharedRoot + "/", StringComparison.Ordinal))
            {
                continue;
            }

            var path = full[SharedRoot.Length..];
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var appId = parts is ["Android", "data" or "media", var package, _, ..] ? package : null;
            yield return new AppFileHit(
                "internal",
                path,
                parts[^1],
                size,
                mtime > 0 ? DateTimeOffset.FromUnixTimeSeconds(mtime) : null,
                appId,
                appId is null ? parts[0] : null);
        }
    }

    /// <summary>"find: /sdcard/Android/data/com.x: Permission denied" → "Android/data", once per top folder.</summary>
    internal static IReadOnlyList<string> ParseUnreadable(string error)
    {
        var folders = new List<string>();
        foreach (var raw in error.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var start = line.IndexOf(SharedRoot + "/", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var rest = line[(start + SharedRoot.Length + 1)..];
            var end = rest.IndexOfAny([':', '\'', '"']);
            var parts = (end < 0 ? rest : rest[..end]).Split('/', StringSplitOptions.RemoveEmptyEntries);
            var folder = parts is ["Android", var sub, ..] ? "Android/" + sub : parts.FirstOrDefault();
            if (folder is not null && !folders.Contains(folder, StringComparer.Ordinal))
            {
                folders.Add(folder);
            }
        }

        return folders;
    }

    public async Task<AppCacheReport> GetCachesAsync(CancellationToken ct)
    {
        var stats = DiskStatsParser.Parse(await channel.ShellCheckedAsync("dumpsys diskstats", ct).ConfigureAwait(false));
        var apps = stats.Packages
            .Where(p => p.Value.CacheBytes > 0)
            .Select(p => new AppCacheEntry(p.Key, p.Value.CacheBytes))
            .OrderByDescending(a => a.Bytes)
            .ToList();
        long? total = stats.AppCacheSize ?? (apps.Count > 0 ? apps.Sum(a => a.Bytes) : null);
        return new AppCacheReport(total, apps);
    }

    public async Task<long> TrimCachesAsync(CancellationToken ct)
    {
        var before = await FreeDataBytesAsync(ct).ConfigureAwait(false);
        await channel.ShellCheckedAsync($"pm trim-caches {TrimAll}", ct).ConfigureAwait(false);
        var after = await FreeDataBytesAsync(ct).ConfigureAwait(false);
        return Math.Max(0, after - before);
    }

    private async Task<long> FreeDataBytesAsync(CancellationToken ct)
    {
        var df = DfParser.Parse(await channel.ShellCheckedAsync("df -k /data", ct).ConfigureAwait(false));
        return df.FirstOrDefault(e => e.MountedOn == "/data")?.AvailableBytes
               ?? throw new DeviceException(DeviceErrorCodes.Protocol, "The phone didn't report its free space.");
    }
}
