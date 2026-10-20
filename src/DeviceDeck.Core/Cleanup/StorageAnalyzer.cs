using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Cleanup;

public sealed class StorageAnalyzerOptions
{
    /// <summary>How many apps the Apps section lists.</summary>
    public int TopApps { get; init; } = 30;

    /// <summary>How many videos and how many photos the Media section lists.</summary>
    public int TopMedia { get; init; } = 40;

    /// <summary>App files smaller than this are left out.</summary>
    public long AppFileMinBytes { get; init; } = 10_000_000;

    public int TopAppFiles { get; init; } = 200;

    /// <summary>Entries one AppDocuments root may hold before the walk stops (the fallback walker only).</summary>
    public int MaxEntriesPerRoot { get; init; } = 20_000;

    /// <summary>The first pass of the duplicate search hashes this much of each file.</summary>
    public long DuplicatePrefixBytes { get; init; } = 64 * 1024;

    public int MaxDuplicateGroups { get; init; } = 500;

    /// <summary>Photo-library page size while reading the whole library.</summary>
    public int PageSize { get; init; } = 2000;
}

/// <summary>
/// Reads one phone and reports what takes room: the biggest apps, the largest videos and photos, exact duplicates,
/// big files in app folders and app caches. Read-only: nothing on the phone is changed.
/// Sections run one after another; <c>onProgress</c> gets the report so far after each step.
/// </summary>
public sealed class StorageAnalyzer(StorageAnalyzerOptions? options = null)
{
    private readonly StorageAnalyzerOptions _options = options ?? new StorageAnalyzerOptions();

    public StorageAnalyzerOptions Options => _options;

    public async Task<CleanupReport> AnalyzeAsync(IDevice device, Action<CleanupReport, CleanupProgress>? onProgress, CancellationToken ct)
    {
        var summary = device.Summary;
        var caps = summary.Capabilities;
        var notes = new List<CleanupNote>();
        var report = new CleanupReport(summary.Id, summary.Platform, DateTimeOffset.Now, null, null, null, null, null, null, null, notes);

        void Report(CleanupSection section, long done, long total) =>
            onProgress?.Invoke(report with { Notes = notes.ToArray() }, new CleanupProgress(section, done, total));

        // Storage: the headline figures.
        if (caps.HasFlag(DeviceCapability.Storage))
        {
            Report(CleanupSection.Storage, 0, 1);
            var storage = await Section(CleanupSection.Storage, notes, () => device.GetStorageAsync(ct)).ConfigureAwait(false);
            if (storage?.Volumes.FirstOrDefault() is { } volume)
            {
                report = report with { TotalBytes = volume.TotalBytes, FreeBytes = volume.FreeBytes };
            }
        }

        // Apps: the biggest user apps (system apps can't be removed).
        IReadOnlyList<AppInfo> apps = [];
        if (caps.HasFlag(DeviceCapability.AppsList))
        {
            Report(CleanupSection.Apps, 0, 1);
            apps = await Section(CleanupSection.Apps, notes, () => device.Apps.ListAsync(false, ct)).ConfigureAwait(false) ?? [];
            var user = apps.Where(a => a.Source != AppSource.System).ToList();
            report = report with
            {
                Apps = new CleanupApps(
                    user.OrderByDescending(a => a.AppBytes + a.DataBytes).ThenBy(a => a.Name, StringComparer.CurrentCulture).Take(_options.TopApps).ToList(),
                    user.Count,
                    user.Sum(a => a.AppBytes + a.DataBytes),
                    caps.HasFlag(DeviceCapability.AppsUninstall),
                    caps.HasFlag(DeviceCapability.AppsExport)),
            };
        }

        var names = apps.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);

        // Media: every photo and video, then the largest of each.
        IReadOnlyList<PhotoAsset> library = [];
        if (caps.HasFlag(DeviceCapability.PhotosRead))
        {
            Report(CleanupSection.Media, 0, 0);
            library = await Section(CleanupSection.Media, notes, () => ReadLibraryAsync(device, (done, total) => Report(CleanupSection.Media, done, total), ct)).ConfigureAwait(false) ?? [];
            var local = library.Where(a => !a.InCloudOnly).ToList();
            var videos = local.Where(a => a.Kind == PhotoKind.Video).ToList();
            var photos = local.Where(a => a.Kind != PhotoKind.Video).ToList();
            report = report with
            {
                Media = new CleanupMedia(
                    Largest(videos),
                    Largest(photos),
                    videos.Count,
                    videos.Sum(a => a.Size),
                    photos.Count,
                    photos.Sum(a => a.Size),
                    library.Count - local.Count,
                    CanDeleteMedia(summary)),
            };
        }

        // Caches: Android only; the phone counts them about once a day.
        if (device is IAppCacheCleaner cleaner)
        {
            Report(CleanupSection.Caches, 0, 1);
            var caches = await Section(CleanupSection.Caches, notes, () => cleaner.GetCachesAsync(ct), quietNotSupported: true).ConfigureAwait(false);
            if (caches is not null)
            {
                report = report with
                {
                    Caches = new CleanupCaches(
                        caches.TotalBytes,
                        caches.Apps.Where(a => a.Bytes > 0).OrderByDescending(a => a.Bytes)
                            .Select(a => new CleanupCacheApp(a.AppId, names.GetValueOrDefault(a.AppId), a.Bytes)).ToList(),
                        null,
                        null),
                };
            }
        }

