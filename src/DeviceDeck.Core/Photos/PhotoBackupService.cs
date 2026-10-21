using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Core.Photos;

/// <summary>
/// Incremental photo backup to the PC, per phone. The host builds one, hands <see cref="Handler"/> to the transfer
/// queue and then calls <see cref="Attach"/>. <see cref="CountNewAsync"/> is the call for a "new photos" notification.
/// </summary>
public sealed class PhotoBackupService : IDisposable
{
    private readonly DeviceHub _hub;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Func<string, PcDiskSpace?> _diskSpace;
    private readonly Func<string, bool> _rootExists;
    private TransferQueue? _queue;
    private HeicConversion _heic;

    /// <param name="diskSpace">Free space of a folder's drive (tests pass their own); <see cref="PhotoBackupSpaces.Probe"/> by default.</param>
    /// <param name="rootExists">Whether a folder's drive or share is there; <see cref="PhotoBackupSpaces.RootExists"/> by default.</param>
    public PhotoBackupService(string databasePath, DeviceHub hub, SettingsStore settings, IImageTranscoder? transcoder,
        HeicConversion heicConversion = HeicConversion.Unknown, TimeProvider? time = null,
        Func<string, PcDiskSpace?>? diskSpace = null, Func<string, bool>? rootExists = null)
    {
        _hub = hub;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _diskSpace = diskSpace ?? PhotoBackupSpaces.Probe;
        _rootExists = rootExists ?? PhotoBackupSpaces.RootExists;
        _heic = transcoder is null ? HeicConversion.MissingCodec : heicConversion;
        Ledger = new PhotoBackupLedger(databasePath);
        Handler = new PhotosBackupHandler(Ledger, transcoder, _time);
        Handler.HeicConversionFailed += (_, _) => _heic = HeicConversion.MissingCodec;
    }

    public PhotoBackupLedger Ledger { get; }

    /// <summary>Register with the transfer queue (TransferKind.PhotosBackup).</summary>
    public PhotosBackupHandler Handler { get; }

    public HeicConversion HeicConversion => _heic;

    /// <summary>A backup job finished (done, failed or cancelled), with what it copied.</summary>
    public event EventHandler<PhotoBackupFinished>? RunFinished;

    /// <summary>Leftover temp files of backups that will never resume were removed (for the log).</summary>
    public event EventHandler<PhotoBackupCleanup>? CleanedUp;

    /// <summary>Starts following the queue's backup jobs, so each run's state and counts are kept.</summary>
    public void Attach(TransferQueue queue)
    {
        lock (_gate)
        {
            if (_queue is not null)
            {
                return;
            }

            _queue = queue;
        }

        queue.JobUpdated += OnJobUpdated;

        // Jobs that finished while the app was closed (or before this was attached).
        foreach (var job in queue.List().Where(j => j.Kind == TransferKind.PhotosBackup))
        {
            OnJobUpdated(queue, job);
        }

        // What a crash or a kill left behind in the backup folders: their downloads start over anyway.
        CleanupTask = Task.Run(() => CleanUpPartials());
    }

