using System.Diagnostics;
using System.Globalization;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;
using Microsoft.Data.Sqlite;

namespace IosProbe;

/// <summary>
/// `wpd-dates [--per 25]` (read-only): which date Windows' month folders ("202610_a") follow. Samples objects in every
/// folder, matches them to the phone's files by name, and compares the folder's month with each date the phone and
/// Windows give: Photos.sqlite's ZDATECREATED, ZADDEDDATE and ZMODIFICATIONDATE, the DCIM file's times over AFC, and every
/// date property Windows reports for the object. Prints counts and dates, never photo names.
/// </summary>
internal static class WpdDates
{
    private static readonly DateTimeOffset CoreDataEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Row(DateTimeOffset? Created, DateTimeOffset? Added, DateTimeOffset? Modified, int Kind);

    public static async Task RunAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var wpd = WpdBackends.ForThisPc() ?? throw new CliException("Windows Portable Devices is only on Windows.");
        var per = cli.IntOption("per", 25);
        var mine = wpd.ListDevices().Where(d => WpdPhotoDeleter.Normalize(d.UsbSerial) == WpdPhotoDeleter.Normalize(device.Udid)).ToList();
        if (mine.Count != 1)
            throw new CliException($"Windows lists {mine.Count} cameras with this iPhone's USB serial.");

