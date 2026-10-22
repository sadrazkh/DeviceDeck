using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Wpd;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>
/// A camera roll the size of the owner's (v0.4.0 log, iPhone 16, iOS 27): 3,258 files the phone keeps in 100APPLE…103APPLE,
/// shown to Windows as 18 month folders ("202505_a" … "202610_a") straight under Internal Storage, about 181 per month.
/// Every tenth file is a video. Sizes and dates come from a fixed seed, so every run builds the same roll.
/// </summary>
internal sealed class BigCameraRoll
{
    public const int Files = 3_258;
    public const int Months = 18;

    public sealed record Entry(string Path, string Name, long Size, DateTimeOffset Taken, string Month, bool Video);

    public FakeWpdDevice Device { get; }
    public IReadOnlyList<Entry> Entries { get; }

    /// <summary>Windows' month folder name → its object id.</summary>
    public IReadOnlyDictionary<string, string> MonthFolders { get; }

    private BigCameraRoll(FakeWpdDevice device, IReadOnlyList<Entry> entries, IReadOnlyDictionary<string, string> months)
    {
        Device = device;
        Entries = entries;
        MonthFolders = months;
    }

    /// <param name="monthOf">Which months the files fall in (index 0…17); by default spread evenly, 181 each.</param>
    public static BigCameraRoll Build(FakeWpd wpd, FakeCameraRoll roll, string udid, Func<int, int>? monthOf = null)
    {
        var random = new Random(4_3_2026);
        var first = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = new List<Entry>(Files);
        for (var i = 0; i < Files; i++)
        {
            var month = monthOf?.Invoke(i) ?? i * Months / Files;
            var start = first.AddMonths(month);
            var days = (start.AddMonths(1) - start).TotalDays;
            // Away from the month's edges unless a test asks for an edge (see AtMonthEdge).
            var taken = start.AddDays(1 + random.NextDouble() * (days - 2));
            var video = i % 10 == 9;
            var name = $"IMG_{i + 1:0000}.{(video ? "MOV" : "HEIC")}";
            var folder = $"{100 + i / 1000}APPLE";
            var size = video ? 20_000_000L + random.NextInt64(400_000_000) : 1_500_000L + random.NextInt64(3_000_000);
            entries.Add(new Entry($"DCIM/{folder}/{name}", name, size, taken, MonthName(start), video));
        }

        return Place(wpd, roll, udid, entries);
    }

    /// <summary>Moves one file to just after midnight UTC on the first of its month (a timezone edge) and rebuilds nothing else.</summary>
    public static Entry AtMonthEdge(Entry e)
    {
        var start = new DateTimeOffset(e.Taken.Year, e.Taken.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return e with { Taken = start.AddHours(2) };
    }

    public static BigCameraRoll Place(FakeWpd wpd, FakeCameraRoll roll, string udid, IReadOnlyList<Entry> entries)
    {
        foreach (var e in entries)
        {
            roll.Add(e.Path, e.Size);
            roll.Taken[e.Path] = e.Taken;
        }

        var device = wpd.AddIphone(udid);
        device.Commands.Add(WpdCommands.DeleteObjects);
        var storage = device.AddStorage();
        var months = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries.OrderBy(e => e.Month, StringComparer.Ordinal))
        {
            if (!months.TryGetValue(e.Month, out var folder))
            {
                folder = months[e.Month] = device.AddFolder(storage, e.Month);
            }

            // iOS 27 doesn't say whether a file can be deleted; Windows shows the time taken in the PC's zone.
            device.AddFile(folder, e.Name, e.Size, canDelete: null, e.Path, e.Taken.ToOffset(TimeSpan.FromHours(3.5)));
        }

        device.Deleted = (_, path) => roll.Disk.Remove(path);
        return new BigCameraRoll(device, entries, months);
    }

    /// <summary>"202610_a": the month Windows files a picture under (the phone's own clock).</summary>
    public static string MonthName(DateTimeOffset monthStart) => $"{monthStart:yyyyMM}_a";

    /// <summary>The <paramref name="count"/> largest videos, as Clean-up's "largest videos" lists them.</summary>
    public IReadOnlyList<PhotoDeleteItem> LargestVideos(int count) =>
        Entries.Where(e => e.Video).OrderByDescending(e => e.Size).Take(count).Select(e => new PhotoDeleteItem(e.Path, e.Size)).ToList();

    public int MonthsOf(IEnumerable<PhotoDeleteItem> items)
    {
        var paths = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        return Entries.Where(e => paths.Contains(e.Path)).Select(e => e.Month).Distinct().Count();
    }
}
