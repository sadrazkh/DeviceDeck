using System.Globalization;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.Wpd;

namespace DeviceDeck.Ios.PhotoDelete;

/// <summary>What the deleter needs from the phone's own side (AFC and the photo index); IosDevice provides it, tests fake it.</summary>
public interface IPhoneCameraRoll
{
    Task<PhotoIndex> GetIndexAsync(bool refresh, CancellationToken ct);

    /// <summary>The index as last built, if any (a check doesn't build one).</summary>
    PhotoIndex? CachedIndex { get; }

    /// <summary>Each file's size on the phone's disk now; null when it isn't there.</summary>
    Task<IReadOnlyDictionary<string, long?>> StatAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Every media file in DCIM, as "DCIM/&lt;folder&gt;/&lt;name&gt;".</summary>
    Task<IReadOnlyList<string>> ListDcimAsync(CancellationToken ct);

    /// <summary>True when the library shows signs of iCloud Photos; null when unknown.</summary>
    bool? ICloudPhotosHint(PhotoIndex? index);
}

/// <summary>
/// Deletes iPhone photos the way File Explorer does: through Windows Portable Devices (PTP), so the phone's Photos
/// library removes them itself. Nothing in DCIM is touched over AFC (that would leave the library pointing at missing
/// files). Only objects found under Internal Storage › DCIM › &lt;folder&gt; are ever deleted, never a folder, never
/// recursively, and only when they match the photo the user confirmed exactly.
/// </summary>
public sealed class WpdPhotoDeleter
{
    /// <summary>Photos per delete call (a Live Photo's still and video count as one).</summary>
    public const int BatchSize = 5;

    /// <summary>
    /// Where a delete from a PC goes before this phone showed it. Apple doesn't document deletes over the camera
    /// connection; users report them gone for good, not in Recently Deleted (File Explorer on iOS 9 and 10, Image Capture on
    /// iOS 16, the Windows Photos app on iOS 14 with iCloud Photos, which removed them from iCloud too). So the page says
    /// "deleted for good" until the first delete on this phone shows otherwise (file kept in DCIM and marked deleted).
    /// </summary>
    public static PhotoDeleteDestination ReportedDestination { get; } = PhotoDeleteDestination.Permanent;

    private readonly IWpdBackend? _wpd;
    private readonly string _udid;
    private readonly IPhoneCameraRoll _phone;
    private readonly PhotoDeleteLog _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PhotoDeleteSupport _support = PhotoDeleteSupport.No(PhotoDeleteBlock.Checking);
    private PhotoDeleteDestination _observed = PhotoDeleteDestination.Unknown;

