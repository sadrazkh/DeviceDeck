using System.Diagnostics;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Cleanup;

/// <summary>
/// One analysis per phone at a time, kept in memory until the phone goes away, plus the confirmed clean-up actions.
/// The owner's rule, enforced here as well as in the page: nothing is deleted unless the user confirmed that exact
/// item at that exact size, the item is one the analysis listed, and it is still the same on the phone. With
/// "copy to PC first", nothing is deleted unless every copy finished.
/// </summary>
public sealed class CleanupService : IAsyncDisposable
{
    private readonly DeviceHub _hub;
    private readonly TransferQueue _transfers;
    private readonly SettingsStore _settings;
    private readonly StorageAnalyzer _analyzer;
    private readonly PhotoDelete.PhotoDeleteService _deletes;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public CleanupService(DeviceHub hub, TransferQueue transfers, SettingsStore settings, StorageAnalyzerOptions? options = null)
    {
        _hub = hub;
        _transfers = transfers;
        _settings = settings;
        _analyzer = new StorageAnalyzer(options);
        _deletes = new PhotoDelete.PhotoDeleteService(hub, transfers, settings);
        _hub.DeviceChanged += OnDeviceChanged;
        if (_deletes.Handler is { } handler)
        {
            handler.Finished += OnPhotosDeleted;
        }
    }

    /// <summary>A delete job (from here or from Photos) finished: what went leaves the report.</summary>
    private void OnPhotosDeleted(object? sender, PhotoDelete.PhotoDeleteFinished finished)
    {
        if (finished.Deleted.Count > 0)
        {
            var gone = finished.Deleted.ToHashSet(StringComparer.Ordinal);
            Mutate(finished.DeviceId, r => WithoutMedia(r, gone, finished.FreedBytes));
        }
    }

    /// <summary>Raised when a phone's status changes; progress is throttled to <see cref="ProgressInterval"/>.</summary>
    public event EventHandler<CleanupStatus>? Updated;

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    private sealed class Entry(string deviceId)
    {
        public CleanupStatus Status = Idle(deviceId);
        public CancellationTokenSource? Cts;
        public Task Run = Task.CompletedTask;
        public long LastEmit;
        public CleanupSection? LastSection;

        /// <summary>One clean-up action at a time per phone.</summary>
        public readonly SemaphoreSlim Action = new(1, 1);
    }

    private static CleanupStatus Idle(string deviceId) => new(deviceId, CleanupState.Idle, null, null, null, null);

