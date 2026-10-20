using System.Globalization;

namespace DeviceDeck.Ios.Protocol.Afc;

/// <summary>GetFileInfo reply: st_size, st_ifmt (S_IFREG / S_IFDIR / S_IFLNK), st_mtime and st_birthtime in nanoseconds.</summary>
public sealed record AfcFileInfo(long Size, string Type, DateTimeOffset? Modified, DateTimeOffset? Created, string? LinkTarget, IReadOnlyDictionary<string, string> Raw)
{
    public bool IsDirectory => Type == "S_IFDIR";
    public bool IsSymlink => Type == "S_IFLNK";

    public static AfcFileInfo FromPairs(IReadOnlyDictionary<string, string> pairs) => new(
        ParseLong(pairs, "st_size") ?? 0,
        pairs.TryGetValue("st_ifmt", out var t) ? t : "S_IFREG",
        FromNanoseconds(ParseLong(pairs, "st_mtime")),
        FromNanoseconds(ParseLong(pairs, "st_birthtime")),
        pairs.TryGetValue("LinkTarget", out var link) ? link : null,
        pairs);

    private static long? ParseLong(IReadOnlyDictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTimeOffset? FromNanoseconds(long? ns) =>
        ns is > 0 ? DateTimeOffset.UnixEpoch.AddTicks(ns.Value / 100) : null;
}