    public WpdPhotoDeleter(IWpdBackend? wpd, string udid, IPhoneCameraRoll phone, PhotoDeleteLog log, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _wpd = wpd;
        _udid = udid;
        _phone = phone;
        _log = log;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The log lines written in this process (the probe prints them).</summary>
    public IReadOnlyList<string> LogLines => _log.Written;

    /// <summary>The last answer of <see cref="CheckAsync"/>, with what deletes on this phone have shown since.</summary>
    public PhotoDeleteSupport Current => WithDestination(Volatile.Read(ref _support));

    /// <summary>Forgets the last check (the phone was locked or went away).</summary>
    public void Reset(PhotoDeleteBlock reason, string? detail = null) => Volatile.Write(ref _support, PhotoDeleteSupport.No(reason, detail));

    // ---------- Can this phone's photos be deleted from the PC? ----------

    /// <summary>
    /// Asks Windows: is the phone listed as a camera, does it show Internal Storage › DCIM, and do DCIM's files say
    /// they can be deleted? Read-only: the device is opened for reading only.
    /// </summary>
    public async Task<PhotoDeleteSupport> CheckAsync(DeviceState state, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var support = await CheckLockedAsync(state, ct).ConfigureAwait(false);
            Volatile.Write(ref _support, support);
            return WithDestination(support);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PhotoDeleteSupport> CheckLockedAsync(DeviceState state, CancellationToken ct)
    {
        if (_wpd is null)
        {
            return PhotoDeleteSupport.No(PhotoDeleteBlock.Unsupported, "Windows Portable Devices isn't available on this PC.");
        }

        if (state == DeviceState.Locked)
        {
            return PhotoDeleteSupport.No(PhotoDeleteBlock.Locked, "The iPhone is locked.");
        }

        if (state != DeviceState.Ready)
        {
            return PhotoDeleteSupport.No(PhotoDeleteBlock.Checking, $"The iPhone is {state.ToString().ToLowerInvariant()}.");
        }

        var phoneFiles = await _phone.ListDcimAsync(ct).ConfigureAwait(false);
        var index = _phone.CachedIndex;
        return await Task.Run(() => CheckWithWindows(phoneFiles, index, ct), ct).ConfigureAwait(false);
    }

    private PhotoDeleteSupport CheckWithWindows(IReadOnlyList<string> phoneFiles, PhotoIndex? index, CancellationToken ct)
    {
        try
        {
            var (device, why) = FindDevice(ct);
            if (device is null)
            {
                return PhotoDeleteSupport.No(PhotoDeleteBlock.NotCamera, why);
            }

            using var c = _wpd!.Open(device.PnpId, readOnly: true);
            if (WpdDcimWalker.FindDcim(c, ct, out var missing) is not { } dcim)
            {
                return missing switch
                {
                    WpdDcimWalker.Missing.NoStorage => PhotoDeleteSupport.No(PhotoDeleteBlock.Locked, "Windows lists the iPhone but no storage on it."),
                    WpdDcimWalker.Missing.EmptyStorage => PhotoDeleteSupport.No(PhotoDeleteBlock.Locked, "Windows lists the iPhone's Internal Storage as empty."),
                    _ => PhotoDeleteSupport.No(PhotoDeleteBlock.Unsupported, "Windows lists no DCIM folder on the iPhone."),
                };
            }

            var folders = WpdDcimWalker.Folders(c, dcim.DcimId, ct);
            if (folders.Count == 0)
            {
                return phoneFiles.Count == 0
                    ? Yes("DCIM is empty on the phone and in Windows.")
                    : PhotoDeleteSupport.No(PhotoDeleteBlock.Locked, "Windows lists DCIM on the iPhone with nothing in it.");
            }

            // A sample is enough: the phone marks its whole library the same way. Photos and videos only: an edit's
            // .AAE sidecar says nothing about them.
            var sample = new[] { folders[0], folders[folders.Count / 2], folders[^1] }
                .DistinctBy(f => f.Id)
                .SelectMany(f => WpdDcimWalker.Files(c, f.Id, ct, limit: 8))
                .Where(o => o.FileName is { } n && PhotoIndex.IsMediaFile(n))
                .ToList();
            if (sample.Count == 0)
            {
                return Yes("Windows lists DCIM folders without files.");
            }

            var deletable = sample.Count(o => o.CanDelete == true);
            var flags = $"{deletable} of {sample.Count} sampled files say they can be deleted ({sample.Count(o => o.CanDelete is null)} don't say)";
            var hint = _phone.ICloudPhotosHint(index);
            if (deletable == 0)
            {
                var icloud = hint == true;
                return PhotoDeleteSupport.No(icloud ? PhotoDeleteBlock.ICloudPhotos : PhotoDeleteBlock.ReadOnly,
                    $"{flags}; {folders.Count} camera-roll folders." + (icloud ? " The library has iCloud Photos assets." : "")) with { ICloudPhotos = hint };
            }

            if (Converted(sample, phoneFiles))
            {
                return PhotoDeleteSupport.No(PhotoDeleteBlock.Converted,
                    $"None of {sample.Count} sampled names is a file on the phone, but the same names with another extension are: the phone converts photos for the PC.");
            }

            var mirrored = folders.Any(f => phoneFiles.Any(p => p.StartsWith("DCIM/" + f.FileName + "/", StringComparison.OrdinalIgnoreCase)));
            return Yes($"{flags}; {folders.Count} camera-roll folders ({folders[0].FileName}…), {(mirrored ? "named as on the phone" : "named differently from the phone's")}"
                + (dcim.DcimId == dcim.StorageId ? ", straight under the storage." : " in DCIM.")) with { ICloudPhotos = hint };
        }
        catch (WpdException ex)
        {
            return PhotoDeleteSupport.No(ex.HResult == WpdErrors.AccessDenied ? PhotoDeleteBlock.Locked : PhotoDeleteBlock.NotCamera, ex.Message);
        }
    }

    private static PhotoDeleteSupport Yes(string detail) => new(true, PhotoDeleteBlock.None, detail, PhotoDeleteDestination.Unknown, false, DateTimeOffset.Now);

    /// <summary>Names Windows lists that aren't on the phone while their stems are, with another extension (HEIC shown as JPG).</summary>
    internal static bool Converted(IReadOnlyList<WpdObject> sample, IReadOnlyCollection<string> phoneFiles)
    {
        var names = phoneFiles.Select(f => f[(f.LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stems = names.Select(n => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var originals = sample.Where(o => o.FileName is { } n && !IsEditedName(n)).ToList();
        return originals.Count > 0
               && originals.All(o => !names.Contains(o.FileName!))
               && originals.Any(o => stems.Contains(Path.GetFileNameWithoutExtension(o.FileName!)));
    }

    /// <summary>"IMG_E0001.JPG": the rendered edit iOS shows the PC next to the original.</summary>
    internal static bool IsEditedName(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var cut = stem.IndexOf('_');
        return cut >= 0 && cut + 1 < stem.Length && stem[cut + 1] is 'E' or 'e' && stem.Length > cut + 2 && char.IsDigit(stem[cut + 2]);
    }

    /// <summary>The one WPD device that is this iPhone: its USB serial, or else its WPD serial number, is the UDID.</summary>
    private (WpdDeviceInfo? Device, string Why) FindDevice(CancellationToken ct)
    {
        var devices = _wpd!.ListDevices();
        var mine = Normalize(_udid);
        var byUsb = devices.Where(d => Normalize(d.UsbSerial) == mine).ToList();
        if (byUsb.Count == 1)
        {
            return (byUsb[0], "");
        }

        var apple = devices.Where(d => d.PnpId.Contains("vid_05ac", StringComparison.OrdinalIgnoreCase)).ToList();
        var bySerial = new List<WpdDeviceInfo>();
        foreach (var candidate in byUsb.Count > 1 ? byUsb : apple)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var c = _wpd.Open(candidate.PnpId, readOnly: true);
                if (Normalize(c.Device.SerialNumber) == mine)
                {
                    bySerial.Add(candidate);
                }
            }
            catch (WpdException)
            {
                // Not this one, or not answering.
            }
        }

        return bySerial.Count == 1
            ? (bySerial[0], "")
            : (null, bySerial.Count > 1
                ? $"Windows lists {bySerial.Count} cameras with this iPhone's serial number."
                : $"Windows lists {devices.Count} portable devices ({apple.Count} from Apple); none has this iPhone's serial number.");
    }

    internal static string Normalize(string? serial) =>
        serial is null ? "" : string.Concat(serial.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private PhotoDeleteSupport WithDestination(PhotoDeleteSupport support)
    {
        var observed = _observed;
        return observed != PhotoDeleteDestination.Unknown
            ? support with { GoesTo = observed, Observed = true }
            : support with { GoesTo = ReportedDestination, Observed = false };
    }

    // ---------- Deleting ----------

    private sealed record Plan(PhotoDeleteItem Item, PhotoAsset Asset, string? PairPath, long DiskSize, long? PairDiskSize)
    {
        public WpdObject? Still;
        public WpdObject? Pair;
        public IReadOnlyList<WpdObject> Edits = [];
        public string? Note;
    }

    /// <summary>See <see cref="IPhotoDeleter.DeleteConfirmedAsync"/>.</summary>
    public async Task<IReadOnlyList<PhotoDeleteResult>> DeleteAsync(IReadOnlyList<PhotoDeleteItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        if (items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ArgumentException("An item is listed twice.");
        }

        if (_wpd is null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "This PC can't reach the iPhone's camera connection (Windows Portable Devices).");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DeleteLockedAsync(items, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<PhotoDeleteResult>> DeleteLockedAsync(IReadOnlyList<PhotoDeleteItem> items, CancellationToken ct)
    {
        var results = new PhotoDeleteResult?[items.Count];

        // 1. The phone's side: still in the library at the confirmed size, and its file is in DCIM at that size.
        var index = await _phone.GetIndexAsync(refresh: true, ct).ConfigureAwait(false);
        var assets = index.Assets.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var plans = new Plan?[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!assets.TryGetValue(item.Id, out var asset))
            {
                results[i] = Mismatch(item, null, "It's no longer in the iPhone's library.");
            }
            else if (asset.Size > 0 && asset.Size != item.Size)
            {
                results[i] = Mismatch(item, asset, "It changed since it was selected.");
            }
            else if (asset.InCloudOnly)
            {
                results[i] = Mismatch(item, asset, "Its original is in iCloud only, not on the iPhone.");
            }
            else
            {
                plans[i] = new Plan(item, asset, asset.Kind == PhotoKind.LivePhoto ? asset.LivePairId : null, 0, null);
            }
        }

        var paths = plans.Where(p => p is not null).SelectMany(p => p!.PairPath is null ? [p.Item.Id] : new[] { p.Item.Id, p.PairPath }).ToList();
        var disk = paths.Count == 0 ? new Dictionary<string, long?>() : await _phone.StatAsync(paths, ct).ConfigureAwait(false);
        for (var i = 0; i < plans.Length; i++)
        {
            if (plans[i] is not { } plan)
            {
                continue;
            }

            if (disk.GetValueOrDefault(plan.Item.Id) is not { } size)
            {
                results[i] = Mismatch(plan.Item, plan.Asset, "The iPhone has no such file in DCIM.");
                plans[i] = null;
                continue;
            }

            // Without Photos.sqlite the library knows no sizes; the page showed the file's own size then.
            if (plan.Asset.Size == 0 && size != plan.Item.Size)
            {
                results[i] = Mismatch(plan.Item, plan.Asset, "It changed since it was selected.");
                plans[i] = null;
                continue;
            }

            plans[i] = plan with { DiskSize = size, PairDiskSize = plan.PairPath is null ? null : disk.GetValueOrDefault(plan.PairPath) };
        }

        if (results.Any(r => r is not null))
        {
            return Refuse(items, results, plans, "Nothing was deleted, because another selected item didn't match.");
        }

        var phoneFiles = await _phone.ListDcimAsync(ct).ConfigureAwait(false);
        var edited = items.Where(i => index.EditStampOf(i.Id) is not null).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);

        // 2. Windows' side, then 3. the deletes: blocking COM calls, off the caller's thread.
        var deleted = await Task.Run(() => DeleteWithWindows(items, plans!, results, phoneFiles, edited, ct), ct).ConfigureAwait(false);

        // 4. Where they went: the photo index is rebuilt, and the phone's disk says whether the files stayed.
        if (deleted.Count > 0)
        {
            await ClassifyAsync(items, plans!, results, deleted, ct).ConfigureAwait(false);
        }

        return results.Select((r, i) => r ?? NotTried(items[i], plans[i]?.Asset, "Not tried.")).ToList();
    }

    /// <summary>Maps every item, then deletes batch by batch. Returns the indexes of the items deleted.</summary>
    private List<int> DeleteWithWindows(IReadOnlyList<PhotoDeleteItem> items, Plan[] plans, PhotoDeleteResult?[] results, IReadOnlyList<string> phoneFiles,
        HashSet<string> edited, CancellationToken ct)
    {
        var deleted = new List<int>();
        IWpdConnection? c = null;
        try
        {
            var (device, why) = FindDevice(ct);
            if (device is null)
            {
                FailAll(items, plans, results, DeviceErrorCodes.NotSupported, "Windows doesn't list the iPhone as a camera now. " + why);
                return deleted;
            }

            c = _wpd!.Open(device.PnpId, readOnly: false);
            var dcim = WpdDcimWalker.Walk(c, ct, out _);
            if (dcim is null)
            {
                FailAll(items, plans, results, DeviceErrorCodes.Locked, "Windows doesn't show the iPhone's DCIM folder now. Unlock the iPhone and try again.");
                return deleted;
            }

            var map = new WpdDcimMap(dcim, phoneFiles);
            for (var i = 0; i < plans.Length; i++)
            {
                var plan = plans[i];
                var still = map.Find(plan.Item.Id, plan.DiskSize);
                if (!still.Ok)
                {
                    results[i] = Mismatch(plan.Item, plan.Asset, still.Message);
                    continue;
                }

                plan.Still = still.Object;
                if (plan.PairPath is not null)
                {
                    var pair = plan.PairDiskSize is { } pairSize ? map.Find(plan.PairPath, pairSize) : null;
                    if (pair is null)
                    {
                        plan.Note = "Its Live Photo video isn't in DCIM.";
                    }
                    else if (pair.Ok)
                    {
                        plan.Pair = pair.Object;
                    }
                    else if (pair.Problem == WpdMapProblem.Absent)
                    {
                        // Windows doesn't list the video half: the phone shows Live Photos as stills only. The still goes;
                        // whether its video went with it is checked on the phone afterwards.
                        plan.Note = "Windows doesn't list its Live Photo video.";
                    }
                    else
                    {
                        results[i] = Mismatch(plan.Item, plan.Asset, "Its Live Photo video doesn't match: " + pair.Message);
                        continue;
                    }
                }

                if (edited.Contains(plan.Item.Id))
                {
                    plan.Edits = map.EditsOf(plan.Still!);
                }
            }

            if (results.Any(r => r is not null))
            {
                Refuse(items, results, plans, "Nothing was deleted, because another selected item didn't match.");
                return deleted;
            }

            for (var start = 0; start < plans.Length; start += BatchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = Enumerable.Range(start, Math.Min(BatchSize, plans.Length - start)).ToList();
                if (!DeleteBatch(c, map, plans, results, batch, deleted, edited))
                {
                    break;
                }
            }
        }
        catch (WpdException ex)
        {
            FailAll(items, plans, results, Code(ex.HResult), ex.Message);
        }
        finally
        {
            c?.Dispose();
        }

        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= NotTried(items[i], plans[i].Asset, "Not tried: the delete stopped at an earlier item.");
        }

        return deleted;
    }

    /// <summary>Re-checks and deletes one batch. False when it stopped (a mismatch or an error).</summary>
    private bool DeleteBatch(IWpdConnection c, WpdDcimMap map, Plan[] plans, PhotoDeleteResult?[] results, List<int> batch, List<int> deleted, HashSet<string> edited)
    {
        // Just before deleting: each object is still the same file in the same DCIM folder.
        foreach (var i in batch)
        {
            var plan = plans[i];
            foreach (var expected in new[] { plan.Still, plan.Pair }.Where(o => o is not null).Select(o => o!))
            {
                var now = c.Get(expected.Id);
                var folder = map.FolderOf(expected);
                if (now is null || now.Kind != WpdObjectKind.File || now.Size != expected.Size
                    || !string.Equals(now.FileName, expected.FileName, StringComparison.OrdinalIgnoreCase)
                    || folder is null || (now.ParentId is not null && now.ParentId != folder.Id))
                {
                    results[i] = Mismatch(plan.Item, plan.Asset, $"{expected.FileName} changed on the iPhone just before the delete.");
                    Log($"refused {plan.Item.Id}: {expected.FileName} changed just before the delete");
                    return false;
                }
            }
        }

        var ids = new List<string>();
        var owner = new List<(int Item, bool IsStill)>();
        foreach (var i in batch)
        {
            ids.Add(plans[i].Still!.Id);
            owner.Add((i, true));
            if (plans[i].Pair is { } pair)
            {
                ids.Add(pair.Id);
                owner.Add((i, false));
            }
        }

        var outcome = c.Delete(ids);
        var stop = false;
        foreach (var i in batch)
        {
            var plan = plans[i];
            var stillAt = owner.FindIndex(o => o.Item == i && o.IsStill);
            var stillHr = outcome[stillAt];
            if (stillHr != 0 || StillListed(c, plan.Still!.Id) == true)
            {
                var hr = stillHr != 0 ? stillHr : WpdErrors.GenFailure;
                results[i] = new PhotoDeleteResult(plan.Item.Id, plan.Asset.FileName, plan.Item.Size, PhotoDeleteOutcome.Failed, Code(hr),
                    stillHr != 0 ? Sentence(WpdErrors.Describe(hr)) : "The iPhone still lists it after the delete.", [], PhotoDeleteDestination.Unknown);
                Log($"failed {plan.Item.Id} ({Bytes(plan.DiskSize)}): {WpdErrors.Describe(hr)}");
                stop = true;
                continue;
            }

            Log($"deleted {plan.Item.Id} ({Bytes(plan.DiskSize)}) through Windows, object {plan.Still.Id}");
            var also = new List<string>();
            var notes = new List<string>();
            var freed = plan.Item.Size;
            if (plan.Pair is { } pair)
            {
                var pairAt = owner.FindIndex(o => o.Item == i && !o.IsStill);
                if (outcome[pairAt] == 0 || StillListed(c, pair.Id) == false)
                {
                    also.Add(pair.FileName ?? Path.GetFileName(plan.PairPath!));
                    freed += plan.PairDiskSize ?? 0;
                    Log($"deleted {plan.PairPath} ({Bytes(plan.PairDiskSize ?? 0)}), the Live Photo's video" + (outcome[pairAt] == 0 ? "" : ", gone with its still"));
                }
                else
                {
                    notes.Add($"Its Live Photo video ({pair.FileName}) is still on the iPhone: {WpdErrors.Describe(outcome[pairAt])}.");
                    Log($"kept {plan.PairPath}: {WpdErrors.Describe(outcome[pairAt])}");
                }
            }

            if (plan.Note is { } note)
            {
                notes.Add(note);
            }

            if (plan.Edits.Count > 0)
            {
                // Not deleted by DeviceDeck: the phone makes these for the PC. What happened to them is reported.
                var kept = plan.Edits.Where(o => StillListed(c, o.Id) == true).Select(o => o.FileName).ToList();
                var names = string.Join(", ", plan.Edits.Select(o => o.FileName));
                notes.Add(kept.Count == 0
                    ? $"Its edit ({names}) went with it."
                    : $"Windows still lists {string.Join(", ", kept)}, the edit the iPhone made for the PC.");
                Log($"edit of {plan.Item.Id} ({names}): " + (kept.Count == 0 ? "gone with it" : "still listed: " + string.Join(", ", kept)));
            }
            else if (edited.Contains(plan.Item.Id))
            {
                notes.Add("It was edited; Windows listed no separate edited version.");
                Log($"edit of {plan.Item.Id}: Windows listed no separate edited version");
            }

            results[i] = new PhotoDeleteResult(plan.Item.Id, plan.Asset.FileName, freed, PhotoDeleteOutcome.Deleted, null,
                notes.Count == 0 ? null : string.Join(" ", notes), also, PhotoDeleteDestination.Unknown);
            deleted.Add(i);
        }

        return !stop;
    }

    /// <summary>
    /// Whether Windows still lists an object after a delete; null when it couldn't say (which error it gives for an
    /// object it no longer has isn't documented). The batch's own per-object result decides then: never throws, so a
    /// delete that went through is never reported as failed.
    /// </summary>
    private static bool? StillListed(IWpdConnection c, string id)
    {
        try
        {
            return c.Get(id) is not null;
        }
        catch (WpdException)
        {
            return null;
        }
    }

    private async Task ClassifyAsync(IReadOnlyList<PhotoDeleteItem> items, Plan?[] plans, PhotoDeleteResult?[] results, List<int> deleted, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var index = await _phone.GetIndexAsync(refresh: true, ct).ConfigureAwait(false);
            var listed = index.Assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            var disk = await _phone.StatAsync(deleted.Select(i => items[i].Id).ToList(), ct).ConfigureAwait(false);
            var pending = 0;
            foreach (var i in deleted)
            {
                var id = items[i].Id;
                var wentTo = disk.GetValueOrDefault(id) is null ? PhotoDeleteDestination.Permanent
                    : !listed.Contains(id) ? PhotoDeleteDestination.RecentlyDeleted
                    : PhotoDeleteDestination.Unknown;
                if (wentTo == PhotoDeleteDestination.Unknown)
                {
                    pending++;
                }

                results[i] = results[i]! with { WentTo = wentTo };
            }

            if (pending == 0)
            {
                break;
            }

            await _delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        foreach (var i in deleted)
        {
            var r = results[i]!;
            Log($"after the delete, {r.Id}: " + r.WentTo switch
            {
                PhotoDeleteDestination.Permanent => "the file is gone from DCIM",
                PhotoDeleteDestination.RecentlyDeleted => "the file is still in DCIM and the library marks it deleted (Recently Deleted)",
                _ => "the library still lists it",
            });
            if (r.WentTo == PhotoDeleteDestination.Unknown)
            {
                results[i] = r with { Message = Join(r.Message, "The iPhone's library still lists it; it may take a moment to update.") };
            }
        }

        var seen = deleted.Select(i => results[i]!.WentTo).Where(d => d != PhotoDeleteDestination.Unknown).Distinct().ToList();
        if (seen.Count == 1)
        {
            _observed = seen[0];
        }
    }

    private PhotoDeleteResult[] Refuse(IReadOnlyList<PhotoDeleteItem> items, PhotoDeleteResult?[] results, Plan?[] plans, string why)
    {
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is { } r)
            {
                Log($"refused {r.Id}: {r.Message}");
            }

            results[i] ??= NotTried(items[i], plans[i]?.Asset, why);
        }

        return results!;
    }

    private void FailAll(IReadOnlyList<PhotoDeleteItem> items, Plan?[] plans, PhotoDeleteResult?[] results, string code, string message)
    {
        Log("stopped: " + message);
        var first = true;
        for (var i = 0; i < results.Length; i++)
        {
            if (results[i] is not null)
            {
                continue;
            }

            results[i] = first
                ? new PhotoDeleteResult(items[i].Id, plans[i]?.Asset.FileName ?? FileNameOf(items[i].Id), items[i].Size, PhotoDeleteOutcome.Failed, code, message, [], PhotoDeleteDestination.Unknown)
                : NotTried(items[i], plans[i]?.Asset, "Not tried: " + message);
            first = false;
        }
    }

    private static PhotoDeleteResult Mismatch(PhotoDeleteItem item, PhotoAsset? asset, string message) =>
        new(item.Id, asset?.FileName ?? FileNameOf(item.Id), item.Size, PhotoDeleteOutcome.Mismatch, DeviceErrorCodes.PathNotFound, message, [], PhotoDeleteDestination.Unknown);

    private static PhotoDeleteResult NotTried(PhotoDeleteItem item, PhotoAsset? asset, string message) =>
        new(item.Id, asset?.FileName ?? FileNameOf(item.Id), item.Size, PhotoDeleteOutcome.NotTried, null, message, [], PhotoDeleteDestination.Unknown);

    private static string FileNameOf(string id) => id[(id.LastIndexOf('/') + 1)..];

    internal static string Code(int hr) => hr switch
    {
        WpdErrors.AccessDenied => DeviceErrorCodes.PermissionDenied,
        WpdErrors.NotFound or WpdErrors.FileNotFound or WpdErrors.InvalidArg => DeviceErrorCodes.PathNotFound,
        WpdErrors.DeviceNotConnected or WpdErrors.NotReady or WpdErrors.GenFailure => DeviceErrorCodes.Disconnected,
        WpdErrors.NotSupported => DeviceErrorCodes.NotSupported,
        _ => DeviceErrorCodes.Protocol,
    };

    private static string Sentence(string text) => char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private static string? Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + " " + b;

    private static string Bytes(long bytes) => bytes.ToString("N0", CultureInfo.InvariantCulture) + " bytes";

    private void Log(string message) => _log.Write(_udid, message);
}
