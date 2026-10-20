using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Parsing;

internal sealed record PackageSize(long AppBytes, long DataBytes, long CacheBytes);

/// <summary>What `dumpsys diskstats` reports. The category sizes come from a cache the phone refreshes about once a day while charging; null when there is none yet.</summary>
internal sealed record DiskStats(
    long? DataFreeBytes,
    long? DataTotalBytes,
    long? AppSize,
    long? AppDataSize,
    long? AppCacheSize,
    long? PhotosSize,
    long? VideosSize,
    long? AudioSize,
    long? DownloadsSize,
    long? SystemSize,
    long? OtherSize,
    IReadOnlyDictionary<string, PackageSize> Packages)
{
    public bool HasCategories => PhotosSize != null || AppSize != null || VideosSize != null;
}

/// <summary>
/// Parses `dumpsys diskstats` (DiskStatsService): "Data-Free: 47245436K / 113192328K total = 41% free", then the
/// cached "App Size: 123", "Photos Size: ...", and the JSON arrays "Package Names: [..]", "App Sizes: [..]".
/// </summary>
internal static partial class DiskStatsParser
{
    [GeneratedRegex(@"^(?<name>[A-Za-z]+)-Free:\s*(?<free>\d+)K\s*/\s*(?<total>\d+)K\s+total")]
    private static partial Regex FreeLine();

    public static DiskStats Parse(string text)
    {
        long? dataFree = null, dataTotal = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            var free = FreeLine().Match(line);
            if (free.Success)
            {
                if (free.Groups["name"].Value == "Data")
                {
                    dataFree = long.Parse(free.Groups["free"].Value, CultureInfo.InvariantCulture) * 1024;
                    dataTotal = long.Parse(free.Groups["total"].Value, CultureInfo.InvariantCulture) * 1024;
                }
                continue;
            }
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                values[line[..colon]] = line[(colon + 2)..].Trim();
            }
        }

        long? Number(string key) =>
            values.TryGetValue(key, out string? value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n >= 0 ? n : null;

        return new DiskStats(
            dataFree,
            dataTotal,
            Number("App Size"),
            Number("App Data Size"),
            Number("App Cache Size"),
            Number("Photos Size"),
            Number("Videos Size"),
            Number("Audio Size"),
            Number("Downloads Size"),
            Number("System Size"),
            Number("Other Size"),
            ParsePackages(values));
    }

    private static IReadOnlyDictionary<string, PackageSize> ParsePackages(Dictionary<string, string> values)
    {
        var result = new Dictionary<string, PackageSize>(StringComparer.Ordinal);
        string[]? names = JsonArray<string>(values, "Package Names");
        long[]? apps = JsonArray<long>(values, "App Sizes");
        long[]? data = JsonArray<long>(values, "App Data Sizes");
        long[]? caches = JsonArray<long>(values, "Cache Sizes");
        if (names == null || apps == null || apps.Length != names.Length)
        {
            return result;
        }
        for (int i = 0; i < names.Length; i++)
        {
            long dataBytes = data != null && data.Length == names.Length ? data[i] : 0;
            long cacheBytes = caches != null && caches.Length == names.Length ? caches[i] : 0;
            result[names[i]] = new PackageSize(apps[i], dataBytes, cacheBytes);
        }
        return result;
    }

    private static T[]? JsonArray<T>(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out string? json) || !json.StartsWith('['))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T[]>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