    /// <summary>The clean-up started last (at attach, or after a run): tests wait for it.</summary>
    internal Task CleanupTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Removes the photo backup's own unfinished downloads (<see cref="PhotosBackupHandler.TempSuffixes"/>; nothing else)
    /// from the folders backups went to, where no backup is queued, running, paused or waiting: no job will resume them
    /// (a download always starts over). <paramref name="only"/> limits it to one folder. Never touches anything else.
    /// </summary>
    public PhotoBackupCleanup CleanUpPartials(string? only = null)
    {
        var busy = (_queue?.List() ?? [])
            .Where(j => j.Kind == TransferKind.PhotosBackup && j.State is not (TransferState.Done or TransferState.Failed or TransferState.Cancelled))
            .Select(j => Ledger.GetRun(j.Id)?.Destination)
            .OfType<string>()
            .ToList();
        var removed = new List<string>();
        long bytes = 0;
        foreach (var folder in Ledger.Destinations().Where(d => only is null || SameFolder(d, only)))
        {
            if (busy.Any(b => SameFolder(b, folder) || IsInside(b, folder) || IsInside(folder, b)) || !Directory.Exists(folder))
            {
                continue;
            }

            IEnumerable<string> partials;
            try
            {
                partials = Directory.EnumerateFiles(folder, "*" + TransferPaths.PartialSuffix,
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint })
                    .Where(f => PhotosBackupHandler.TempSuffixes.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in partials)
            {
                try
                {
                    // One written in the last minute may belong to a backup that started just now.
                    var info = new FileInfo(file);
                    if (_time.GetUtcNow().UtcDateTime - info.LastWriteTimeUtc < TimeSpan.FromMinutes(1))
                    {
                        continue;
                    }

                    var length = info.Length;
                    File.Delete(file);
                    removed.Add(file);
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or locked: next time.
                }
            }
        }

        var result = new PhotoBackupCleanup(removed, bytes);
        if (removed.Count > 0)
        {
            CleanedUp?.Invoke(this, result);
        }

        return result;

        static bool SameFolder(string a, string b) =>
            string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

        static bool IsInside(string inner, string outer) =>
            inner.StartsWith(Path.TrimEndingDirectorySeparator(outer) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How many photos and videos on the phone this PC hasn't backed up. Needs the phone ready (unlocked iPhone);
    /// on an iPhone the first call after plugging in reads the photo library index (a few seconds; later calls reuse it).
    /// </summary>
    public async Task<PhotoBackupCount> CountNewAsync(string deviceId, CancellationToken ct)
    {
        var device = Require(deviceId);
        return (await CountAsync(device, EffectiveOptions(device).Options, ct).ConfigureAwait(false)).Count;
    }

    public async Task<PhotoBackupStatus> GetStatusAsync(string deviceId, CancellationToken ct)
    {
        var device = Require(deviceId);
        var (options, customized) = EffectiveOptions(device);
        var (count, copyBytes) = await CountAsync(device, options, ct).ConfigureAwait(false);
        return new PhotoBackupStatus(
            deviceId,
            count,
            options,
            customized,
            Ledger.CountBackedUp(deviceId),
            Ledger.GetLastRun(deviceId),
            ActiveJob(deviceId)?.Id,
            _heic,
            CheckSpace(options.Destination, copyBytes),
            Ledger.GetOnConnect(deviceId));
    }

    /// <summary>Room on the drive of <paramref name="folder"/> for <paramref name="bytes"/> (0: is the drive there, is it nearly full).</summary>
    public PhotoBackupSpace CheckSpace(string folder, long bytes = 0)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException("folder must be a full folder path on this PC.");
        }

        return PhotoBackupSpaces.Check(folder, bytes, _diskSpace, _rootExists);
    }

