using System.Diagnostics;
using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;

namespace IosProbe;

/// <summary>
/// Round 4 (photodelete): what Windows' camera connection (WPD) shows of the iPhone, read-only, and finding given
/// pictures on the phone by their content; `photodelete --delete --yes` then deletes exactly those, through the
/// app's own delete. Photo names are printed only for the files being looked for.
/// </summary>
internal static class PhotoDeleteProbe
{
    // ---------- wpd-devices: no phone needed ----------

    /// <summary>Every portable device Windows lists, with what WPD says about the Apple ones (opened read-only).</summary>
    public static Task<int> DevicesAsync(CancellationToken ct)
    {
        var wpd = WpdBackends.ForThisPc() ?? throw new CliException("Windows Portable Devices is only on Windows.");
        var clock = Stopwatch.StartNew();
        var devices = wpd.ListDevices();
        Console.WriteLine($"Windows lists {devices.Count} portable device(s) ({clock.ElapsedMilliseconds} ms):");
        foreach (var d in devices)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine();
            Out.Pairs([
                ("Name", d.FriendlyName ?? "—"),
                ("Manufacturer", d.Manufacturer ?? "—"),
                ("Description", d.Description ?? "—"),
                ("PnP id", d.PnpId),
                ("USB serial", d.UsbSerial ?? "—"),
            ]);
            if (!d.PnpId.Contains("vid_05ac", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var c = wpd.Open(d.PnpId, readOnly: true);
                var p = c.Device;
                Out.Pairs([
                    ("WPD name", p.FriendlyName ?? "—"),
                    ("WPD model", p.Model ?? "—"),
                    ("WPD serial", p.SerialNumber ?? "—"),
                    ("WPD protocol", p.Protocol ?? "—"),
                    ("WPD firmware", p.Firmware ?? "—"),
                ]);
            }
            catch (WpdException ex)
            {
                Console.WriteLine($"  (couldn't open it: {ex.Message})");
            }
        }

        return Task.FromResult(0);
    }

    // ---------- wpd: the connected iPhone's camera roll as Windows shows it ----------

    public static async Task WpdAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var wpd = WpdBackends.ForThisPc() ?? throw new CliException("Windows Portable Devices is only on Windows.");
        Console.WriteLine($"iPhone {device.Udid} ({device.Summary.Name}, iOS {device.Summary.OsVersion})");

        var clock = Stopwatch.StartNew();
        var support = await device.GetDeleteSupportAsync(recheck: true, ct);
        Console.WriteLine();
        Console.WriteLine($"The app's verdict ({clock.Elapsed.TotalSeconds:0.0} s, read-only):");
        PrintSupport(support);

        var match = wpd.ListDevices().Where(d => WpdPhotoDeleter.Normalize(d.UsbSerial) == WpdPhotoDeleter.Normalize(device.Udid)).ToList();
        Console.WriteLine();
        Console.WriteLine($"WPD devices whose USB serial is this UDID: {match.Count}");
        if (match.Count != 1)
            return;

        using var c = wpd.Open(match[0].PnpId, readOnly: true);
        Out.Pairs([("WPD serial", c.Device.SerialNumber ?? "—"), ("WPD protocol", c.Device.Protocol ?? "—"), ("WPD model", c.Device.Model ?? "—")]);
        clock.Restart();
        var dcim = WpdDcimWalker.Walk(c, ct, out var missing);
        if (dcim is null)
        {
            Console.WriteLine($"No camera roll in Windows' view: {missing}.");
            return;
        }