        var clock = Stopwatch.StartNew();
        var photos = (IosPhotoLibrary)device.Photos;
        var index = await photos.GetIndexAsync(refresh: false, ct);
        var phoneFiles = await device.UseMediaAfcAsync(afc => IosPhotoLibrary.ListDcimFilesAsync(afc, ct), ct);
        var rows = ReadRows(Path.Combine(photos.CacheDirectory, "Photos.sqlite"));
        var byName = phoneFiles.GroupBy(f => f[(f.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"Phone: {index.Assets.Count:N0} assets, {phoneFiles.Count:N0} DCIM files ({byName.Count:N0} with a unique name), {rows.Count:N0} Photos.sqlite rows: {clock.Elapsed.TotalSeconds:0.0} s");

        using var c = wpd.Open(mine[0].PnpId, readOnly: true);
        clock.Restart();
        var listing = new WpdRollReader(c, new WpdReadStats(), null, ct).Structure(null, mine[0].PnpId, DateTimeOffset.UtcNow, out var missing)
                      ?? throw new CliException($"Windows shows no camera roll: {missing}.");
        var children = listing.Folders.ToDictionary(f => f.Id, f => c.ChildIds(f.Id, ct), StringComparer.Ordinal);
        Console.WriteLine($"Windows: {listing.Folders.Count} folders, {children.Values.Sum(x => x.Count):N0} objects listed: {clock.Elapsed.TotalSeconds:0.0} s");

        // A spread of objects in every folder, with every property Windows gives.
        clock.Restart();
        var samples = new List<(WpdObject Folder, IReadOnlyList<(Guid FormatId, uint Pid, object? Value)> Props)>();
        foreach (var folder in listing.Folders)
        {
            var ids = children[folder.Id];
            var step = Math.Max(1, ids.Count / Math.Max(1, per));
            for (var i = 0; i < ids.Count && samples.Count(s => s.Folder == folder) < per; i += step)
            {
                ct.ThrowIfCancellationRequested();
                samples.Add((folder, c.GetAll(ids[i])));
            }
        }

        Console.WriteLine($"Sampled {samples.Count} objects (all properties): {clock.Elapsed.TotalSeconds:0.0} s ({clock.Elapsed.TotalMilliseconds / Math.Max(1, samples.Count):0} ms each)");
        var name = new PropertyKeyId(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 12);
        var objectName = new PropertyKeyId(new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 4);
        var dateKeys = samples.SelectMany(s => s.Props).Where(p => p.Value is DateTime).Select(p => new PropertyKeyId(p.FormatId, p.Pid)).Distinct().ToList();
        Console.WriteLine("Date properties Windows reports: " + string.Join(", ", dateKeys.Select(k => k.ToString())));

        // Matched to the phone's file by name, then its dates.
        var matched = new List<(string Folder, string Path, IReadOnlyList<(Guid, uint, object?)> Props)>();
        foreach (var (folder, props) in samples)
        {
            var file = Value(props, name) as string ?? Value(props, objectName) as string;
            if (file is not null && byName.TryGetValue(file, out var path))
                matched.Add((folder.FileName ?? "", path, props));
        }

        var stats = await photos.StatAsync(matched.Select(m => m.Path).ToList(), ct);
        Console.WriteLine($"Matched to a phone file by name: {matched.Count} of {samples.Count} (the rest have names of the phone's making, or the name isn't unique)");

        var sources = new List<(string Label, Func<(string Folder, string Path, IReadOnlyList<(Guid, uint, object?)> Props), DateTimeOffset?> Date, bool Utc)>
        {
            ("ZDATECREATED", m => rows.GetValueOrDefault(m.Path)?.Created, true),
            ("ZADDEDDATE", m => rows.GetValueOrDefault(m.Path)?.Added, true),
            ("ZMODIFICATIONDATE", m => rows.GetValueOrDefault(m.Path)?.Modified, true),
            ("AFC file created", m => stats.GetValueOrDefault(m.Path)?.Created, true),
            ("AFC file modified", m => stats.GetValueOrDefault(m.Path)?.Modified, true),
        };
        foreach (var key in dateKeys)
            sources.Add(($"WPD {key}", m => Value(m.Props, key) is DateTime d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero) : null, false));

        Console.WriteLine();
        Out.Table(["date", "known", "same month (UTC / as stated)", "same month (PC's zone)", "within ±14 h", "another month (examples: folder ← date)"],
            sources.Select(source =>
            {
                var known = matched.Select(m => (m.Folder, Date: source.Date(m))).Where(x => x.Date is not null && WpdMonths.MonthOf(x.Folder) is not null).ToList();
                bool Same((int, int)? folder, DateTimeOffset d) => folder == (d.Year, d.Month);
                var utc = known.Count(x => Same(WpdMonths.MonthOf(x.Folder), source.Utc ? x.Date!.Value.ToUniversalTime() : x.Date!.Value));
                var local = known.Count(x => Same(WpdMonths.MonthOf(x.Folder), source.Utc ? x.Date!.Value.ToLocalTime() : x.Date!.Value));
                var window = known.Count(x => WpdMonths.MonthsFor(x.Date!.Value).Contains(WpdMonths.MonthOf(x.Folder)!.Value));
                var off = known.Where(x => !WpdMonths.MonthsFor(x.Date!.Value).Contains(WpdMonths.MonthOf(x.Folder)!.Value))
                    .Take(4).Select(x => $"{x.Folder} ← {x.Date!.Value:yyyy-MM-dd}");
                return new[] { source.Label, known.Count.ToString(), Pct(utc, known.Count), Pct(local, known.Count), Pct(window, known.Count), string.Join("; ", off) };
            }));

        // The folders, their size, and the spread of the best-fitting dates in each (what _a / _b means).
        Console.WriteLine();
        Out.Table(["folder", "objects", "sampled", "matched", "ZDATECREATED range", "ZADDEDDATE range"], listing.Folders.Select(f =>
        {
            var inFolder = matched.Where(m => m.Folder == f.FileName).ToList();
            string Range(Func<Row, DateTimeOffset?> pick)
            {
                var dates = inFolder.Select(m => rows.GetValueOrDefault(m.Path)).Where(r => r is not null).Select(r => pick(r!)).Where(d => d is not null).Select(d => d!.Value).ToList();
                return dates.Count == 0 ? "—" : $"{dates.Min():yyyy-MM-dd} … {dates.Max():yyyy-MM-dd}";
            }

            return new[] { f.FileName ?? "", children[f.Id].Count.ToString(), samples.Count(s => s.Folder == f).ToString(), inFolder.Count.ToString(), Range(r => r.Created), Range(r => r.Added) };
        }));

        // The 39 largest videos: which months each date puts them in.
        var videos = index.Assets.Where(a => a.Kind == DeviceDeck.Core.Abstractions.PhotoKind.Video && !a.InCloudOnly && a.Size > 0).OrderByDescending(a => a.Size).Take(39).ToList();
        string Months(Func<Row, DateTimeOffset?> pick) => string.Join(" ", videos.Select(v => rows.GetValueOrDefault(v.Id)).Where(r => r is not null).Select(r => pick(r!))
            .Where(d => d is not null).GroupBy(d => d!.Value.ToUniversalTime().ToString("yyyyMM", CultureInfo.InvariantCulture)).OrderBy(g => g.Key).Select(g => $"{g.Key}×{g.Count()}"));
        Console.WriteLine($"\nThe 39 largest videos by ZDATECREATED: {Months(r => r.Created)}");
        Console.WriteLine($"                  by ZADDEDDATE:   {Months(r => r.Added)}");
        Console.WriteLine($"Windows' month folders: {string.Join(" ", listing.Folders.Select(f => f.FileName))}");

        if (cli.Has("full"))
        {
            // Every object (Windows keeps what it read once, so this is slow only the first time), then where the
            // phone's files are in Windows' view, by when they were added.
            clock.Restart();
            var reader = new WpdRollReader(c, new WpdReadStats(), null, ct);
            reader.Load(listing, listing.Folders, fresh: true);
            var dcim = listing.ToDcim();
            var objects = dcim.Folders.SelectMany(f => f.Files.Select(o => (Folder: f.Name, Object: o))).ToList();
            Console.WriteLine($"\nEvery object read: {objects.Count:N0} in {clock.Elapsed.TotalSeconds:0.0} s");
            var wpdNames = objects.GroupBy(x => x.Object.FileName ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var assets = index.Assets.Where(a => !a.InCloudOnly).ToList();
            var listed = assets.Where(a => wpdNames.ContainsKey(a.FileName)).ToList();
            Console.WriteLine($"Assets on the phone: {assets.Count:N0}; with an object of the same name in Windows' view: {listed.Count:N0}");
            Out.Table(["added (UTC month)", "assets", "listed by Windows", "in the folder of ZDATECREATED", "in the folder of ZADDEDDATE"],
                assets.GroupBy(a => rows.GetValueOrDefault(a.Id)?.Added?.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture) ?? "—")
                    .OrderBy(g => g.Key).Select(g =>
                    {
                        var inView = g.Where(a => wpdNames.ContainsKey(a.FileName)).ToList();
                        int InFolderOf(Func<Row, DateTimeOffset?> pick) => inView.Count(a => rows.GetValueOrDefault(a.Id) is { } r && pick(r) is { } d
                            && wpdNames[a.FileName].Any(x => WpdMonths.MonthOf(x.Folder) is { } m && WpdMonths.MonthsFor(d).Contains(m)));
                        return new[] { g.Key, g.Count().ToString(), inView.Count.ToString(), InFolderOf(r => r.Created).ToString(), InFolderOf(r => r.Added).ToString() };
                    }));
            var inViewVideos = videos.Where(v => wpdNames.ContainsKey(v.FileName)).ToList();
            Console.WriteLine($"The 39 largest videos: {inViewVideos.Count} listed by Windows (by name); folders: " +
                              string.Join(" ", inViewVideos.Select(v => wpdNames[v.FileName][0].Folder + "←" +
                                  (rows.GetValueOrDefault(v.Id)?.Created?.ToUniversalTime().ToString("yyyyMM", CultureInfo.InvariantCulture) ?? "?") + "/" +
                                  (rows.GetValueOrDefault(v.Id)?.Added?.ToUniversalTime().ToString("yyyyMM", CultureInfo.InvariantCulture) ?? "?"))));
            var bySize = videos.Where(v => !wpdNames.ContainsKey(v.FileName)).Count(v => objects.Any(x => x.Object.Size == v.Size));
            Console.WriteLine($"  not listed by name: {videos.Count - inViewVideos.Count}, of which an object of the same size exists: {bySize}");

            // What Windows lists that isn't a phone file by name, by folder and by the shape of the name (never the name).
            var phoneNames = phoneFiles.Select(f => f[(f.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            static string Shape(string? n) => n is null ? "—" : string.Concat(Path.GetFileNameWithoutExtension(n).Select(ch => char.IsAsciiDigit(ch) ? '9' : char.IsAsciiLetter(ch) ? 'A' : ch))
                                                               + Path.GetExtension(n).ToUpperInvariant();
            Console.WriteLine();
            Out.Table(["folder", "objects", "phone names", "other names (shape ×count)"], dcim.Folders.Select(f => new[]
            {
                f.Name, f.Files.Count.ToString(), f.Files.Count(o => phoneNames.Contains(o.FileName ?? "")).ToString(),
                string.Join(" ", f.Files.Where(o => !phoneNames.Contains(o.FileName ?? "")).GroupBy(o => Shape(o.FileName)).OrderByDescending(g => g.Count()).Take(5).Select(g => $"{g.Key}×{g.Count()}")),
            }));
            var dated = objects.Where(x => x.Object.Created is not null && WpdMonths.MonthOf(x.Folder) is not null).ToList();
            var sameMonth = dated.Count(x => WpdMonths.MonthOf(x.Folder) == (x.Object.Created!.Value.Year, x.Object.Created.Value.Month));
            Console.WriteLine();
            Console.WriteLine($"Every object: its own creation date (as Windows states it) is in its folder's month for {sameMonth:N0} of {dated.Count:N0} " +
                              $"({objects.Count - dated.Count} without a date); phone DCIM files Windows lists by name: {phoneFiles.Count(f => wpdNames.ContainsKey(f[(f.LastIndexOf('/') + 1)..])):N0} of {phoneFiles.Count:N0}");
            foreach (var f in dcim.Folders.Where(f => f.Files.Count <= 3))
                foreach (var o in f.Files)
                    Console.WriteLine($"  {f.Name}: {Shape(o.FileName)} {o.Size:N0} bytes, created {o.Created:yyyy-MM-dd HH:mm}, phone file: {(phoneNames.Contains(o.FileName ?? "") ? "yes" : "no")}");
        }

        Console.WriteLine("\nRead-only: nothing was changed.");
    }

    private readonly record struct PropertyKeyId(Guid FormatId, uint Pid)
    {
        public override string ToString() => $"{FormatId.ToString()[..8]}…/{Pid}";
    }

    private static object? Value(IReadOnlyList<(Guid FormatId, uint Pid, object? Value)> props, PropertyKeyId key) =>
        props.FirstOrDefault(p => p.FormatId == key.FormatId && p.Pid == key.Pid).Value;

    private static string Pct(int n, int of) => of == 0 ? "—" : $"{n} ({100.0 * n / of:0}%)";

    private static Dictionary<string, Row> ReadRows(string path)
    {
        var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return rows;
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT ZDIRECTORY, ZFILENAME, ZDATECREATED, ZADDEDDATE, ZMODIFICATIONDATE, ZKIND FROM ZASSET";
        using var r = cmd.ExecuteReader();
        DateTimeOffset? At(int i) => r.IsDBNull(i) ? null : CoreDataEpoch.AddSeconds(r.GetDouble(i));
        while (r.Read())
        {
            if (r.IsDBNull(0) || r.IsDBNull(1))
                continue;
            rows[r.GetString(0).TrimEnd('/') + "/" + r.GetString(1)] = new Row(At(2), At(3), At(4), r.IsDBNull(5) ? -1 : r.GetInt32(5));
        }

        return rows;
    }
}
