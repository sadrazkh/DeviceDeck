using System.Text.RegularExpressions;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Info;

/// <summary>An SD card or USB drive mounted under /storage/&lt;id&gt;.</summary>
internal sealed record RemovableVolume(string Id, bool IsUsb)
{
    public string Key => "sd:" + Id;
    public string Label => IsUsb ? "USB drive" : "SD card";
    public string MountPath => "/storage/" + Id;
}

/// <summary>Turns df + diskstats into <see cref="StorageInfo"/> the way Android's Settings app presents it.</summary>
internal static partial class StorageBuilder
{
    /// <summary>One round trip: volumes, sizes and the cached category sizes.</summary>
    public static string Script() => ShellScript.Build(
        ("storage", "ls -1 /storage"),
        ("volumes", "sm list-volumes public"),
        ("df", "df -k /data /storage/*"),
        ("diskstats", "dumpsys diskstats"));

    [GeneratedRegex(@"^public:(?<major>\d+),\d+\s+\S+\s+(?<uuid>\S+)")]
    private static partial Regex PublicVolume();

    /// <summary>
    /// Entries of `ls /storage` other than emulated/self are removable volumes. `sm list-volumes public`
    /// ("public:179,1 mounted 1234-ABCD") tells SD (mmc, major 179) from USB (sd, major 8).
    /// </summary>
    public static IReadOnlyList<RemovableVolume> ParseVolumes(string? storageListing, string? smVolumes)
    {
        var usb = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in (smVolumes ?? "").Split('\n'))
        {
            var match = PublicVolume().Match(line.Trim());
            if (match.Success && match.Groups["major"].Value == "8")
            {
                usb.Add(match.Groups["uuid"].Value);
            }
        }
        var volumes = new List<RemovableVolume>();
        foreach (string raw in (storageListing ?? "").Split('\n'))
        {
            string id = raw.Trim().TrimEnd('/');
            if (id.Length == 0 || id is "emulated" or "self" || id.Contains(' ') || id.Contains(':'))
            {
                continue;
            }
            volumes.Add(new RemovableVolume(id, usb.Contains(id)));
        }
        return volumes;
    }

    public static StorageInfo Build(IReadOnlyList<DfEntry> df, DiskStats? stats, IReadOnlyList<RemovableVolume> removable)
    {
        var volumes = new List<StorageVolume>();
        var data = df.FirstOrDefault(e => e.MountedOn == "/data");
        long dataTotal = data?.TotalBytes ?? stats?.DataTotalBytes ?? 0;
        long dataFree = data?.AvailableBytes ?? stats?.DataFreeBytes ?? 0;
        if (dataTotal <= 0)
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, "The phone didn't report its storage size.");
        }

        // Settings shows the advertised size (128 GB), not the /data partition (~110 GB); the gap is the OS.
        long total = RoundStorageSize(dataTotal);
        volumes.Add(new StorageVolume("internal", "Internal storage", total, dataFree, Categories(total, dataTotal, dataFree, stats)));

        foreach (var volume in removable)
        {
            var entry = df.FirstOrDefault(e => e.MountedOn == volume.MountPath);
            if (entry != null && entry.TotalBytes > 0)
            {
                volumes.Add(new StorageVolume(volume.Key, volume.Label, entry.TotalBytes, entry.AvailableBytes, []));
            }
        }
        return new StorageInfo(volumes);
    }

    private static IReadOnlyList<StorageCategory> Categories(long total, long dataTotal, long dataFree, DiskStats? stats)
    {
        if (stats is not { HasCategories: true })
        {
            return []; // the phone hasn't computed them yet (it does so about daily while charging)
        }
        long used = Math.Max(0, total - dataFree);
        long photos = stats.PhotosSize ?? 0;
        long videos = stats.VideosSize ?? 0;
        long apps = (stats.AppSize ?? 0) + (stats.AppDataSize ?? 0) + (stats.AppCacheSize ?? 0);
        long media = (stats.AudioSize ?? 0) + (stats.DownloadsSize ?? 0);
        long system = Math.Max(0, total - dataTotal);

        // The cache can be a day old; never let the parts add up to more than what's used.
        long known = photos + videos + apps + media + system;
        if (known > used && known > 0)
        {
            double scale = (double)used / known;
            photos = (long)(photos * scale);
            videos = (long)(videos * scale);
            apps = (long)(apps * scale);
            media = (long)(media * scale);
            system = (long)(system * scale);
            known = photos + videos + apps + media + system;
        }
        long other = Math.Max(0, used - known);
        return
        [
            new("photos", photos),
            new("videos", videos),
            new("apps", apps),
            new("media", media),
            new("system", system),
            new("other", other),
        ];
    }

    /// <summary>android.os.FileUtils.roundStorageSize: the next 1, 2, 4 … 512 × 1000^n at or above the size.</summary>
    public static long RoundStorageSize(long size)
    {
        long value = 1;
        long power = 1;
        while (value * power < size)
        {
            value <<= 1;
            if (value > 512)
            {
                value = 1;
                power *= 1000;
            }
        }
        return value * power;
    }
}
