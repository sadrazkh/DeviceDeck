using System.Diagnostics;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Cleanup;
using DeviceDeck.Ios.Device;

namespace IosProbe;

/// <summary>
/// `cleanup`: the clean-up analysis the app runs, read-only, with timings per section. Sizes and counts only;
/// photo and file names stay hidden unless --names is given (app names are shown).
/// </summary>
internal static class CleanupProbe
{
    public static async Task RunAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var names = cli.Has("names");
        var top = cli.IntOption("top", 10);
        var analyzer = new StorageAnalyzer(new StorageAnalyzerOptions
        {
            AppFileMinBytes = cli.IntOption("min-mb", 10) * 1_000_000L,
        });

        var clock = Stopwatch.StartNew();
        var started = TimeSpan.Zero;
        CleanupSection? section = null;
        var lastLine = TimeSpan.Zero;
        var report = await analyzer.AnalyzeAsync(device, (_, progress) =>
        {
            if (progress.Section != section)
            {
                if (section is { } done)
                    Console.Error.WriteLine($"({done}: {(clock.Elapsed - started).TotalSeconds:0.0} s)");
                section = progress.Section;
                started = clock.Elapsed;
            }
            else if (progress.Section == CleanupSection.Duplicates && progress.Total > 0 && clock.Elapsed - lastLine > TimeSpan.FromSeconds(5))
            {
                lastLine = clock.Elapsed;
                Console.Error.WriteLine($"  hashing {Out.Bytes(progress.Done)} of {Out.Bytes(progress.Total)}");
            }
        }, ct);
        if (section is { } last)
            Console.Error.WriteLine($"({last}: {(clock.Elapsed - started).TotalSeconds:0.0} s)");
        Console.Error.WriteLine($"(analysis: {clock.Elapsed.TotalSeconds:0.0} s)");

        Console.WriteLine($"Storage: {Size(report.TotalBytes)} total, {Size(report.FreeBytes)} free");

        if (report.Apps is { } apps)
        {
            Console.WriteLine();
            Console.WriteLine($"Apps: {apps.Count} user apps, {Out.Bytes(apps.TotalBytes)} (app + data)");
            Out.Table(["App", "App", "Data", "Total"], apps.Largest.Take(top).Select(a =>
                new[] { a.Name, Out.Bytes(a.AppBytes), Out.Bytes(a.DataBytes), Out.Bytes(a.AppBytes + a.DataBytes) }));
        }

        if (report.Media is { } media)
        {
            Console.WriteLine();
            Console.WriteLine($"Media: {media.VideoCount} videos {Out.Bytes(media.VideoBytes)}, {media.PhotoCount} photos {Out.Bytes(media.PhotoBytes)}, {media.CloudOnlyCount} in iCloud only");
            Console.WriteLine($"  delete from the PC: {(media.CanDelete ? "yes" : "no (copy to the PC, then delete in Photos)")}");
            Console.WriteLine("  Largest videos:");
            Out.Table(["#", "Size", "Length", "Taken", "Name"], media.LargestVideos.Take(top).Select((a, i) =>
                new[] { (i + 1).ToString(), Out.Bytes(a.Size), a.DurationSeconds is { } s ? $"{s / 60:0}:{s % 60:00}" : "-", Month(a), Name(a.FileName, names) }));
            Console.WriteLine("  Largest photos:");
            Out.Table(["#", "Size", "Kind", "Taken", "Name"], media.LargestPhotos.Take(top).Select((a, i) =>
                new[] { (i + 1).ToString(), Out.Bytes(a.Size), a.Kind.ToString(), Month(a), Name(a.FileName, names) }));
        }

        if (report.Duplicates is { } duplicates)
        {
            Console.WriteLine();
            Console.WriteLine($"Exact duplicates: {duplicates.Groups.Count} groups, {duplicates.Groups.Sum(g => g.Assets.Count - 1)} extra copies, {Out.Bytes(duplicates.ReclaimableBytes)} to free");
            Console.WriteLine($"  hashed {duplicates.HashedCount} files ({Out.Bytes(duplicates.HashedBytes)}) that shared a size with another");
            Out.Table(["#", "Size", "Copies", "Frees", "Kinds"], duplicates.Groups.Take(top).Select((g, i) =>
                new[] { (i + 1).ToString(), Out.Bytes(g.Size), g.Assets.Count.ToString(), Out.Bytes(g.ReclaimableBytes), string.Join("/", g.Assets.Select(a => a.Kind).Distinct()) }));
        }

        if (report.AppFiles is { } files)
        {
            Console.WriteLine();
            Console.WriteLine($"Big files in app documents (>= {Out.Bytes(files.MinBytes)}): {files.Files.Count} files, {Out.Bytes(files.TotalBytes)}");
            Out.Table(["App", "Files", "Size"], files.Files
                .GroupBy(f => f.AppId is { } id ? files.AppNames.GetValueOrDefault(id, id) : f.Folder ?? f.Root)
                .OrderByDescending(g => g.Sum(f => f.Size))
                .Take(top)
                .Select(g => new[] { g.Key, g.Count().ToString(), Out.Bytes(g.Sum(f => f.Size)) }));
            if (names)
                Out.Table(["Size", "Root", "Path"], files.Files.Take(top).Select(f => new[] { Out.Bytes(f.Size), f.Root, f.Path }));
        }

        if (report.Caches is { } caches)
        {
            Console.WriteLine();
            Console.WriteLine($"App caches: {Size(caches.TotalBytes)} as last counted");
        }

        foreach (var note in report.Notes)
            Console.WriteLine($"note [{note.Section}, {note.Code}]: {note.Message}");
    }

    private static string Size(long? bytes) => bytes is { } b ? Out.Bytes(b) : "unknown";

    private static string Month(PhotoAsset a) => a.TakenAt?.ToString("yyyy-MM") ?? "-";

    private static string Name(string name, bool show) => show ? name : "(hidden)";
}
