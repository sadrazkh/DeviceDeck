using System.Diagnostics;
using System.Globalization;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;

namespace IosProbe;

/// <summary>
/// `wpd-scan` (read-only): why a phone file isn't in Windows' view. A fresh read-only connection, what the device says
/// it supports, an object's supported properties, every folder with its dates, the objects that look like the ones
/// asked for (name, size or day), and what Windows lists against the phone's DCIM, by extension and by folder.
/// </summary>
internal static class WpdScan
{
    public static async Task RunAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var wpd = WpdBackends.ForThisPc() ?? throw new CliException("Windows Portable Devices is only on Windows.");
        var nameLike = cli.Option("name-like");
        var sizes = (cli.Option("sizes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();
        DateTime? day = cli.Option("day") is { } d ? DateTime.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

        var mine = wpd.ListDevices().Where(x => WpdPhotoDeleter.Normalize(x.UsbSerial) == WpdPhotoDeleter.Normalize(device.Udid)).ToList();
        if (mine.Count != 1)
            throw new CliException($"Windows lists {mine.Count} cameras with this iPhone's USB serial.");
        using var c = wpd.Open(mine[0].PnpId, readOnly: true);

        var commands = c.SupportedCommands();
        Console.WriteLine($"Supported commands: {commands.Count}; DELETE_OBJECTS {(commands.Contains(WpdCommands.DeleteObjects) ? "yes" : "no")}");
        foreach (var g in commands.GroupBy(k => k.Category))
            Console.WriteLine($"  {g.Key}: {string.Join(",", g.Select(k => k.Id).Order())}");

        var clock = Stopwatch.StartNew();
        var dcim = WpdDcimWalker.Walk(c, ct, out var missing) ?? throw new CliException($"No camera roll in Windows' view: {missing}.");
        var all = dcim.Folders.SelectMany(f => f.Files.Select(o => (Folder: f.Name, Object: o))).ToList();
        Console.WriteLine($"\nFresh walk: {dcim.Folders.Count} folders, {all.Count} files, {clock.Elapsed.TotalSeconds:0.0} s ({(dcim.DcimId == dcim.StorageId ? "straight under the storage" : "in DCIM")}).");

        if (all.FirstOrDefault().Object is { } first)
        {
            var props = c.SupportedProperties(first.Id);
            Console.WriteLine($"Supported properties of one file ({first.FileName}): {props.Count}");
            foreach (var g in props.GroupBy(k => k.FormatId))
                Console.WriteLine($"  {g.Key}: {string.Join(",", g.Select(k => k.Pid).Order())}");
            Console.WriteLine($"  WPD_OBJECT_CAN_DELETE supported: {(props.Contains((new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 26u)) ? "yes" : "no")}");
        }

        Console.WriteLine();
        Out.Table(["folder", "files", "oldest created", "newest created"], dcim.Folders.Select(f => new[]
        {
            f.Name, f.Files.Count.ToString(), Date(f.Files.Min(o => o.Created)), Date(f.Files.Max(o => o.Created)),
        }));

        var hits = all.Where(x =>
                (nameLike is not null && (x.Object.FileName ?? "").Contains(nameLike, StringComparison.OrdinalIgnoreCase))
                || (x.Object.Size is { } s && sizes.Contains(s))
                || (day is { } dd && x.Object.Created is { } cr && cr.LocalDateTime.Date == dd.Date))
            .ToList();
        Console.WriteLine($"\nObjects like the ones asked for (name contains \"{nameLike}\", size in [{string.Join(",", sizes)}], or created on {day:yyyy-MM-dd}): {hits.Count}");
        Out.Table(["folder", "name", "bytes", "created", "can delete", "persistent id"], hits.Select(x => new[]
        {
            x.Folder, x.Object.FileName ?? "—", x.Object.Size?.ToString("N0") ?? "—", Date(x.Object.Created),
            x.Object.CanDelete switch { true => "yes", false => "no", null => "doesn't say" }, x.Object.PersistentId ?? "—",
        }));

        var newest = all.Where(x => x.Object.Created is not null).OrderByDescending(x => x.Object.Created).Take(12).ToList();
        Console.WriteLine("\nThe 12 newest objects Windows lists:");
        Out.Table(["folder", "name", "bytes", "created"], newest.Select(x => new[] { x.Folder, x.Object.FileName ?? "—", x.Object.Size?.ToString("N0") ?? "—", Date(x.Object.Created) }));

        // Against the phone's own DCIM.
        var phone = await device.UseMediaAfcAsync(afc => IosPhotoLibrary.ListDcimFilesAsync(afc, ct), ct);
        var wpdNames = all.Select(x => x.Object.FileName ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var phoneNames = phone.Select(p => p[(p.LastIndexOf('/') + 1)..]).ToList();
        string Ext(string n) => Path.GetExtension(n).ToUpperInvariant();
        Console.WriteLine($"\nPhone DCIM (AFC): {phone.Count} media files. By extension: phone / Windows / phone files Windows doesn't list:");
        Out.Table(["ext", "phone", "Windows", "missing in Windows"], phoneNames.GroupBy(Ext).OrderByDescending(g => g.Count()).Select(g => new[]
        {
            g.Key, g.Count().ToString(), all.Count(x => Ext(x.Object.FileName ?? "") == g.Key).ToString(), g.Count(n => !wpdNames.Contains(n)).ToString(),
        }));
        Console.WriteLine("\nBy phone folder: files / missing in Windows:");
        Out.Table(["folder", "files", "missing in Windows"], phone.GroupBy(p => p.Split('/')[1]).OrderBy(g => g.Key).Select(g => new[]
        {
            g.Key, g.Count().ToString(), g.Count(p => !wpdNames.Contains(p[(p.LastIndexOf('/') + 1)..])).ToString(),
        }));

        // The photo index's view of what is missing: hidden? trashed? recently added?
        var index = await ((IosPhotoLibrary)device.Photos).GetIndexAsync(refresh: false, ct);
        var missingPaths = phone.Where(p => !wpdNames.Contains(p[(p.LastIndexOf('/') + 1)..])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var listed = index.Assets.Where(a => missingPaths.Contains(a.Id)).ToList();
        Console.WriteLine($"\nOf the {missingPaths.Count} phone files Windows doesn't list: {listed.Count} are assets in the photo index " +
                          $"({listed.Count(a => index.IsHidden(a.Id))} hidden, {listed.Count(a => a.Kind == DeviceDeck.Core.Abstractions.PhotoKind.Screenshot)} screenshots, " +
                          $"{listed.Count(a => a.Kind == DeviceDeck.Core.Abstractions.PhotoKind.Video)} videos); the rest are Live Photo videos or files the index leaves out (Recently Deleted).");
        var byMonth = listed.Where(a => a.TakenAt is not null).GroupBy(a => a.TakenAt!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderByDescending(g => g.Key).Take(8);
        Console.WriteLine("  by month taken: " + string.Join(", ", byMonth.Select(g => $"{g.Key}: {g.Count()}")));

        // Given phone files: what the library says about them, and the Windows objects of the same size and extension.
        var paths = (cli.Option("paths") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length == 0)
            return;
        var rows = new Dictionary<string, PhotoRow>(StringComparer.OrdinalIgnoreCase);
        var copy = Path.Combine(((IosPhotoLibrary)device.Photos).CacheDirectory, "Photos.sqlite");
        if (File.Exists(copy))
        {
            using var db = PhotosDatabase.Open(copy);
            foreach (var r in db.ReadAssets())
                rows[r.Path] = r;
        }
        var disk = await ((IosPhotoLibrary)device.Photos).StatAsync(paths, ct);
        Console.WriteLine();
        foreach (var p in paths)
        {
            var asset = index.Assets.FirstOrDefault(a => a.Id.Equals(p, StringComparison.OrdinalIgnoreCase));
            var row = rows.GetValueOrDefault(p);
            var info = disk.GetValueOrDefault(p);
            Console.WriteLine($"{p}: file {info?.Size.ToString("N0") ?? "missing"} bytes, file created {Date(info?.Created)}, modified {Date(info?.Modified)}; " +
                              $"index: taken {asset?.TakenAt?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "—"}, size {asset?.Size.ToString("N0") ?? "—"}, kind {asset?.Kind.ToString() ?? "—"}; " +
                              $"db: pk {row?.Pk.ToString(CultureInfo.InvariantCulture) ?? "—"}, original name \"{row?.OriginalFileName}\", uuid {row?.Uuid ?? "—"}");
            var ext = Path.GetExtension(p);
            foreach (var x in all.Where(x => x.Object.Size == info?.Size && string.Equals(Path.GetExtension(x.Object.FileName), ext, StringComparison.OrdinalIgnoreCase)))
                Console.WriteLine($"    Windows: {x.Folder}/{x.Object.FileName} {x.Object.Size:N0} bytes, created {x.Object.Created?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) ?? "—"}, persistent id {x.Object.PersistentId}");
        }
    }

    private static string Date(DateTimeOffset? d) => d?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—";
}