        // Big files in app folders.
        var scanner = device as IAppFileScanner;
        if (scanner is not null || caps.HasFlag(DeviceCapability.AppDocuments))
        {
            Report(CleanupSection.AppFiles, 0, 0);
            var scan = await Section(CleanupSection.AppFiles, notes, () => ScanAppFilesAsync(device, scanner, (done, total) => Report(CleanupSection.AppFiles, done, total), ct)).ConfigureAwait(false);
            if (scan is not null)
            {
                if (scan.Unreadable.Count > 0)
                {
                    notes.Add(new CleanupNote(CleanupSection.AppFiles, DeviceErrorCodes.PermissionDenied,
                        $"The phone wouldn't let these folders be read: {string.Join(", ", scan.Unreadable)}."));
                }

                var appNames = scan.Files.Where(f => f.AppId is not null).Select(f => f.AppId!).Distinct(StringComparer.Ordinal)
                    .ToDictionary(id => id, id => names.GetValueOrDefault(id) ?? id, StringComparer.Ordinal);
                report = report with
                {
                    AppFiles = new CleanupAppFiles(scan.Files, scan.Files.Sum(f => f.Size), _options.AppFileMinBytes, appNames, scan.Files.All(f => CanDeleteIn(caps, f.Root))),
                };
            }
        }

        // Duplicates: last, because it may read a lot.
        if (library.Count > 1)
        {
            Report(CleanupSection.Duplicates, 0, 0);
            var found = await Section(CleanupSection.Duplicates, notes, () => DuplicateFinder.FindAsync(
                device,
                library,
                _options.DuplicatePrefixBytes,
                _options.MaxDuplicateGroups,
                (done, total) => Report(CleanupSection.Duplicates, done, total),
                ct)).ConfigureAwait(false);
            if (found is not null)
            {
                if (found.Unreadable > 0)
                {
                    notes.Add(new CleanupNote(CleanupSection.Duplicates, DeviceErrorCodes.PathNotFound,
                        $"{found.Unreadable} photos or videos couldn't be read, so they weren't compared."));
                }

                report = report with
                {
                    Duplicates = new CleanupDuplicates(found.Groups, found.Groups.Sum(g => g.ReclaimableBytes), found.HashedCount, found.HashedBytes, CanDeleteMedia(summary)),
                };
            }
        }

        return report with { Notes = notes.ToArray() };
    }

    /// <summary>Media can be deleted from the PC only through Android's MediaStore; an iPhone's library is left to the Photos app.</summary>
    public static bool CanDeleteMedia(DeviceSummary summary) =>
        summary.Platform == DevicePlatform.Android && summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete);

    /// <summary>Files in an app's own root ("app:…") need AppDocuments; files in shared storage need Files.</summary>
    public static bool CanDeleteIn(DeviceCapability caps, string root) =>
        caps.HasFlag(root.StartsWith("app:", StringComparison.Ordinal) ? DeviceCapability.AppDocuments : DeviceCapability.Files);

    private List<PhotoAsset> Largest(List<PhotoAsset> assets) =>
        assets.OrderByDescending(a => a.Size).ThenBy(a => a.Id, StringComparer.Ordinal).Take(_options.TopMedia).ToList();

    private async Task<IReadOnlyList<PhotoAsset>> ReadLibraryAsync(IDevice device, Action<long, long> progress, CancellationToken ct)
    {
        var all = new List<PhotoAsset>();
        for (var offset = 0; ;)
        {
            var page = await device.Photos.GetAssetsAsync(null, offset, _options.PageSize, ct).ConfigureAwait(false);
            all.AddRange(page.Items);
            offset += page.Items.Count;
            progress(offset, page.Total);
            if (page.Items.Count == 0 || offset >= page.Total)
            {
                break;
            }
        }

        // A library read page by page can list one asset twice if it changed in between.
        return all.DistinctBy(a => a.Id, StringComparer.Ordinal).ToList();
    }

    private async Task<AppFileScan> ScanAppFilesAsync(IDevice device, IAppFileScanner? scanner, Action<long, long> progress, CancellationToken ct)
    {
        if (scanner is not null)
        {
            try
            {
                return await scanner.FindLargeAppFilesAsync(_options.AppFileMinBytes, _options.TopAppFiles, ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.NotSupported && device.Summary.Capabilities.HasFlag(DeviceCapability.AppDocuments))
            {
                // Fall through to the walk.
            }
        }

        return await AppFileWalker.ScanAsync(device, _options.AppFileMinBytes, _options.TopAppFiles, _options.MaxEntriesPerRoot, (d, t) => progress(d, t), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one section. A section the phone refuses (locked folder, unsupported, odd answer) becomes a note and the
    /// rest carries on; a phone that is gone or untrusted ends the whole analysis.
    /// </summary>
    private static async Task<T?> Section<T>(CleanupSection section, List<CleanupNote> notes, Func<Task<T>> run, bool quietNotSupported = false) where T : class
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported && quietNotSupported)
        {
            return null;
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.NotSupported or DeviceErrorCodes.Protocol or DeviceErrorCodes.PathNotFound)
        {
            notes.Add(new CleanupNote(section, ex.Code, ex.Message));
            return null;
        }
    }
}
