using System.Diagnostics;
using System.Globalization;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;

namespace IosProbe;

/// <summary>
/// `wpd-timing` (v0.4.1, read-only): how long a delete's reading of Windows' view of the camera roll takes on this
/// phone. The device is opened for reading only; nothing is deleted. Prints counts and times, never photo names.
/// <list type="number">
/// <item>the storage and its camera-roll folders;</item>
/// <item>the folders the N largest videos can be in, read as a delete reads them now (bulk where the driver can), and how
/// many of them map to an object;</item>
/// <item>those objects read again, as a second delete within five minutes does with the kept listing;</item>
/// <item>objects of a folder not read yet, one at a time and then in bulk (do bulk reads work on this phone, and are they faster?);</item>
/// <item>with --full, every folder read as v0.4.0 did (one by one) and as now.</item>
/// </list>
/// </summary>
internal static class WpdTiming
{
    public static async Task RunAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var wpd = WpdBackends.ForThisPc() ?? throw new CliException("Windows Portable Devices is only on Windows.");
        var itemCount = cli.IntOption("items", 39);
        var sample = cli.IntOption("count", 200);
        var mine = wpd.ListDevices().Where(d => WpdPhotoDeleter.Normalize(d.UsbSerial) == WpdPhotoDeleter.Normalize(device.Udid)).ToList();
        if (mine.Count != 1)
            throw new CliException($"Windows lists {mine.Count} cameras with this iPhone's USB serial.");
        var pnpId = mine[0].PnpId;

        // The phone's own side, as a delete reads it first.
        var clock = Stopwatch.StartNew();
        var photos = (IosPhotoLibrary)device.Photos;
        var index = await photos.GetIndexAsync(refresh: false, ct);
        var phoneFiles = await device.UseMediaAfcAsync(afc => IosPhotoLibrary.ListDcimFilesAsync(afc, ct), ct);
        Console.WriteLine($"Phone side: {index.Assets.Count:N0} assets ({index.Mode}), {phoneFiles.Count:N0} files in DCIM: {Secs(clock)}");

        using var c = wpd.Open(pnpId, readOnly: true);
        clock.Restart();
        var stats = new WpdReadStats();
        var reader = new WpdRollReader(c, stats, null, ct);
        var listing = reader.Structure(null, pnpId, DateTimeOffset.UtcNow, out var missing)
                      ?? throw new CliException($"Windows shows no camera roll: {missing}.");
        var names = listing.Folders.Select(f => f.FileName ?? "").ToList();
        var byFolder = WpdDcimMap.ShowsPhoneFolders(names, phoneFiles);
        Console.WriteLine($"1. Storage and {listing.Folders.Count} folders ({(byFolder ? "the phone's own folder names" : "month folders")}, " +
                          $"{names.Count(n => WpdMonths.MonthOf(n) is not null)} named after a month): {Secs(clock)}");

