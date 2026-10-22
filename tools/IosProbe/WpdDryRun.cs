using System.Diagnostics;
using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;

namespace IosProbe;

/// <summary>
/// `wpd-dryrun [--set largest|listed] [--items 39]` (v0.4.2, read-only): everything a delete does before its first batch
/// (the phone's side, Windows' view, kept listing, read ahead), on a read-only connection, and nothing after. Times it
/// without and with a read ahead, then checks every mapping: same name (or the phone's made-up name), same size, in the
/// folder of the month it was taken, and the bytes of a few small ones against the phone's file. Never deletes.
/// --set largest: the N largest videos (as Clean-up lists them); listed: the N largest items Windows lists by name.
/// </summary>
internal static class WpdDryRun
{
    public static async Task RunAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var wpd = WpdBackends.ForThisPc() ?? throw new CliException("Windows Portable Devices is only on Windows.");
        var count = cli.IntOption("items", 39);
        var set = cli.Option("set") ?? "largest";
        var photos = (IosPhotoLibrary)device.Photos;
        var support = await device.GetDeleteSupportAsync(recheck: true, ct);
        if (!support.CanDelete)
            throw new CliException($"The app wouldn't delete on this phone now: {support.Reason} ({support.Detail}).");
        var index = await photos.GetIndexAsync(refresh: false, ct);
        var assets = index.Assets.Where(a => !a.InCloudOnly && a.Size > 0).ToList();

        List<PhotoAsset> chosen;
        if (set == "listed")
        {
            var mine = wpd.ListDevices().Single(d => WpdPhotoDeleter.Normalize(d.UsbSerial) == WpdPhotoDeleter.Normalize(device.Udid));
            using var c = wpd.Open(mine.PnpId, readOnly: true);
            var reader = new WpdRollReader(c, new WpdReadStats(), null, ct);
            var listing = reader.Structure(null, mine.PnpId, DateTimeOffset.UtcNow, out _) ?? throw new CliException("Windows shows no camera roll.");
            reader.Load(listing, listing.Folders, fresh: true);
            var names = listing.ToDcim().Folders.SelectMany(f => f.Files).Select(o => o.FileName ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            chosen = assets.Where(a => names.Contains(a.FileName)).OrderByDescending(a => a.Size).Take(count).ToList();
        }
        else
        {
            chosen = assets.Where(a => a.Kind == PhotoKind.Video).OrderByDescending(a => a.Size).Take(count).ToList();
        }

        var items = chosen.Select(a => new PhotoDeleteItem(a.Id, a.Size)).ToList();
        Console.WriteLine($"{items.Count} items ({set}), {items.Sum(i => i.Size) / 1e9:0.00} GB, taken in {chosen.Select(a => a.TakenAt?.ToString("yyyy-MM")).Distinct().Count()} months.");
        var deleter = device.PhotoDeleter;

        // Without a read ahead: the kept listing dropped (Windows' own driver may still hold what it read this session).
        deleter.Listing.Invalidate();
        var clock = Stopwatch.StartNew();
        var cold = await deleter.DryRunAsync(items, null, ct);
        Console.WriteLine($"Without a read ahead: Delete → Deleting in {clock.Elapsed.TotalSeconds:0.0} s; Windows' view: {deleter.LastRead}");

        // With one: read ahead first (as Clean-up's analysis or Photos' selection starts it), then the delete.
        deleter.Listing.Invalidate();
        clock.Restart();
        deleter.StartPrefetch("wpd-dryrun", ct);
        if (deleter.Prefetching is { } running)
            await running.WaitAsync(ct);
        Console.WriteLine($"Read ahead: {clock.Elapsed.TotalSeconds:0.0} s; {deleter.LastPrefetch}");
        clock.Restart();
        var warm = await deleter.DryRunAsync(items, null, ct);
        Console.WriteLine($"With the read ahead done: Delete → Deleting in {clock.Elapsed.TotalSeconds:0.0} s; Windows' view: {deleter.LastRead}");

        // Every mapping checked.
        var byId = chosen.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var mapped = warm.Where(r => r.Object is not null && r.Refusal is null).ToList();
        var wrong = 0;
        foreach (var r in mapped)
        {
            var asset = byId[r.Item.Id];
            var o = r.Object!;
            var sameName = string.Equals(o.FileName, asset.FileName, StringComparison.OrdinalIgnoreCase);
            var month = WpdMonths.MonthOf(r.Folder);
            var inMonth = month is null || asset.TakenAt is null || WpdMonths.MonthsFor(asset.TakenAt.Value).Contains(month.Value);
            var ok = o.Size == r.Item.Size && inMonth && (sameName || (o.Created is { } created && asset.TakenAt is { } taken && (created - taken).Duration() <= TimeSpan.FromSeconds(1)));
            if (!ok)
            {
                wrong++;
                Console.WriteLine($"  CHECK: {Path.GetExtension(asset.FileName)} {r.Item.Size:N0} bytes → {r.Folder}, {o.Size:N0} bytes, same name {sameName}, in its month {inMonth}");
            }
        }

        Console.WriteLine($"Mapped {mapped.Count} of {items.Count} (same as without a read ahead: {cold.Count(r => r.Object is not null && r.Refusal is null) == mapped.Count}); " +
                          $"name, size and month checked: {mapped.Count - wrong} right, {wrong} wrong.");
        // Why, without names: the photo's own name is taken out of the sentence.
        foreach (var g in warm.Where(r => r.Refusal is not null).GroupBy(r => r.Refusal!.Replace(Path.GetFileName(r.Item.Id), "<name>", StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"  refused ×{g.Count()}: {g.Key}");

        // The bytes of up to 3 small mapped items, read through Windows, against the phone's file.
        foreach (var r in mapped.Where(r => r.Item.Size <= 64L * 1024 * 1024).OrderBy(r => r.Item.Size).Take(3))
        {
            var mine = wpd.ListDevices().Single(d => WpdPhotoDeleter.Normalize(d.UsbSerial) == WpdPhotoDeleter.Normalize(device.Udid));
            using var c = wpd.Open(mine.PnpId, readOnly: true);
            var viaWindows = c.ReadAll(r.Object!.Id, 64 * 1024 * 1024);
            using var phone = new MemoryStream();
            await photos.ReadOriginalAsync(r.Item.Id, phone, null, ct);
            var same = viaWindows is not null && SHA256.HashData(viaWindows).AsSpan().SequenceEqual(SHA256.HashData(phone.ToArray()));
            Console.WriteLine($"  bytes of a {r.Item.Size:N0}-byte {Path.GetExtension(r.Item.Id)}: {(same ? "same SHA-256 through Windows and on the phone" : "DIFFERENT")}");
        }

        Console.WriteLine("\nRead-only: nothing was deleted.");
    }
}