    /// <summary>Refuses a backup the destination can't take: its drive isn't there, or the new photos won't fit with room to spare.</summary>
    private static void RequireRoom(PhotoBackupSpace space, PhotoBackupCount count)
    {
        if (space.DriveMissing)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound,
                $"The backup folder's drive {space.Root} isn't there. Connect it, or choose another folder.");
        }

        if (!space.Enough && count.NewCount + count.NewEdits > 0)
        {
            throw new DeviceException(BackupErrors.NoSpace,
                $"Not enough space on {space.Root} for {count.NewCount:N0} new photos: about {Gb(space.NeededBytes)} needed with room to spare, " +
                $"{Gb(space.FreeBytes ?? 0)} free. Free some space, or choose another folder.");
        }
    }

    private static string Gb(long bytes) => bytes >= 1_000_000_000
        ? (bytes / 1e9).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : (bytes / 1e6).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    /// <summary>
    /// Saves the phone's own options; a null value keeps what it had. <paramref name="useDefaults"/> drops them all
    /// first, so the phone follows Settings again (its destination too, and the range goes back to everything).
    /// </summary>
    public void Configure(string deviceId, string? destination, string? folderPattern, bool? convertHeic, bool? includeHidden, bool useDefaults = false,
        PhotoBackupRange? range = null, PhotoBackupOnConnect? onConnect = null, bool? saveEdited = null)
    {
        if (onConnect is { } mode)
        {
            if (!Enum.IsDefined(mode))
            {
                throw new ArgumentException("onConnect must be \"nothing\", \"notify\" or \"backUp\".");
            }

            // Not one of the options Settings has defaults for: "Use the Settings defaults" leaves it alone.
            Ledger.SetOnConnect(deviceId, mode);
        }

        if (destination is not null)
        {
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
            {
                throw new ArgumentException("destination must be a full folder path on this PC.");
            }
        }

        if (folderPattern is not null && !PhotoBackupNaming.IsValidPattern(folderPattern))
        {
            throw new ArgumentException("folderPattern must be \"\" or yyyy, MM and dd joined by / - _ . or a space.");
        }

        range = range?.Validated();
        var saved = useDefaults ? new PhotoBackupOwnOptions() : Ledger.GetOptions(deviceId);
        Ledger.SetOptions(deviceId, new PhotoBackupOwnOptions(
            destination ?? saved.Destination,
            folderPattern ?? saved.Pattern,
            convertHeic ?? saved.ConvertHeic,
            includeHidden ?? saved.IncludeHidden,
            range ?? saved.Range,
            saveEdited ?? saved.SaveEdited));
    }

    /// <summary>
    /// Queues a backup of everything new on the phone with its current options, or returns the backup job already
    /// queued or running for it. The destination is pinned to the phone on its first backup, so renaming the
    /// phone later doesn't start a second folder.
    /// </summary>
    public Task<TransferJob> StartAsync(string deviceId, CancellationToken ct) => StartAsync(deviceId, PhotoBackupTrigger.Manual, ct);

    /// <summary>
    /// As <see cref="StartAsync(string, CancellationToken)"/>, saying what started it: a run started by the "Back up now"
    /// notification or by the phone connecting ends with a notification of its own.
    /// </summary>
    public async Task<TransferJob> StartAsync(string deviceId, PhotoBackupTrigger trigger, CancellationToken ct)
    {
        var queue = _queue ?? throw new InvalidOperationException("The photo backup isn't attached to the transfer queue.");
        var device = Require(deviceId);
        if (ActiveJob(deviceId) is { } running)
        {
            return running;
        }

        var (options, _) = EffectiveOptions(device);
        var (count, copyBytes) = await CountAsync(device, options, ct).ConfigureAwait(false);
        RequireRoom(CheckSpace(options.Destination, copyBytes), count);
        if (Ledger.GetOptions(deviceId).Destination is null)
        {
            Configure(deviceId, options.Destination, null, null, null);
        }

        var request = new PhotoBackupRequest(options.Destination, options.FolderPattern, options.ConvertHeic, options.IncludeHidden, options.Range, trigger,
            options.SaveEdited);
        var job = await queue.EnqueueAsync(deviceId, TransferKind.PhotosBackup, request, ConflictRule.KeepBoth, ct).ConfigureAwait(false);
        Ledger.StartRun(job.Id, deviceId, options.Destination, job.CreatedAt, trigger);

        // The job may have got ahead of this (nothing new: done at once); catch its run up with it.
        if (queue.Get(job.Id) is { } now)
        {
            OnJobUpdated(queue, now);
        }

        return job;
    }

    /// <summary>Forgets which photos were backed up from the phone; the files on the PC are not touched.</summary>
    public int Forget(string deviceId) => Ledger.Forget(deviceId);

    /// <summary>The options for this phone: its own where it has them, else Settings (destination: the Settings folder plus the phone's name).</summary>
    public (PhotoBackupOptions Options, bool Customized) EffectiveOptions(IDevice device)
    {
        var settings = _settings.Current;
        var own = Ledger.GetOptions(device.Summary.Id);
        var options = new PhotoBackupOptions(
            own.Destination ?? Path.Combine(settings.PhotoBackupFolder, PhotoBackupNaming.SafeName(device.Summary.Name)),
            own.Pattern ?? settings.PhotoBackupPattern,
            own.ConvertHeic ?? settings.PhotoBackupConvertHeic,
            own.IncludeHidden ?? false,
            own.Range ?? PhotoBackupRange.Everything,
            own.SaveEdited ?? false);
        var customized = own.Pattern is not null || own.ConvertHeic is not null || own.IncludeHidden is not null || own.Range is not null || own.SaveEdited is not null
            || (own.Destination is not null && !string.Equals(own.Destination, Path.Combine(settings.PhotoBackupFolder, PhotoBackupNaming.SafeName(device.Summary.Name)), StringComparison.OrdinalIgnoreCase));
        return (options, customized);
    }

    /// <summary>The count, and the bytes a backup would write: known sizes, an estimate for the rest, HEIC bigger as JPG.</summary>
    private async Task<(PhotoBackupCount Count, long CopyBytes)> CountAsync(IDevice device, PhotoBackupOptions options, CancellationToken ct)
    {
        var assets = await PhotoBackupSources.For(device.Photos).GetBackupAssetsAsync(ct).ConfigureAwait(false);
        var known = Ledger.LoadKnown(device.Summary.Id);
        var cutoff = options.Range.Cutoff(_time.GetLocalNow(), _time.LocalTimeZone);
        int photos = 0, videos = 0, cloud = 0, hidden = 0, outOfRange = 0, edits = 0;
        long bytes = 0;
        var copy = new CopyEstimate(options.ConvertHeic && _heic != HeicConversion.MissingCodec);
        foreach (var asset in assets)
        {
            if (options.SaveEdited && PhotoBackupEdits.Wanted(asset, known) && PhotoBackupRange.Includes(cutoff, asset.TakenAt)
                && !asset.InCloudOnly && (options.IncludeHidden || !asset.Hidden))
            {
                edits++;
                copy.AddEdit();
            }

            if (known.Contains(asset))
            {
                continue;
            }

            if (!PhotoBackupRange.Includes(cutoff, asset.TakenAt))
            {
                outOfRange++;
            }
            else if (asset.Hidden && !options.IncludeHidden)
            {
                hidden++;
            }
            else if (asset.InCloudOnly)
            {
                cloud++;
            }
            else
            {
                if (asset.Kind == PhotoKind.Video)
                {
                    videos++;
                }
                else
                {
                    photos++;
                }

                bytes += asset.Size + (asset.Motion?.Size ?? 0);
                copy.Add(asset);
            }
        }

        return (new PhotoBackupCount(device.Summary.Id, photos + videos, photos, videos, bytes, cloud, hidden, outOfRange, edits), copy.Total);
    }

    /// <summary>What new photos take on the PC, with averages standing in for sizes the phone hasn't said yet.</summary>
    private sealed class CopyEstimate(bool toJpeg)
    {
        private const long UsualPhoto = 4_000_000;
        private const long UsualVideo = 60_000_000;
        private const long UsualMotion = 3_000_000;

        private long _known;
        private long _photoBytes, _videoBytes;
        private int _photos, _videos, _unknownPhotos, _unknownVideos, _unknownMotions;

        /// <summary>An edited version: its size isn't known before the backup looks for it; a photo's average stands in.</summary>
        public void AddEdit() => _unknownPhotos++;

        public void Add(PhotoBackupAsset asset)
        {
            var video = asset.Kind == PhotoKind.Video;
            if (asset.Size > 0)
            {
                _known += toJpeg && !video && ImageFormats.IsHeic(asset.FileName) ? (long)(asset.Size * PhotoBackupSpaces.JpegGrowth) : asset.Size;
                if (video)
                {
                    (_videos, _videoBytes) = (_videos + 1, _videoBytes + asset.Size);
                }
                else
                {
                    (_photos, _photoBytes) = (_photos + 1, _photoBytes + asset.Size);
                }
            }
            else if (video)
            {
                _unknownVideos++;
            }
            else
            {
                _unknownPhotos++;
            }

            if (asset.Motion is { } motion)
            {
                if (motion.Size > 0)
                {
                    _known += motion.Size;
                }
                else
                {
                    _unknownMotions++;
                }
            }
        }

        public long Total =>
            _known
            + _unknownPhotos * (long)((_photos > 0 ? _photoBytes / _photos : UsualPhoto) * (toJpeg ? PhotoBackupSpaces.JpegGrowth : 1))
            + _unknownVideos * (_videos > 0 ? _videoBytes / _videos : UsualVideo)
            + _unknownMotions * UsualMotion;
    }

    private IDevice Require(string deviceId)
    {
        var device = _hub.GetUsableDevice(deviceId);
        if ((device.Summary.Capabilities & DeviceCapability.PhotosRead) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't show its photos.");
        }

        return device;
    }

    private TransferJob? ActiveJob(string deviceId) =>
        _queue?.List().LastOrDefault(j => j.DeviceId == deviceId && j.Kind == TransferKind.PhotosBackup
            && j.State is not (TransferState.Done or TransferState.Failed or TransferState.Cancelled));

    private void OnJobUpdated(object? sender, TransferJob job)
    {
        if (job.Kind != TransferKind.PhotosBackup)
        {
            return;
        }

        var state = job.State switch
        {
            TransferState.Done => PhotoBackupRunState.Done,
            TransferState.Failed => PhotoBackupRunState.Failed,
            TransferState.Cancelled => PhotoBackupRunState.Cancelled,
            _ => PhotoBackupRunState.Running,
        };
        PhotoBackupRun? finished = null;
        lock (_gate)
        {
            var before = Ledger.GetRun(job.Id);
            if (before is null || (before.State == state && before.Failed == job.FailedItems))
            {
                return; // not started through this service, or nothing new
            }

            Ledger.UpdateRun(job.Id, state, job.FailedItems, state == PhotoBackupRunState.Running ? null : job.FinishedAt ?? _time.GetLocalNow());
            if (state != PhotoBackupRunState.Running && before.State == PhotoBackupRunState.Running)
            {
                Handler.Release(job.Id);
                finished = Ledger.GetRun(job.Id);
            }
        }

        if (finished is not null)
        {
            RunFinished?.Invoke(this, new PhotoBackupFinished(job.DeviceId, finished, job));
            var folder = finished.Destination;
            CleanupTask = Task.Run(() => CleanUpPartials(folder));
        }
    }

    public void Dispose()
    {
        if (_queue is { } queue)
        {
            queue.JobUpdated -= OnJobUpdated;
        }

        Ledger.Dispose();
    }
}