        var files = dcim.Folders.Sum(f => f.Files.Count);
        Console.WriteLine();
        Console.WriteLine($"Walked {dcim.Folders.Count} folders, {files} files in {clock.Elapsed.TotalSeconds:0.0} s " +
                          $"(storage \"{dcim.StorageName}\", {dcim.StorageCount} storage(s); folders {(dcim.DcimId == dcim.StorageId ? "straight under the storage" : "in DCIM")}).");
        var phoneFiles = await device.UseMediaAfcAsync(afc => IosPhotoLibrary.ListDcimFilesAsync(afc, ct), ct);
        var map = new WpdDcimMap(dcim, phoneFiles);
        Console.WriteLine($"The phone's own DCIM (AFC): {phoneFiles.Count} media files in {phoneFiles.Select(f => f.Split('/')[1]).Distinct().Count()} folders.");
        Console.WriteLine($"Mapping: {(map.ByFolder ? "folder + name + size (Windows shows the phone's folders)" : "name + size, unique names only (Windows shows other folders)")}");
        Console.WriteLine();
        Out.Table(["folder", "files", "can delete", "read-only", "doesn't say", "extensions"],
            dcim.Folders.Select(f => new[]
            {
                f.Name,
                f.Files.Count.ToString(),
                f.Files.Count(o => o.CanDelete == true).ToString(),
                f.Files.Count(o => o.CanDelete == false).ToString(),
                f.Files.Count(o => o.CanDelete is null).ToString(),
                string.Join(" ", f.Files.GroupBy(o => Path.GetExtension(o.FileName ?? "").ToUpperInvariant()).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}")),
            }));

        var names = phoneFiles.Select(p => p[(p.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wpdNames = dcim.Folders.SelectMany(f => f.Files).Where(o => o.FileName is not null).ToList();
        var edited = wpdNames.Count(o => WpdPhotoDeleter.IsEditedName(o.FileName!));
        var aae = wpdNames.Count(o => o.FileName!.EndsWith(".AAE", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine();
        Console.WriteLine($"Names Windows lists that are files in the phone's DCIM: {wpdNames.Count(o => names.Contains(o.FileName!))} of {wpdNames.Count} " +
                          $"(edited renders IMG_E…: {edited}; .AAE sidecars: {aae}; Live Photo .MOV: {wpdNames.Count(o => o.FileName!.EndsWith(".MOV", StringComparison.OrdinalIgnoreCase))}).");
    }

    private static void PrintSupport(PhotoDeleteSupport s) => Out.Pairs([
        ("can delete", s.CanDelete ? "yes" : "no"),
        ("reason", s.Reason.ToString()),
        ("detail", s.Detail ?? "—"),
        ("goes to", $"{s.GoesTo}{(s.Observed ? " (seen on this phone)" : " (as users report; not seen yet)")}"),
        ("iCloud Photos", s.ICloudPhotos switch { true => "signs of it", false => "no sign", null => "unknown" }),
    ]);

    // ---------- photodelete: find given pictures on the phone, then (with --delete --yes) delete exactly those ----------

    private sealed record Local(string Path, string Name, long Size, string Hash, DateTime Written);

    private sealed record Found(Local Local, string AssetId, long Size, string How);

    public static async Task PhotoDeleteAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var folder = cli.Option("find") ?? @"E:\DeviceDeck-test\localsend";
        var deleting = cli.Has("delete");
        if (deleting && !cli.Has("yes"))
            throw new CliException("--delete changes the phone; add --yes to confirm.");

        var locals = Directory.Exists(folder)
            ? Directory.GetFiles(folder).Where(f => Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".jpg", StringComparison.OrdinalIgnoreCase)).Order().ToList()
            : throw new CliException($"No folder {folder}.");
        if (locals.Count is 0 or > 10)
            throw new CliException($"{folder} must hold 1 to 10 pictures to look for; it has {locals.Count}.");
        var wanted = locals.Select(p => new Local(p, Path.GetFileName(p), new FileInfo(p).Length, Sha256(File.ReadAllBytes(p)), File.GetLastWriteTime(p))).ToList();
        Console.WriteLine($"Looking for {wanted.Count} picture(s) from {folder}:");
        Out.Table(["file", "bytes", "sha-256", "written"], wanted.Select(w => new[] { w.Name, w.Size.ToString("N0"), w.Hash[..16] + "…", w.Written.ToString("yyyy-MM-dd HH:mm") }));

        var photos = (IosPhotoLibrary)device.Photos;
        var clock = Stopwatch.StartNew();
        var index = await photos.GetIndexAsync(refresh: true, ct);
        Console.WriteLine($"\nPhoto index: {index.Assets.Count} assets ({index.Mode}{(index.SchemaVariant is { } v ? ", " + v : "")}) in {clock.Elapsed.TotalSeconds:0.0} s.");

        // Content first: the phone's PNG/JPG files of a size one of the pictures has are read and hashed.
        var exts = wanted.Select(w => Path.GetExtension(w.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = index.Assets.Where(a => !a.InCloudOnly && exts.Contains(Path.GetExtension(a.Id))).Select(a => a.Id).ToList();
        var sizes = await photos.StatAsync(candidates, ct);
        var bySize = sizes.Where(s => s.Value is { } info && wanted.Any(w => w.Size == info.Size)).Select(s => (Id: s.Key, Size: s.Value!.Size)).ToList();
        Console.WriteLine($"{candidates.Count} phone files with those extensions; {bySize.Count} of the same size as one of the pictures; reading those.");
        var found = new List<Found>();
        foreach (var (id, size) in bySize)
        {
            using var buffer = new MemoryStream((int)size);
            await photos.ReadOriginalAsync(id, buffer, null, ct);
            var hash = Sha256(buffer.ToArray());
            foreach (var w in wanted.Where(w => w.Hash == hash))
                found.Add(new Found(w, id, size, "content (SHA-256)"));
        }

        // Then, for a picture whose content isn't on the phone: its name in Photos.sqlite, its size and its day.
        foreach (var w in wanted.Where(w => found.All(f => f.Local != w)))
        {
            var rows = ReadRows(photos);
            var day = w.Written.Date;
            var near = rows.Where(r => r.OriginalFileSize == w.Size
                                       && (string.Equals(r.OriginalFileName, w.Name, StringComparison.OrdinalIgnoreCase) || r.DateCreated?.LocalDateTime.Date == day)
                                       && !r.Trashed)
                .ToList();
            foreach (var r in near)
                found.Add(new Found(w, r.Path, r.OriginalFileSize ?? 0, "name/size/date only — the content didn't match"));
        }

        Console.WriteLine();
        Out.Table(["picture", "on the phone", "bytes", "matched by"], wanted.Select(w =>
        {
            var hits = found.Where(f => f.Local == w).ToList();
            return hits.Count == 0 ? [w.Name, "not found", "", ""]
                : hits.Count > 1 ? [w.Name, $"{hits.Count} candidates: " + string.Join(", ", hits.Select(h => h.AssetId)), "", hits[0].How]
                : new[] { w.Name, hits[0].AssetId, hits[0].Size.ToString("N0"), hits[0].How };
        }));

        // How each maps to what Windows lists (read-only).
        var wpd = WpdBackends.ForThisPc();
        var support = await device.GetDeleteSupportAsync(recheck: true, ct);
        Console.WriteLine("\nThe app's verdict:");
        PrintSupport(support);
        if (wpd is not null)
        {
            var mine = wpd.ListDevices().Where(d => WpdPhotoDeleter.Normalize(d.UsbSerial) == WpdPhotoDeleter.Normalize(device.Udid)).ToList();
            if (mine.Count == 1)
            {
                using var c = wpd.Open(mine[0].PnpId, readOnly: true);
                if (WpdDcimWalker.Walk(c, ct, out var missing) is { } dcim)
                {
                    var phoneFiles = await device.UseMediaAfcAsync(afc => IosPhotoLibrary.ListDcimFilesAsync(afc, ct), ct);
                    var map = new WpdDcimMap(dcim, phoneFiles);
                    Console.WriteLine($"\nIn Windows' view ({(map.ByFolder ? "folder + name + size" : "name + size")}):");
                    Out.Table(["on the phone", "Windows object", "folder", "bytes", "can delete", "problem"], found.Select(f =>
                    {
                        var m = map.Find(f.AssetId, f.Size);
                        var o = m.Object ?? dcim.Folders.SelectMany(x => x.Files).FirstOrDefault(x => string.Equals(x.FileName, Path.GetFileName(f.AssetId), StringComparison.OrdinalIgnoreCase));
                        return new[]
                        {
                            f.AssetId, o?.FileName ?? "—", o is null ? "—" : map.FolderOf(o)?.Name ?? "—", o?.Size?.ToString("N0") ?? "—",
                            o?.CanDelete switch { true => "yes", false => "no", null => "doesn't say" }, m.Ok ? "" : $"{m.Problem}: {m.Message}",
                        };
                    }));
                }
                else
                {
                    Console.WriteLine($"\nWindows shows no camera roll: {missing}.");
                }
            }
            else
            {
                Console.WriteLine($"\nWindows lists {mine.Count} cameras with this iPhone's USB serial.");
            }
        }

        if (!deleting)
        {
            Console.WriteLine("\nRead-only: nothing was deleted. Add --delete --yes to delete exactly the files matched by content above.");
            return;
        }

        // ---------- --delete --yes ----------
        var exact = wanted.Select(w => found.Where(f => f.Local == w).ToList()).ToList();
        if (exact.Any(h => h.Count != 1))
            throw new CliException("Every picture must match exactly one phone file before anything is deleted; see the table above.");
        if (exact.Any(h => !h[0].How.StartsWith("content", StringComparison.Ordinal)) && !cli.Has("allow-fallback"))
            throw new CliException("Some matches are by name/size/date, not content; add --allow-fallback to delete them anyway.");
        if (!support.CanDelete)
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"The app wouldn't delete on this phone now: {support.Reason} ({support.Detail}).");

        var assets = index.Assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var items = exact.Select(h => new PhotoDeleteItem(h[0].AssetId, assets[h[0].AssetId].Size > 0 ? assets[h[0].AssetId].Size : h[0].Size)).ToList();
        Console.WriteLine($"\nDeleting exactly {items.Count} file(s) through Windows: {string.Join(", ", items.Select(i => i.Id))}");
        clock.Restart();
        var results = await device.DeleteConfirmedAsync(items, ct);
        Console.WriteLine($"({clock.Elapsed.TotalSeconds:0.0} s)");
        Out.Table(["file", "outcome", "went to", "also", "note"], results.Select(r => new[]
        {
            r.Id, r.Outcome.ToString(), r.WentTo.ToString(), string.Join(", ", r.Also), r.Message ?? "",
        }));

        // Gone from the index? Still on the phone's disk (Recently Deleted keeps the file in DCIM)?
        var after = await photos.GetIndexAsync(refresh: true, ct);
        var listed = after.Assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var disk = await photos.StatAsync(items.Select(i => i.Id).ToList(), ct);
        Console.WriteLine("\nAfterwards:");
        Out.Table(["file", "in the photo index", "file in DCIM"], items.Select(i => new[] { i.Id, listed.Contains(i.Id) ? "yes" : "no", disk.GetValueOrDefault(i.Id) is null ? "gone" : "still there" }));
        Console.WriteLine("\nLog lines (" + (PhotoDeleteLog.DefaultFolder) + "):");
        foreach (var line in device.PhotoDeleter.LogLines)
            Console.WriteLine("  " + line);
    }

    private static IReadOnlyList<PhotoRow> ReadRows(IosPhotoLibrary photos)
    {
        var copy = Path.Combine(photos.CacheDirectory, "Photos.sqlite");
        if (!File.Exists(copy))
            return [];
        using var db = PhotosDatabase.Open(copy);
        return db.ReadAssets();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
