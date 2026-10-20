using DeviceDeck.Core.Abstractions;
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
    private TransferQueue? _queue;
    private HeicConversion _heic;

    public PhotoBackupService(string databasePath, DeviceHub hub, SettingsStore settings, IImageTranscoder? transcoder,
        HeicConversion heicConversion = HeicConversion.Unknown, TimeProvider? time = null)
    {
        _hub = hub;
        _settings = settings;
        _time = time ?? TimeProvider.System;
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
    }

    /// <summary>
    /// How many photos and videos on the phone this PC hasn't backed up. Needs the phone ready (unlocked iPhone);
    /// on an iPhone the first call after plugging in reads the photo library index (a few seconds; later calls reuse it).
    /// </summary>
    public async Task<PhotoBackupCount> CountNewAsync(string deviceId, CancellationToken ct)
    {
        var device = Require(deviceId);
        return await CountAsync(device, EffectiveOptions(device).Options, ct).ConfigureAwait(false);
    }

    public async Task<PhotoBackupStatus> GetStatusAsync(string deviceId, CancellationToken ct)
    {
        var device = Require(deviceId);
        var (options, customized) = EffectiveOptions(device);
        var count = await CountAsync(device, options, ct).ConfigureAwait(false);
        return new PhotoBackupStatus(
            deviceId,
            count,
            options,
            customized,
            Ledger.CountBackedUp(deviceId),
            Ledger.GetLastRun(deviceId),
            ActiveJob(deviceId)?.Id,
            _heic);
    }

    /// <summary>
    /// Saves the phone's own options; a null value keeps what it had. <paramref name="useDefaults"/> drops them all
    /// first, so the phone follows Settings again (its destination too).
    /// </summary>
    public void Configure(string deviceId, string? destination, string? folderPattern, bool? convertHeic, bool? includeHidden, bool useDefaults = false)
    {
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

        var saved = useDefaults ? default : Ledger.GetOptions(deviceId);
        Ledger.SetOptions(
            deviceId,
            destination ?? saved.Destination,
            folderPattern ?? saved.Pattern,
            convertHeic ?? saved.ConvertHeic,
            includeHidden ?? saved.IncludeHidden);
    }

    /// <summary>
    /// Queues a backup of everything new on the phone with its current options, or returns the backup job already
    /// queued or running for it. The destination is pinned to the phone on its first backup, so renaming the
    /// phone later doesn't start a second folder.
    /// </summary>
    public async Task<TransferJob> StartAsync(string deviceId, CancellationToken ct)
    {
        var queue = _queue ?? throw new InvalidOperationException("The photo backup isn't attached to the transfer queue.");
        var device = Require(deviceId);
        if (ActiveJob(deviceId) is { } running)
        {
            return running;
        }

        var (options, _) = EffectiveOptions(device);
        if (Ledger.GetOptions(deviceId).Destination is null)
        {
            Configure(deviceId, options.Destination, null, null, null);
        }

        var request = new PhotoBackupRequest(options.Destination, options.FolderPattern, options.ConvertHeic, options.IncludeHidden);
        var job = await queue.EnqueueAsync(deviceId, TransferKind.PhotosBackup, request, ConflictRule.KeepBoth, ct).ConfigureAwait(false);
        Ledger.StartRun(job.Id, deviceId, options.Destination, job.CreatedAt);

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
            own.IncludeHidden ?? false);
        var customized = own.Pattern is not null || own.ConvertHeic is not null || own.IncludeHidden is not null
            || (own.Destination is not null && !string.Equals(own.Destination, Path.Combine(settings.PhotoBackupFolder, PhotoBackupNaming.SafeName(device.Summary.Name)), StringComparison.OrdinalIgnoreCase));
        return (options, customized);
    }

    private async Task<PhotoBackupCount> CountAsync(IDevice device, PhotoBackupOptions options, CancellationToken ct)
    {
        var assets = await PhotoBackupSources.For(device.Photos).GetBackupAssetsAsync(ct).ConfigureAwait(false);
        var known = Ledger.LoadKnown(device.Summary.Id);
        int photos = 0, videos = 0, cloud = 0, hidden = 0;
        long bytes = 0;
        foreach (var asset in assets)
        {
            if (known.Contains(asset))
            {
                continue;
            }

            if (asset.Hidden && !options.IncludeHidden)
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
            }
        }

        return new PhotoBackupCount(device.Summary.Id, photos + videos, photos, videos, bytes, cloud, hidden);
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