        // 2. The N largest videos, as Clean-up › Largest videos lists them.
        var videos = index.Assets.Where(a => a.Kind == PhotoKind.Video && !a.InCloudOnly && a.Size > 0).OrderByDescending(a => a.Size).Take(itemCount).ToList();
        var wanted = WpdMonths.FoldersFor(listing.Folders, byFolder, videos.Select(v => (v.Id, (string?)null, v.TakenAt)).ToList());
        var months = videos.Where(v => v.TakenAt is not null).Select(v => v.TakenAt!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture)).Distinct().Count();
        clock.Restart();
        reader.Load(listing, wanted, fresh: true);
        var cold = clock.Elapsed;
        var phoneAssets = index.Mode == PhotoIndexMode.Database
            ? index.Assets.Where(a => a.Size > 0).Select(a => new WpdPhoneAsset(a.Id, a.Size, a.TakenAt)).ToList()
            : null;
        var map = new WpdDcimMap(listing.ToDcim(), phoneFiles, phoneAssets, names);
        var found = videos.Select(v => map.Find(v.Id, v.Size, v.TakenAt)).ToList();
        Console.WriteLine($"2. The {videos.Count} largest videos ({months} months): {wanted.Count} of {listing.Folders.Count} folders, " +
                          $"{listing.FileCount:N0} objects read in {cold.TotalSeconds:0.0} s ({PerObject(cold, listing.FileCount)}); {stats}");
        Console.WriteLine($"   mapped: {found.Count(f => f.Ok)} of {videos.Count}" +
                          (found.Any(f => !f.Ok) ? "; not: " + string.Join(", ", found.Where(f => !f.Ok).GroupBy(f => f.Problem).Select(g => $"{g.Key} ×{g.Count()}")) : "") +
                          (found.Any(f => f.Problem is WpdMapProblem.Absent or WpdMapProblem.NoFolder) ? " (a delete would walk every folder then)" : ""));

        // 3. A second delete within five minutes: only the objects it uses are read again.
        var used = found.Where(f => f.Ok).Select(f => f.Object!.Id).ToList();
        clock.Restart();
        var again = reader.ReadAgain(used);
        Console.WriteLine($"3. The same {used.Count} objects read again (the kept listing): {Secs(clock)}, {again.Count} still there");

        // 4. One at a time against bulk, on objects not read yet (the driver may keep what it read once).
        var untouched = listing.Folders.FirstOrDefault(f => !listing.Has(f.Id)) ?? listing.Folders[^1];
        var ids = c.ChildIds(untouched.Id, ct).Take(sample * 2).ToList();
        var half = ids.Count / 2;
        clock.Restart();
        foreach (var id in ids.Take(half))
        {
            ct.ThrowIfCancellationRequested();
            c.Get(id);
        }

        var oneByOne = clock.Elapsed;
        clock.Restart();
        var bulk = c.GetMany(ids.Skip(half).ToList(), null, ct);
        var bulkTime = clock.Elapsed;
        clock.Restart();
        var bulkAgain = c.GetMany(ids.Take(half).ToList(), null, ct);
        var bulkAgainTime = clock.Elapsed;
        Console.WriteLine($"4. Folder {(listing.Has(untouched.Id) ? "(already read) " : "")}with {ids.Count} objects sampled:");
        Console.WriteLine($"   one at a time: {half} objects in {oneByOne.TotalSeconds:0.00} s ({PerObject(oneByOne, half)})");
        Console.WriteLine(bulk is null
            ? "   in bulk: not supported by this driver (IPortableDevicePropertiesBulk); deletes read one at a time"
            : $"   in bulk: {bulk.Count} of {ids.Count - half} other objects answered in {bulkTime.TotalSeconds:0.00} s ({PerObject(bulkTime, ids.Count - half)}); " +
              $"the first {half} again in bulk: {bulkAgainTime.TotalSeconds:0.00} s ({bulkAgain?.Count ?? 0} answered)");

        if (!cli.Has("full"))
        {
            Console.WriteLine("\nRead-only: nothing was deleted. Add --full to time every folder too.");
            return;
        }

        // 5. Every folder: v0.4.0's walk (one object at a time) and now.
        using var fresh = wpd.Open(pnpId, readOnly: true);
        clock.Restart();
        var old = WpdDcimWalker.Walk(fresh, ct, out _);
        Console.WriteLine($"5. Every folder one at a time (v0.4.0): {old?.Folders.Sum(f => f.Files.Count):N0} objects in {Secs(clock)}");
        using var fresh2 = wpd.Open(pnpId, readOnly: true);
        var fullStats = new WpdReadStats();
        var fullReader = new WpdRollReader(fresh2, fullStats, null, ct);
        clock.Restart();
        var full = fullReader.Structure(null, pnpId, DateTimeOffset.UtcNow, out _)!;
        fullReader.Load(full, full.Folders, fresh: true);
        Console.WriteLine($"   every folder now: {full.FileCount:N0} objects in {Secs(clock)}; {fullStats}");
        Console.WriteLine("\nRead-only: nothing was deleted.");
    }

    private static string Secs(Stopwatch clock) => clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string PerObject(TimeSpan time, int count) =>
        count == 0 ? "—" : (time.TotalMilliseconds / count).ToString("0.0", CultureInfo.InvariantCulture) + " ms an object";
}
