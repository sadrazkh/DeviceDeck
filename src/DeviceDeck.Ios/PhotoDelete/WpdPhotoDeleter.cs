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
    private readonly WpdListingCache _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PhotoDeleteSupport _support = PhotoDeleteSupport.No(PhotoDeleteBlock.Checking);
    private PhotoDeleteDestination _observed = PhotoDeleteDestination.Unknown;

    /// <param name="cache">The listing kept between deletes (5 minutes by default; tests give their own clock).</param>
    public WpdPhotoDeleter(IWpdBackend? wpd, string udid, IPhoneCameraRoll phone, PhotoDeleteLog log, Func<TimeSpan, CancellationToken, Task>? delay = null,
        WpdListingCache? cache = null)
    {
        _wpd = wpd;
        _udid = udid;
        _phone = phone;
        _log = log;
        _delay = delay ?? Task.Delay;
        _cache = cache ?? new WpdListingCache();
    }

    /// <summary>The log lines written in this process (the probe prints them).</summary>
    public IReadOnlyList<string> LogLines => _log.Written;

    /// <summary>The last answer of <see cref="CheckAsync"/>, with what deletes on this phone have shown since.</summary>
    public PhotoDeleteSupport Current => WithDestination(Volatile.Read(ref _support));

    /// <summary>Forgets the last check and the kept listing, and stops a read ahead (the phone was locked or went away).</summary>
    public void Reset(PhotoDeleteBlock reason, string? detail = null)
    {
        CancelPrefetch();
        _cache.Invalidate();
        Volatile.Write(ref _support, PhotoDeleteSupport.No(reason, detail));
    }

    // ---------- Reading ahead (v0.4.2) ----------

    private readonly Lock _prefetchGate = new();
    private CancellationTokenSource? _prefetchCts;
    private Task? _prefetchTask;

    /// <summary>How the last read ahead went (the probe prints it).</summary>
    public WpdReadStats? LastPrefetch { get; private set; }

    /// <summary>The read ahead running now, if any.</summary>
    public Task? Prefetching
    {
        get
        {
            lock (_prefetchGate)
            {
                return _prefetchTask is { IsCompleted: false } task ? task : null;
            }
        }
    }

    /// <summary>
    /// Starts reading Windows' view of the whole camera roll in the background, read-only, on a low-priority thread, into
    /// the kept listing: a delete that follows then reads (almost) nothing itself. On the owner's iPhone a first read took
    /// about 90 s for 3,217 objects, and Windows' driver then keeps what it read until the phone is unplugged (0.1 s
    /// to read them all again). Does nothing while one runs, while a delete runs, or when the kept listing is complete.
    /// A delete stops it and carries on from what it read; locking or unplugging the phone stops it.
    /// </summary>
    /// <returns>True when a read ahead started.</returns>
    public bool StartPrefetch(string reason, CancellationToken life)
    {
        if (_wpd is null || _gate.CurrentCount == 0)
        {
            return false;
        }

        lock (_prefetchGate)
        {
            if (_prefetchTask is { IsCompleted: false })
            {
                return false;
            }

            if (_cache.Peek() is { HasAll: true } kept && _cache.Now - kept.ReadAt < _cache.Lifetime / 2)
            {
                return false;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(life);
            _prefetchCts = cts;
            _prefetchTask = Task.Factory.StartNew(() => Prefetch(reason, cts.Token), cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return true;
        }
    }

    private void Prefetch(string reason, CancellationToken ct)
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        var stats = new WpdReadStats();
        LastPrefetch = stats;
        try
        {
            var (device, _) = FindDevice(ct);
            if (device is null)
            {
                return;
            }

            using var c = _wpd!.Open(device.PnpId, readOnly: true);
            var kept = _cache.Take(device.PnpId, out var generation);
            var reader = new WpdRollReader(c, stats, null, ct);
            var listing = reader.Structure(kept, device.PnpId, _cache.Now, out _);
            if (listing is null)
            {
                return;
            }

            stats.FoldersInRoll = listing.Folders.Count;
            // Folder by folder, newest first (what Clean-up and Photos show first), each kept as soon as it is read.
            foreach (var folder in listing.Folders.OrderByDescending(f => WpdMonths.MonthOf(f.FileName) ?? (9999, 12)))
            {
                ct.ThrowIfCancellationRequested();
                reader.Load(listing, [folder]);
                if (!_cache.Keep(listing, generation))
                {
                    return; // the phone went away meanwhile
                }
            }

            Log($"read ahead ({reason}): {stats}");
        }
        catch (OperationCanceledException)
        {
            Log($"read ahead ({reason}) stopped: {stats}");
        }
        catch (Exception ex) when (ex is WpdException or DeviceException or IOException)
        {
            Log($"read ahead ({reason}) failed: {ex.Message}");
        }
    }

    private void CancelPrefetch()
    {
        lock (_prefetchGate)
        {
            try
            {
                _prefetchCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>Stops a read ahead and waits for it to hand over; one that doesn't stop in time is cut off from the kept listing.</summary>
    private async Task StopPrefetchAsync()
    {
        Task? running;
        lock (_prefetchGate)
        {
            running = _prefetchTask is { IsCompleted: false } task ? task : null;
        }

        if (running is null)
        {
            return;
        }

        CancelPrefetch();
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still inside a call to Windows: the delete reads on its own (Windows keeps what was read either way).
            _cache.Invalidate();
        }
    }

    /// <summary>What a dry run found for one item: the object it maps to, or why not.</summary>
    public sealed record DryRunItem(PhotoDeleteItem Item, WpdObject? Object, string? Folder, WpdObject? Pair, string? Refusal);

    /// <summary>
    /// Everything a delete does before its first batch (the phone's side, then Windows' view, kept listing and read ahead
    /// included), on a read-only connection, and nothing after: for timing and checking the mapping on a real phone.
    /// </summary>
    public async Task<IReadOnlyList<DryRunItem>> DryRunAsync(IReadOnlyList<PhotoDeleteItem> items, IPhotoDeleteObserver? observer, CancellationToken ct)
    {
        if (_wpd is null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "This PC can't reach the iPhone's camera connection (Windows Portable Devices).");
        }

        observer ??= NoPhotoDeleteObserver.Instance;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            observer.Stage(PhotoDeleteStage.Reading, 0, 0);
            await StopPrefetchAsync().ConfigureAwait(false);
            var results = new PhotoDeleteResult?[items.Count];
            var plans = new Plan?[items.Count];
            if (await PhoneSideAsync(items, results, plans, ct).ConfigureAwait(false) is not { } side)
            {
                return items.Select((item, i) => new DryRunItem(item, null, null, null, results[i]?.Message ?? "Not tried: another item didn't match.")).ToList();
            }

            return await Task.Run(() =>
            {
                var (device, why) = FindDevice(ct);
                if (device is null)
                {
                    return items.Select(i => new DryRunItem(i, null, null, null, "Windows doesn't list the iPhone as a camera now. " + why)).ToList();
                }

                using var c = _wpd.Open(device.PnpId, readOnly: true);
                var map = MapWithWindows(c, device.PnpId, plans!, side.Files, side.Assets, side.Edited, observer, ct);
                return plans!.Select(p => new DryRunItem(p!.Item, map is null ? null : p.Still, p.Still is null || map is null ? null : map.FolderOf(p.Still)?.Name,
                    p.Pair, map is null ? "Windows shows no camera roll." : p.Refusal)).ToList();
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

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
            // iOS 27 doesn't report WPD_OBJECT_CAN_DELETE at all: then the device's own word, that it deletes objects, counts.
            var unmarked = deletable == 0 && sample.All(o => o.CanDelete is null);
            if (unmarked && c.SupportedCommands().Contains(WpdCommands.DeleteObjects))
            {
                deletable = sample.Count;
                flags = $"none of {sample.Count} sampled files says whether it can be deleted (the phone doesn't report it); the phone supports deleting objects";
            }

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
        public int PairHr;
        public string? Note;

        /// <summary>Why it doesn't map to what Windows lists (null when it does).</summary>
        public string? Refusal;
        public WpdMapProblem Problem;

        public void Clear()
        {
            Still = null;
            Pair = null;
            Edits = [];
            PairHr = 0;
            Note = null;
            Refusal = null;
            Problem = WpdMapProblem.None;
        }
    }

    /// <summary>How the last delete read Windows' view of the camera roll (the probe prints it).</summary>
    public WpdReadStats? LastRead { get; private set; }

    /// <summary>The listing of this phone's camera roll kept between deletes.</summary>
    public WpdListingCache Listing => _cache;

    /// <summary>See <see cref="IPhotoDeleter.DeleteConfirmedAsync"/>.</summary>
    public Task<IReadOnlyList<PhotoDeleteResult>> DeleteAsync(IReadOnlyList<PhotoDeleteItem> items, CancellationToken ct) => DeleteAsync(items, null, ct);

    /// <summary>See <see cref="IPhotoDeleter.DeleteConfirmedAsync"/>.</summary>
    public async Task<IReadOnlyList<PhotoDeleteResult>> DeleteAsync(IReadOnlyList<PhotoDeleteItem> items, IPhotoDeleteObserver? observer, CancellationToken ct)
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
            return await DeleteLockedAsync(items, observer ?? NoPhotoDeleteObserver.Instance, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record PhoneSide(IReadOnlyList<string> Files, HashSet<string> Edited, IReadOnlyCollection<WpdPhoneAsset>? Assets);

    /// <summary>
    /// 1. The phone's side: still in the library at the confirmed size, and its file is in DCIM at that size. Fills
    /// <paramref name="results"/> with refusals; null when one was refused (nothing may be deleted then).
    /// </summary>
    private async Task<PhoneSide?> PhoneSideAsync(IReadOnlyList<PhotoDeleteItem> items, PhotoDeleteResult?[] results, Plan?[] plans, CancellationToken ct)
    {
        var index = await _phone.GetIndexAsync(refresh: true, ct).ConfigureAwait(false);
        var assets = index.Assets.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
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
            return null;
        }

        var phoneFiles = await _phone.ListDcimAsync(ct).ConfigureAwait(false);
        var edited = items.Where(i => index.EditStampOf(i.Id) is not null).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        // Sizes and dates come from Photos.sqlite: without it the renamed match stays off.
        var phoneAssets = index.Mode == PhotoIndexMode.Database
            ? index.Assets.Where(a => a.Size > 0).Select(a => new WpdPhoneAsset(a.Id, a.Size, a.TakenAt)).ToList()
            : null;
        return new PhoneSide(phoneFiles, edited, phoneAssets);
    }

    private async Task<IReadOnlyList<PhotoDeleteResult>> DeleteLockedAsync(IReadOnlyList<PhotoDeleteItem> items, IPhotoDeleteObserver observer, CancellationToken ct)
    {
        var results = new PhotoDeleteResult?[items.Count];
        observer.Stage(PhotoDeleteStage.Reading, 0, 0);

        // A read ahead still running hands over what it read (the delete reads the rest itself).
        await StopPrefetchAsync().ConfigureAwait(false);

        var plans = new Plan?[items.Count];
        if (await PhoneSideAsync(items, results, plans, ct).ConfigureAwait(false) is not { } side)
        {
            return Refuse(items, results, plans, "Nothing was deleted, because another selected item didn't match.");
        }

        var (phoneFiles, edited, phoneAssets) = (side.Files, side.Edited, side.Assets);

        // 2. Windows' side, then 3. the deletes: blocking COM calls, off the caller's thread.
        var run = await Task.Run(() => DeleteWithWindows(items, plans!, results, phoneFiles, edited, phoneAssets, observer, ct), ct).ConfigureAwait(false);

        // 4. Where they went: the photo index is rebuilt, and the phone's disk says whether the files stayed.
        var confirmed = run.Deleted.Count == 0 || await ClassifyAsync(items, plans!, results, run.Deleted, edited, observer).ConfigureAwait(false);

        // The kept listing: without what went when everything went as Windows said, else read again next time.
        if (run.Clean && confirmed)
        {
            _cache.Forget(run.Gone);
        }
        else
        {
            _cache.Invalidate();
        }

        return results.Select((r, i) => r ?? NotTried(items[i], plans[i]?.Asset, "Not tried.")).ToList();
    }

    private sealed record WindowsRun(List<int> Deleted, HashSet<string> Gone, bool Clean);

    /// <summary>Maps every item, then deletes batch by batch. Returns the indexes of the items deleted and the objects that went.</summary>
    private WindowsRun DeleteWithWindows(IReadOnlyList<PhotoDeleteItem> items, Plan[] plans, PhotoDeleteResult?[] results, IReadOnlyList<string> phoneFiles,
        HashSet<string> edited, IReadOnlyCollection<WpdPhoneAsset>? phoneAssets, IPhotoDeleteObserver observer, CancellationToken ct)
    {
        var deleted = new List<int>();
        var gone = new HashSet<string>(StringComparer.Ordinal);
        var clean = true;
        IWpdConnection? c = null;
        try
        {
            var (device, why) = FindDevice(ct);
            if (device is null)
            {
                FailAll(items, plans, results, DeviceErrorCodes.NotSupported, "Windows doesn't list the iPhone as a camera now. " + why);
                return new WindowsRun(deleted, gone, false);
            }

            c = _wpd!.Open(device.PnpId, readOnly: false);
            if (MapWithWindows(c, device.PnpId, plans, phoneFiles, phoneAssets, edited, observer, ct) is not { } map)
            {
                FailAll(items, plans, results, DeviceErrorCodes.Locked, "Windows doesn't show the iPhone's DCIM folder now. Unlock the iPhone and try again.");
                return new WindowsRun(deleted, gone, false);
            }

            for (var i = 0; i < plans.Length; i++)
            {
                if (plans[i].Refusal is { } refusal)
                {
                    results[i] = Mismatch(plans[i].Item, plans[i].Asset, refusal);
                }
            }

            if (results.Any(r => r is not null))
            {
                Refuse(items, results, plans, "Nothing was deleted, because another selected item didn't match.");
                return new WindowsRun(deleted, gone, true);
            }

            // The last moment a cancel stops everything: from the first batch on, it stops between batches.
            ct.ThrowIfCancellationRequested();
            observer.Stage(PhotoDeleteStage.Deleting, 0, plans.Length);
            for (var start = 0; start < plans.Length; start += BatchSize)
            {
                if (start > 0 && ct.IsCancellationRequested)
                {
                    Log($"stopped: cancelled after {deleted.Count} of {plans.Length}");
                    for (var i = start; i < plans.Length; i++)
                    {
                        results[i] ??= NotTried(items[i], plans[i].Asset, CancelledNote);
                    }

                    break;
                }

                var batch = Enumerable.Range(start, Math.Min(BatchSize, plans.Length - start)).ToList();
                var ok = DeleteBatch(c, map, plans, results, batch, deleted, gone, observer);
                observer.Stage(PhotoDeleteStage.Deleting, start + batch.Count, plans.Length);
                if (!ok)
                {
                    clean = false;
                    break;
                }
            }
        }
        catch (WpdException ex)
        {
            clean = false;
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

        return new WindowsRun(deleted, gone, clean);
    }

    /// <summary>What an item not reached says after a cancel part-way (the same words as PhotoDeletes.Cancelled).</summary>
    public const string CancelledNote = "Not tried: the delete was stopped.";

    /// <summary>
    /// Maps every plan to what Windows lists, reading as little as it can: the listing kept from the last few minutes, else
    /// only the folders that can hold the items (their own folder, or the month they were taken in); every folder, read
    /// fresh, when a match is missing; objects kept from earlier read again before they are used. Null when Windows
    /// shows no camera roll.
    /// </summary>
    private WpdDcimMap? MapWithWindows(IWpdConnection c, string pnpId, Plan[] plans, IReadOnlyList<string> phoneFiles,
        IReadOnlyCollection<WpdPhoneAsset>? phoneAssets, HashSet<string> edited, IPhotoDeleteObserver observer, CancellationToken ct)
    {
        var stats = new WpdReadStats();
        LastRead = stats;
        var reader = new WpdRollReader(c, stats, (done, total) => observer.Stage(PhotoDeleteStage.Reading, done, total), ct);
        var kept = _cache.Take(pnpId, out var generation);
        var listing = reader.Structure(kept, pnpId, _cache.Now, out _);
        var fresh = kept is null;
        var full = false;
        while (true)
        {
            if (listing is null)
            {
                _cache.Invalidate();
                return null;
            }

            var names = listing.Folders.Select(f => f.FileName ?? "").ToList();
            var byFolder = WpdDcimMap.ShowsPhoneFolders(names, phoneFiles);
            var folders = full ? listing.Folders : Narrow(listing, byFolder, plans);
            stats.FoldersInRoll = listing.Folders.Count;
            reader.Load(listing, folders, fresh);
            var map = new WpdDcimMap(listing.ToDcim(), phoneFiles, phoneAssets, names);
            MapAll(map, plans, edited);

            if (!(full && fresh) && plans.Any(p => p.Problem is WpdMapProblem.Absent or WpdMapProblem.NoFolder))
            {
                // Not where its month (or folder) says, or not in the listing kept from earlier: look everywhere, fresh.
                stats.FreshBecause = fresh ? "an item isn't in the folders its date points to" : "an item isn't in the listing kept from earlier";
                if (!fresh)
                {
                    listing = reader.Structure(null, pnpId, _cache.Now, out _);
                    fresh = true;
                }

                full = true;
                continue;
            }

            if (!fresh && plans.Any(p => p.Refusal is not null))
            {
                // A refusal on what was read earlier may only be out of date: read those folders again before refusing.
                listing = reader.Structure(null, pnpId, _cache.Now, out _);
                fresh = true;
                continue;
            }

            if (plans.All(p => p.Refusal is null) && !Verified(reader, listing, map, plans))
            {
                stats.FreshBecause = "an object kept from earlier changed";
                listing = reader.Structure(null, pnpId, _cache.Now, out _);
                fresh = true;
                continue;
            }

            if (full && fresh)
            {
                // Read everywhere, fresh: an item Windows lists nowhere can't be deleted from a PC at all. On the owner's
                // iPhone 16 that was 2,361 of 4,376 DCIM files (the ones moved over from an earlier iPhone).
                foreach (var plan in plans.Where(p => p.Problem == WpdMapProblem.Absent))
                {
                    plan.Refusal = $"Windows doesn't list {Path.GetFileName(plan.Item.Id)} anywhere in the iPhone's camera roll, so this PC can't delete it. Delete it in the Photos app on the iPhone.";
                }
            }

            Log($"read Windows' view for {plans.Length} item(s) ({(byFolder ? "by folder" : "by month")}): {stats}");
            _cache.Keep(listing, generation);
            return map;
        }
    }

    /// <summary>
    /// The folders that can hold the items: when Windows shows the phone's own folders, theirs; when it shows month folders
    /// ("202610_a"), the month each was taken in (two at a month's edge, for any time zone), plus every folder not named
    /// after a month. Every folder when an item's date isn't known.
    /// </summary>
    private static IReadOnlyList<WpdObject> Narrow(WpdListing listing, bool byFolder, Plan[] plans) =>
        WpdMonths.FoldersFor(listing.Folders, byFolder, plans.Select(p => (p.Item.Id, p.PairPath, p.Asset.TakenAt)).ToList());

    /// <summary>Maps each plan to its still (and Live Photo video, and edits) in this map, or says why not.</summary>
    private static void MapAll(WpdDcimMap map, Plan[] plans, HashSet<string> edited)
    {
        foreach (var plan in plans)
        {
            plan.Clear();
            var still = map.Find(plan.Item.Id, plan.DiskSize, plan.Asset.Size > 0 ? plan.Asset.TakenAt : null);
            if (!still.Ok)
            {
                plan.Refusal = still.Message;
                plan.Problem = still.Problem;
                continue;
            }

            plan.Still = still.Object;
            var renamed = still.Message.Length > 0;
            if (renamed)
            {
                plan.Note = still.Message;
            }

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
                else if (renamed && pair.Problem == WpdMapProblem.Absent && map.CompanionOf(plan.Still!, plan.PairPath, plan.PairDiskSize!.Value) is { } companion)
                {
                    // The still is shown under a name of the phone's making; its video carries the same name.
                    plan.Pair = companion;
                }
                else if (pair.Problem == WpdMapProblem.Absent)
                {
                    // Windows doesn't list the video half: the phone shows Live Photos as stills only. The still goes;
                    // whether its video went with it is checked on the phone afterwards.
                    plan.Note = "Windows doesn't list its Live Photo video.";
                }
                else
                {
                    plan.Refusal = "Its Live Photo video doesn't match: " + pair.Message;
                    plan.Problem = pair.Problem;
                    continue;
                }
            }

            if (edited.Contains(plan.Item.Id))
            {
                plan.Edits = map.EditsOf(plan.Still!);
            }
        }
    }

    /// <summary>Objects the plans use that were read on an earlier connection are read again; false when one changed or is gone.</summary>
    private static bool Verified(WpdRollReader reader, WpdListing listing, WpdDcimMap map, Plan[] plans)
    {
        var used = plans.SelectMany(p => new[] { p.Still, p.Pair }).Where(o => o is not null).Select(o => o!)
            .Where(o => map.FolderOf(o) is { } folder && !listing.IsFresh(folder.Id))
            .ToList();
        if (used.Count == 0)
        {
            return true;
        }

        var now = reader.ReadAgain(used.Select(o => o.Id).ToList()).ToDictionary(o => o.Id, StringComparer.Ordinal);
        return used.All(o => now.TryGetValue(o.Id, out var again) && SameFile(o, again, map.FolderOf(o)));
    }

    /// <summary>The object read now is still the file the map found: a file, the same name and size, in the same folder, the same persistent id.</summary>
    private static bool SameFile(WpdObject expected, WpdObject? now, WpdFolder? folder) =>
        now is not null && now.Kind == WpdObjectKind.File && now.Size == expected.Size
        && string.Equals(now.FileName, expected.FileName, StringComparison.OrdinalIgnoreCase)
        && folder is not null && (now.ParentId is null || now.ParentId == folder.Id)
        && (now.PersistentId is null || expected.PersistentId is null || now.PersistentId == expected.PersistentId);

    /// <summary>Re-checks and deletes one batch. False when it stopped (a mismatch or an error).</summary>
    private bool DeleteBatch(IWpdConnection c, WpdDcimMap map, Plan[] plans, PhotoDeleteResult?[] results, List<int> batch, List<int> deleted,
        HashSet<string> gone, IPhotoDeleteObserver observer)
    {
        // Just before deleting: each object is still the same file in the same DCIM folder.
        foreach (var i in batch)
        {
            var plan = plans[i];
            foreach (var expected in new[] { plan.Still, plan.Pair }.Where(o => o is not null).Select(o => o!))
            {
                if (!SameFile(expected, c.Get(expected.Id), map.FolderOf(expected)))
                {
                    results[i] = Mismatch(plan.Item, plan.Asset, $"{expected.FileName} changed on the iPhone just before the delete.");
                    observer.Result(i, results[i]!);
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

        observer.Deleting(batch);
        var outcome = c.Delete(ids);
        var stop = false;
        foreach (var i in batch)
        {
            var plan = plans[i];
            var stillHr = outcome[owner.FindIndex(o => o.Item == i && o.IsStill)];
            if (stillHr != 0)
            {
                results[i] = new PhotoDeleteResult(plan.Item.Id, plan.Asset.FileName, plan.Item.Size, PhotoDeleteOutcome.Failed, Code(stillHr),
                    Sentence(WpdErrors.Describe(stillHr)), [], PhotoDeleteDestination.Unknown);
                observer.Result(i, results[i]!);
                Log($"failed {plan.Item.Id} ({Bytes(plan.DiskSize)}): {WpdErrors.Describe(stillHr)}");
                stop = true;
                continue;
            }

            // Windows said it's done. Reading the object back on this connection isn't proof either way: on iOS 27 the
            // driver still answered with the deleted objects' properties. What the phone itself shows decides (ClassifyAsync).
            Log($"deleted {plan.Item.Id} ({Bytes(plan.DiskSize)}) through Windows, object {plan.Still!.Id} ({plan.Still.FileName})");
            gone.Add(plan.Still.Id);
            gone.UnionWith(plan.Edits.Select(o => o.Id));
            var also = new List<string>();
            var freed = plan.Item.Size;
            if (plan.Pair is { } pair)
            {
                plan.PairHr = outcome[owner.FindIndex(o => o.Item == i && !o.IsStill)];
                gone.Add(pair.Id);
                if (plan.PairHr == 0)
                {
                    also.Add(pair.FileName ?? Path.GetFileName(plan.PairPath!));
                    freed += plan.PairDiskSize ?? 0;
                    Log($"deleted {plan.PairPath} ({Bytes(plan.PairDiskSize ?? 0)}), the Live Photo's video, object {pair.Id}");
                }
            }

            results[i] = new PhotoDeleteResult(plan.Item.Id, plan.Asset.FileName, freed, PhotoDeleteOutcome.Deleted, null, plan.Note, also, PhotoDeleteDestination.Unknown);
            observer.Result(i, results[i]!);
            deleted.Add(i);
        }

        return !stop;
    }

    /// <summary>
    /// What the phone shows after the delete: the file gone from DCIM (deleted for good), or kept but no longer in the
    /// library (Recently Deleted). A photo the phone still has and still lists, after a few tries, wasn't deleted.
    /// Live Photo videos and edits are told from the same view. It runs to the end even after a cancel (it only reads);
    /// when the phone can't be asked, Windows' word stands. False when the phone showed otherwise or couldn't be asked.
    /// </summary>
    private async Task<bool> ClassifyAsync(IReadOnlyList<PhotoDeleteItem> items, Plan?[] plans, PhotoDeleteResult?[] results, List<int> deleted, HashSet<string> edited,
        IPhotoDeleteObserver observer)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = limit.Token;
        observer.Stage(PhotoDeleteStage.Checking, 0, deleted.Count);
        IReadOnlyDictionary<string, long?> disk = new Dictionary<string, long?>();
        string? notChecked = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            int pending;
            try
            {
                var index = await _phone.GetIndexAsync(refresh: true, ct).ConfigureAwait(false);
                var listed = index.Assets.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
                var paths = deleted.SelectMany(i => plans[i]!.PairPath is { } pair ? new[] { items[i].Id, pair } : [items[i].Id]).ToList();
                disk = await _phone.StatAsync(paths, ct).ConfigureAwait(false);
                pending = 0;
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
            }
            catch (Exception ex) when (ex is DeviceException or IOException or OperationCanceledException)
            {
                notChecked = ex is OperationCanceledException ? "the iPhone took too long to answer" : ex.Message;
                break;
            }

            observer.Stage(PhotoDeleteStage.Checking, deleted.Count - pending, deleted.Count);
            if (pending == 0)
            {
                break;
            }

            await _delay(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }

        if (notChecked is not null)
        {
            // Windows said each of these went; the phone couldn't be asked, so where they went stays unknown.
            Log($"couldn't check on the phone after the delete: {notChecked}");
            foreach (var i in deleted)
            {
                var r = results[i]!;
                results[i] = r with { WentTo = PhotoDeleteDestination.Unknown, Message = Join(r.Message, "DeviceDeck couldn't check on the iPhone afterwards.") };
            }

            observer.Stage(PhotoDeleteStage.Checking, deleted.Count, deleted.Count);
            return false;
        }

        var confirmed = true;
        foreach (var i in deleted.ToList())
        {
            var r = results[i]!;
            var plan = plans[i]!;
            if (r.WentTo == PhotoDeleteDestination.Unknown)
            {
                results[i] = r with
                {
                    Outcome = PhotoDeleteOutcome.Failed,
                    Code = DeviceErrorCodes.Protocol,
                    Message = Join(plan.Note, "Windows said it was deleted, but the iPhone still has it and still lists it."),
                    Also = [],
                    Size = plan.Item.Size,
                };
                deleted.Remove(i);
                confirmed = false;
                observer.Result(i, results[i]!);
                Log($"not deleted after all, {r.Id}: the phone still has the file and still lists it");
                continue;
            }

            var notes = new List<string>();
            if (plan.Note is { } note)
            {
                notes.Add(note);
            }

            var also = r.Also.ToList();
            var freed = r.Size;
            if (plan.PairPath is { } pairPath)
            {
                var pairName = plan.Pair?.FileName ?? Path.GetFileName(pairPath);
                var pairGone = disk.GetValueOrDefault(pairPath) is null;
                if (pairGone && !also.Contains(pairName))
                {
                    also.Add(pairName);
                    freed += plan.PairDiskSize ?? 0;
                    Log($"deleted {pairPath} ({Bytes(plan.PairDiskSize ?? 0)}), the Live Photo's video, gone with its still");
                }
                else if (!pairGone && r.WentTo == PhotoDeleteDestination.Permanent)
                {
                    also.Remove(pairName);
                    notes.Add($"Its Live Photo video ({Path.GetFileName(pairPath)}) is still on the iPhone.");
                    Log($"kept {pairPath}: still in DCIM");
                }
            }

            if (plan.Edits.Count > 0 || edited.Contains(r.Id))
            {
                // Not deleted by DeviceDeck: the edit belongs to the photo, which the library no longer lists.
                var names = plan.Edits.Count > 0 ? $" ({string.Join(", ", plan.Edits.Select(o => o.FileName))})" : "";
                notes.Add($"Its edit{names} went with it.");
            }

            results[i] = r with { Also = also, Size = freed, Message = notes.Count == 0 ? null : string.Join(" ", notes) };
            observer.Result(i, results[i]!);
            Log($"after the delete, {r.Id}: " + (r.WentTo == PhotoDeleteDestination.Permanent
                ? "the file is gone from DCIM"
                : "the file is still in DCIM and the library no longer lists it (Recently Deleted)"));
        }

        var seen = deleted.Select(i => results[i]!.WentTo).Where(d => d != PhotoDeleteDestination.Unknown).Distinct().ToList();
        if (seen.Count == 1)
        {
            _observed = seen[0];
        }

        return confirmed;
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