    public CleanupStatus GetStatus(string deviceId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(deviceId, out var entry) ? entry.Status : Idle(deviceId);
        }
    }

    /// <summary>Starts an analysis, or returns the one already running.</summary>
    public CleanupStatus Analyze(string deviceId)
    {
        var device = _hub.GetUsableDevice(deviceId);
        CleanupStatus status;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var entry = EntryFor(deviceId);
            if (entry.Status.State == CleanupState.Running)
            {
                return entry.Status;
            }

            var cts = new CancellationTokenSource();
            entry.Cts = cts;
            entry.LastSection = null;
            entry.Status = new CleanupStatus(deviceId, CleanupState.Running, new CleanupProgress(CleanupSection.Storage, 0, 0), null, null, null);
            status = entry.Status;
            entry.Run = Task.Run(() => RunAsync(entry, device, cts), CancellationToken.None);
        }

        Updated?.Invoke(this, status);
        return status;
    }

    public void Cancel(string deviceId)
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _entries.TryGetValue(deviceId, out var entry) && entry.Status.State == CleanupState.Running ? entry.Cts : null;
        }

        TryCancel(cts);
    }

    private async Task RunAsync(Entry entry, IDevice device, CancellationTokenSource cts)
    {
        CleanupStatus final;
        try
        {
            var report = await _analyzer.AnalyzeAsync(device, (partial, progress) => Progress(entry, cts, partial, progress), cts.Token).ConfigureAwait(false);
            final = new CleanupStatus(entry.Status.DeviceId, CleanupState.Done, null, report, null, DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            final = entry.Status with { State = CleanupState.Cancelled, Progress = null, FinishedAt = DateTimeOffset.Now };
        }
        catch (DeviceException ex)
        {
            final = entry.Status with { State = CleanupState.Failed, Progress = null, Error = new TransferError(ex.Code, ex.Message), FinishedAt = DateTimeOffset.Now };
        }
        catch (Exception ex)
        {
            final = entry.Status with
            {
                State = CleanupState.Failed,
                Progress = null,
                Error = new TransferError("internal", $"The analysis stopped: {ex.Message}"),
                FinishedAt = DateTimeOffset.Now,
            };
        }

        CleanupStatus? publish = null;
        lock (_gate)
        {
            // Unless the phone went away meanwhile and the entry was reset.
            if (entry.Cts == cts)
            {
                entry.Status = final;
                entry.Cts = null;
                publish = final;
            }
        }

        cts.Dispose();
        if (publish is not null)
        {
            Updated?.Invoke(this, publish);
        }
    }

    private void Progress(Entry entry, CancellationTokenSource cts, CleanupReport partial, CleanupProgress progress)
    {
        CleanupStatus status;
        lock (_gate)
        {
            if (entry.Cts != cts || entry.Status.State != CleanupState.Running)
            {
                return;
            }

            entry.Status = entry.Status with { Report = partial, Progress = progress };
            var now = Stopwatch.GetTimestamp();
            var newSection = entry.LastSection != progress.Section;
            if (!newSection && Stopwatch.GetElapsedTime(entry.LastEmit, now) < ProgressInterval)
            {
                return;
            }

            entry.LastEmit = now;
            entry.LastSection = progress.Section;
            status = entry.Status;
        }

        Updated?.Invoke(this, status);
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        if (e.Change != DeviceChange.Removed && e.Summary.State != DeviceState.Disconnected)
        {
            return;
        }

        // A report describes the phone as it was; once it's gone, start again next time.
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!_entries.TryGetValue(e.Summary.Id, out var entry) || entry.Status.State == CleanupState.Idle)
            {
                return;
            }

            cts = entry.Cts;
            entry.Cts = null;
            entry.Status = Idle(e.Summary.Id);
        }

        TryCancel(cts);
        Updated?.Invoke(this, Idle(e.Summary.Id));
    }

    // ---------- Confirmed actions ----------

    /// <summary>
    /// Deletes photos and videos the analysis listed, as a transfer job (v0.4.1; the Transfers drawer shows it): Android
    /// through MediaStore, an iPhone through Windows' camera connection when it allows it. Items that changed since the
    /// analysis are left out and counted; the job matches the rest again and deletes nothing unless all of them match.
    /// What went leaves the report when the job finishes. No job (a null id) when nothing is left to delete.
    /// </summary>
    public Task<PhotoDelete.PhotoDeleteStarted> DeleteMediaAsync(string deviceId, IReadOnlyList<CleanupItem> items, long expectedBytes, string? copyFirstTo, CancellationToken ct)
    {
        RequireConfirmed(items.Select(i => i.Size), items.Select(i => i.Id), expectedBytes);
        return ActAsync(deviceId, async (device, report) =>
        {
            if (!StorageAnalyzer.CanDeleteMedia(device.Summary))
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, device.Summary.Platform == DevicePlatform.Ios
                    ? "This iPhone doesn't let the PC delete its photos right now. Copy them to the PC, then delete them in the Photos app on the iPhone."
                    : $"{device.Summary.Name} can't delete photos from the PC.");
            }

            var listed = ListedMedia(report);
            foreach (var item in items)
            {
                if (!listed.TryGetValue(item.Id, out var asset) || asset.Size != item.Size)
                {
                    throw new ArgumentException($"'{item.Id}' isn't a photo or video the analysis listed at that size. Run the analysis again.");
                }
            }

            // Still there, still the same size: otherwise it changed since the user saw it, and stays.
            var now = await ReadLibraryAsync(device, ct).ConfigureAwait(false);
            var verified = items.Where(i => now.TryGetValue(i.Id, out var a) && a.Size == i.Size).ToList();
            var skipped = items.Count - verified.Count;
            if (verified.Count == 0)
            {
                return new PhotoDelete.PhotoDeleteStarted(null, skipped);
            }

            return await _deletes.StartAsync(deviceId, verified.Select(i => new PhotoDeleteItem(i.Id, i.Size)).ToList(), verified.Sum(i => i.Size), copyFirstTo,
                PhotoDelete.PhotoDeleteSource.Cleanup, skipped, ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Deletes files in app folders the analysis listed.</summary>
    public Task<CleanupResult> DeleteFilesAsync(string deviceId, IReadOnlyList<CleanupFileItem> items, long expectedBytes, string? copyFirstTo, CancellationToken ct)
    {
        RequireConfirmed(items.Select(i => i.Size), items.Select(i => i.Root + "\n" + i.Path), expectedBytes);
        return ActAsync(deviceId, async (device, report) =>
        {
            var listed = (report.AppFiles?.Files ?? []).ToDictionary(f => (f.Root, f.Path));
            foreach (var item in items)
            {
                if (!listed.TryGetValue((item.Root, item.Path), out var hit) || hit.Size != item.Size)
                {
                    throw new ArgumentException($"'{item.Path}' isn't a file the analysis listed at that size. Run the analysis again.");
                }

                if (!StorageAnalyzer.CanDeleteIn(device.Summary.Capabilities, item.Root))
                {
                    throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't let the PC delete files there.");
                }
            }

            var verified = new List<CleanupFileItem>();
            foreach (var root in items.GroupBy(i => i.Root, StringComparer.Ordinal))
            {
                await using var fs = await device.OpenFileSystemAsync(root.Key, ct).ConfigureAwait(false);
                foreach (var item in root)
                {
                    if (await fs.StatAsync(item.Path, ct).ConfigureAwait(false) is { IsDirectory: false } entry && entry.Size == item.Size)
                    {
                        verified.Add(item);
                    }
                }
            }

            var skipped = items.Count - verified.Count;
            if (verified.Count == 0)
            {
                return new CleanupResult(0, skipped, 0, [], false);
            }

            var jobs = new List<string>();
            if (copyFirstTo is not null)
            {
                foreach (var root in verified.GroupBy(i => i.Root, StringComparer.Ordinal))
                {
                    var job = await _transfers.EnqueueAsync(deviceId, TransferKind.CopyToPc,
                        new CopyToPcRequest(root.Key, root.Select(i => i.Path).ToList(), copyFirstTo), ConflictRule.KeepBoth, ct).ConfigureAwait(false);
                    jobs.Add(job.Id);
                }

                if (!await WaitForCopiesAsync(jobs, ct).ConfigureAwait(false))
                {
                    return new CleanupResult(0, skipped, 0, jobs, true);
                }
            }

            var gone = new List<CleanupFileItem>();
            foreach (var root in verified.GroupBy(i => i.Root, StringComparer.Ordinal))
            {
                await using var fs = await device.OpenFileSystemAsync(root.Key, ct).ConfigureAwait(false);
                foreach (var item in root)
                {
                    await fs.DeleteAsync(item.Path, recursive: false, ct).ConfigureAwait(false);
                    if (await fs.StatAsync(item.Path, ct).ConfigureAwait(false) is null)
                    {
                        gone.Add(item);
                    }
                }
            }

            var freed = gone.Sum(i => i.Size);
            var goneKeys = gone.Select(i => (i.Root, i.Path)).ToHashSet();
            Mutate(deviceId, r => WithoutFiles(r, goneKeys, freed));
            return new CleanupResult(gone.Count, items.Count - gone.Count, freed, jobs, false);
        }, ct);
    }

    /// <summary>Uninstalls apps the analysis listed. With copy first (Android), each app's APK is saved to the PC before it goes; its data is not.</summary>
    public Task<CleanupResult> UninstallAppsAsync(string deviceId, IReadOnlyList<CleanupItem> items, long expectedBytes, string? copyFirstTo, CancellationToken ct)
    {
        RequireConfirmed(items.Select(i => i.Size), items.Select(i => i.Id), expectedBytes);
        return ActAsync(deviceId, async (device, report) =>
        {
            var caps = device.Summary.Capabilities;
            if (!caps.HasFlag(DeviceCapability.AppsUninstall))
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't remove apps from the PC.");
            }

            if (copyFirstTo is not null && !caps.HasFlag(DeviceCapability.AppsExport))
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, "Apps on this phone can't be copied to the PC.");
            }

            var listed = (report.Apps?.Largest ?? []).ToDictionary(a => a.Id, StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (!listed.TryGetValue(item.Id, out var app) || app.AppBytes + app.DataBytes != item.Size)
                {
                    throw new ArgumentException($"'{item.Id}' isn't an app the analysis listed at that size. Run the analysis again.");
                }
            }

            var installed = (await device.Apps.ListAsync(false, ct).ConfigureAwait(false)).ToDictionary(a => a.Id, StringComparer.Ordinal);
            var verified = items.Where(i => installed.TryGetValue(i.Id, out var a) && a.Source != AppSource.System).ToList();
            var skipped = items.Count - verified.Count;
            if (verified.Count == 0)
            {
                return new CleanupResult(0, skipped, 0, [], false);
            }

            var jobs = new List<string>();
            if (copyFirstTo is not null)
            {
                foreach (var item in verified)
                {
                    var job = await _transfers.EnqueueAsync(deviceId, TransferKind.AppExport, new AppExportRequest(item.Id, copyFirstTo), ConflictRule.KeepBoth, ct).ConfigureAwait(false);
                    jobs.Add(job.Id);
                }

                if (!await WaitForCopiesAsync(jobs, ct).ConfigureAwait(false))
                {
                    return new CleanupResult(0, skipped, 0, jobs, true);
                }
            }

            var gone = new List<CleanupItem>();
            DeviceException? firstError = null;
            foreach (var item in verified)
            {
                try
                {
                    await device.Apps.UninstallAsync(item.Id, ct).ConfigureAwait(false);
                    gone.Add(item);
                }
                catch (DeviceException ex) when (ex.Code is not (DeviceErrorCodes.Disconnected or DeviceErrorCodes.TrustRequired or DeviceErrorCodes.Unauthorized))
                {
                    firstError ??= ex;
                }
            }

            if (gone.Count == 0 && firstError is not null)
            {
                throw firstError;
            }

            var freed = gone.Sum(i => i.Size);
            var goneIds = gone.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            Mutate(deviceId, r => WithoutApps(r, goneIds, freed));
            return new CleanupResult(gone.Count, items.Count - gone.Count, freed, jobs, false);
        }, ct);
    }

    /// <summary>Clears every app's cache files (Android). Caches hold nothing personal; apps rebuild them as needed.</summary>
    public Task<CleanupResult> TrimCachesAsync(string deviceId, CancellationToken ct) =>
        ActAsync(deviceId, async (device, report) =>
        {
            if (device is not IAppCacheCleaner cleaner || report.Caches is null)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't let the PC clear app caches.");
            }

            var freed = await cleaner.TrimCachesAsync(ct).ConfigureAwait(false);
            var apps = report.Caches.Apps.Count;
            Mutate(deviceId, r => r with
            {
                FreeBytes = r.FreeBytes + freed,
                Caches = r.Caches is null ? null : r.Caches with { TrimmedBytes = freed, TrimmedAt = DateTimeOffset.Now },
            });
            return new CleanupResult(apps, 0, freed, [], false);
        }, ct);

    private async Task<T> ActAsync<T>(string deviceId, Func<IDevice, CleanupReport, Task<T>> act, CancellationToken ct)
    {
        var device = _hub.GetUsableDevice(deviceId);
        Entry entry;
        CleanupReport report;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entry = EntryFor(deviceId);
            if (entry.Status.State == CleanupState.Running)
            {
                throw new InvalidOperationException("Wait for the analysis to finish first.");
            }

            report = entry.Status.Report ?? throw new InvalidOperationException("Analyse the phone first: only what the analysis lists can be cleaned up.");
        }

        await entry.Action.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                // Another action may have finished while this one waited.
                report = entry.Status.Report ?? report;
            }

            return await act(device, report).ConfigureAwait(false);
        }
        finally
        {
            entry.Action.Release();
        }
    }

    /// <summary>What the page sends must add up to what it showed the user, item by item.</summary>
    private static void RequireConfirmed(IEnumerable<long> sizes, IEnumerable<string> keys, long expectedBytes)
    {
        var list = sizes.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("items is empty.");
        }

        if (list.Any(s => s < 0))
        {
            throw new ArgumentException("A size is negative.");
        }

        var keyList = keys.ToList();
        if (keyList.Distinct(StringComparer.Ordinal).Count() != keyList.Count)
        {
            throw new ArgumentException("An item is listed twice.");
        }

        if (list.Sum() != expectedBytes)
        {
            throw new ArgumentException($"expectedBytes ({expectedBytes}) isn't the total of the items ({list.Sum()}).");
        }
    }

    /// <summary>
    /// Waits for copy jobs to finish. True only when every job is done with every item copied; false as soon as one fails,
    /// is cancelled, or waits for an unplugged phone (it carries on by itself when the phone is back, but nothing is deleted).
    /// </summary>
    private async Task<bool> WaitForCopiesAsync(IReadOnlyList<string> jobIds, CancellationToken ct)
    {
        using var signal = new SemaphoreSlim(0);
        var watched = jobIds.ToHashSet(StringComparer.Ordinal);
        void OnJob(object? sender, TransferJob job)
        {
            if (watched.Contains(job.Id))
            {
                try
                {
                    signal.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        _transfers.JobUpdated += OnJob;
        try
        {
            while (true)
            {
                var jobs = jobIds.Select(_transfers.Get).ToList();
                if (jobs.Any(j => j is null || j.State is TransferState.Failed or TransferState.Cancelled or TransferState.WaitingForDevice or TransferState.NeedsDecision))
                {
                    return false;
                }

                if (jobs.All(j => j!.State == TransferState.Done))
                {
                    return jobs.All(j => j!.FailedItems == 0 && j.DoneItems == j.TotalItems);
                }

                await signal.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _transfers.JobUpdated -= OnJob;
        }
    }

    private static async Task<Dictionary<string, PhotoAsset>> ReadLibraryAsync(IDevice device, CancellationToken ct)
    {
        var all = new Dictionary<string, PhotoAsset>(StringComparer.Ordinal);
        for (var offset = 0; ;)
        {
            var page = await device.Photos.GetAssetsAsync(null, offset, 2000, ct).ConfigureAwait(false);
            foreach (var asset in page.Items)
            {
                all[asset.Id] = asset;
            }

            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total)
            {
                return all;
            }
        }
    }

    private static Dictionary<string, PhotoAsset> ListedMedia(CleanupReport report)
    {
        var listed = new Dictionary<string, PhotoAsset>(StringComparer.Ordinal);
        foreach (var asset in (report.Media?.LargestVideos ?? []).Concat(report.Media?.LargestPhotos ?? []).Concat((report.Duplicates?.Groups ?? []).SelectMany(g => g.Assets)))
        {
            listed[asset.Id] = asset;
        }

        return listed;
    }

    // ---------- Keeping the report in step with what was removed ----------

    private void Mutate(string deviceId, Func<CleanupReport, CleanupReport> change)
    {
        CleanupStatus status;
        lock (_gate)
        {
            if (!_entries.TryGetValue(deviceId, out var entry) || entry.Status.Report is not { } report)
            {
                return;
            }

            entry.Status = entry.Status with { Report = change(report) };
            status = entry.Status;
        }

        Updated?.Invoke(this, status);
    }

    private static CleanupReport WithoutMedia(CleanupReport r, HashSet<string> gone, long freed)
    {
        var media = r.Media;
        if (media is not null)
        {
            var listed = ListedMedia(r);
            var removed = gone.Select(id => listed.GetValueOrDefault(id)).Where(a => a is not null).Select(a => a!).ToList();
            var videos = removed.Where(a => a.Kind == PhotoKind.Video).ToList();
            var photos = removed.Where(a => a.Kind != PhotoKind.Video).ToList();
            media = media with
            {
                LargestVideos = media.LargestVideos.Where(a => !gone.Contains(a.Id)).ToList(),
                LargestPhotos = media.LargestPhotos.Where(a => !gone.Contains(a.Id)).ToList(),
                VideoCount = Math.Max(0, media.VideoCount - videos.Count),
                VideoBytes = Math.Max(0, media.VideoBytes - videos.Sum(a => a.Size)),
                PhotoCount = Math.Max(0, media.PhotoCount - photos.Count),
                PhotoBytes = Math.Max(0, media.PhotoBytes - photos.Sum(a => a.Size)),
            };
        }

        var duplicates = r.Duplicates;
        if (duplicates is not null)
        {
            var groups = duplicates.Groups
                .Select(g => g with { Assets = g.Assets.Where(a => !gone.Contains(a.Id)).ToList() })
                .Where(g => g.Assets.Count > 1)
                .ToList();
            duplicates = duplicates with { Groups = groups, ReclaimableBytes = groups.Sum(g => g.ReclaimableBytes) };
        }

        return r with { Media = media, Duplicates = duplicates, FreeBytes = r.FreeBytes + freed };
    }

    private static CleanupReport WithoutFiles(CleanupReport r, HashSet<(string Root, string Path)> gone, long freed)
    {
        if (r.AppFiles is not { } files)
        {
            return r;
        }

        var left = files.Files.Where(f => !gone.Contains((f.Root, f.Path))).ToList();
        return r with { AppFiles = files with { Files = left, TotalBytes = left.Sum(f => f.Size) }, FreeBytes = r.FreeBytes + freed };
    }

    private static CleanupReport WithoutApps(CleanupReport r, HashSet<string> gone, long freed)
    {
        var apps = r.Apps;
        if (apps is not null)
        {
            var removed = apps.Largest.Where(a => gone.Contains(a.Id)).ToList();
            apps = apps with
            {
                Largest = apps.Largest.Where(a => !gone.Contains(a.Id)).ToList(),
                Count = Math.Max(0, apps.Count - removed.Count),
                TotalBytes = Math.Max(0, apps.TotalBytes - removed.Sum(a => a.AppBytes + a.DataBytes)),
            };
        }

        var caches = r.Caches is null ? null : r.Caches with { Apps = r.Caches.Apps.Where(c => !gone.Contains(c.AppId)).ToList() };
        return r with { Apps = apps, Caches = caches, FreeBytes = r.FreeBytes + freed };
    }

    private Entry EntryFor(string deviceId)
    {
        if (!_entries.TryGetValue(deviceId, out var entry))
        {
            entry = new Entry(deviceId);
            _entries[deviceId] = entry;
        }

        return entry;
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task> runs;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _entries.Values)
            {
                TryCancel(entry.Cts);
            }

            runs = _entries.Values.Select(e => e.Run).ToList();
        }

        _hub.DeviceChanged -= OnDeviceChanged;
        if (_deletes.Handler is { } handler)
        {
            handler.Finished -= OnPhotosDeleted;
        }

        try
        {
            await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down: a run that won't stop must not hold up the exit.
        }
    }
}
