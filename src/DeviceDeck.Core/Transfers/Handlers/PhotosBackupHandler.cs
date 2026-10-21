using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Serialization;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.Transfers.Handlers;

/// <summary>
/// photos.backup: the photos and videos of one phone that this PC hasn't copied before, into date folders. Each item is
/// one photo (a Live Photo's still and video together). A photo whose bytes are already somewhere in the destination
/// (same size, same SHA-256) is not written again, only remembered. Never asks about conflicts: a different file
/// under the same name makes "IMG_0001 (1)", for both halves of a Live Photo.
/// <para>
/// Speed: <see cref="ParallelDownloads"/> photos copy at once, the destination folder is read once per run, and the
/// ledger is written in batches (<see cref="IBatchingTransferHandler"/>). Choosing a name and putting files in place
/// happen one photo at a time, so two photos never take one name and a duplicate is always seen.
/// </para>
/// </summary>
public sealed class PhotosBackupHandler(PhotoBackupLedger ledger, IImageTranscoder? transcoder = null, TimeProvider? time = null)
    : TransferHandler<PhotoBackupRequest>, IBatchingTransferHandler
{
    /// <summary>Photos copied at once: enough to keep the USB link busy between files, few enough not to fight over it.</summary>
    public const int ParallelDownloads = 3;

    /// <summary>A HEIC up to this size is kept in memory to be converted (no temp file written and read back).</summary>
    private const long InMemoryHeic = 64L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public override TransferKind Kind => TransferKind.PhotosBackup;

    public int ParallelItems => ParallelDownloads;

    /// <summary>Raised when a HEIC could not be converted (Windows lacks the HEIF/HEVC extensions); it was kept as HEIC.</summary>
    public event EventHandler? HeicConversionFailed;

    /// <summary>Drops whatever a finished job still keeps in memory.</summary>
    public void Release(string jobId)
    {
        if (_runs.TryRemove(jobId, out var run))
        {
            Save(run, all: true);
        }
    }

    public Task EndRunAsync(TransferWorkContext context)
    {
        if (_runs.TryGetValue(context.JobId, out var run) && run.Context == context && _runs.TryRemove(new(context.JobId, run)))
        {
            Save(run, all: true);
        }

        return Task.CompletedTask;
    }

    protected override Task<string> DescribeAsync(IDevice device, PhotoBackupRequest request, CancellationToken ct)
    {
        RequirePcFolder(request.Destination, "The backup folder");
        if (!PhotoBackupNaming.IsValidPattern(request.FolderPattern))
        {
            throw new ArgumentException("folderPattern must be \"\" or yyyy, MM and dd joined by / - _ . or a space.");
        }

        if ((device.Summary.Capabilities & DeviceCapability.PhotosRead) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't show its photos.");
        }

        return Task.FromResult($"Backing up new photos from {device.Summary.Name} to {request.Destination}");
    }

    protected override async Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, PhotoBackupRequest request, CancellationToken ct)
    {
        var deviceId = context.Device.Summary.Id;
        ledger.StartRun(context.JobId, deviceId, request.Destination, _time.GetLocalNow(), request.Trigger);
        var source = PhotoBackupSources.For(context.Device.Photos);
        var all = await source.GetBackupAssetsAsync(ct).ConfigureAwait(false);
        var known = ledger.LoadKnown(deviceId);
        var cutoff = (request.Range ?? PhotoBackupRange.Everything).Cutoff(_time.GetLocalNow(), _time.LocalTimeZone);
        var wanted = all.Where(a => !a.InCloudOnly && (request.IncludeHidden || !a.Hidden) && !known.Contains(a) && PhotoBackupRange.Includes(cutoff, a.TakenAt)).ToList();
        IReadOnlyList<PhotoBackupAsset> complete = wanted.Count == 0 ? wanted : await source.CompleteAsync(wanted, ct).ConfigureAwait(false);

        // A photo the list had no date for got one from the phone just now: the range applies to it as well.
        complete = complete.Where(a => PhotoBackupRange.Includes(cutoff, a.TakenAt)).ToList();

        var paths = PhotoBackupNaming.Plan(complete, request.Destination, request.FolderPattern, request.ConvertHeic && transcoder is not null);
        var plans = new List<TransferItemPlan>(complete.Count);
        for (var i = 0; i < complete.Count; i++)
        {
            var asset = complete[i];
            var itemSource = JsonSerializer.Serialize(ItemSource.From(asset), DeviceDeckJson.Options);
            plans.Add(new TransferItemPlan(itemSource, paths[i].Still, asset.FileName, asset.Size + (asset.Motion?.Size ?? 0)));
        }

        ledger.SetPlanned(context.JobId, plans.Count);
        return plans;
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, PhotoBackupRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var run = RunFor(context, request);
        var info = JsonSerializer.Deserialize<ItemSource>(item.Source, DeviceDeckJson.Options)
            ?? throw new ArgumentException("The backup item is malformed.");
        var asset = info.ToAsset();
        if (run.Known.Contains(asset))
        {
            // Copied before the app stopped: its ledger entry was saved, the item's state not yet.
            return TransferItemState.Skipped;
        }

        var photos = context.Device.Photos;
        var planned = item.Destination;
        var folder = Path.GetDirectoryName(planned)!;
        run.EnsureFolder(folder);
        var convert = request.ConvertHeic && transcoder is not null && ImageFormats.IsHeic(info.FileName);
        var temps = new List<string>();
        try
        {
            var original = await DownloadAsync(photos, info.AssetId, info.FileName,
                convert && info.Size is > 0 and <= InMemoryHeic ? null : planned + ".original" + PartialSuffix, progress, 0, temps, ct).ConfigureAwait(false);
            Downloaded? motion = null;
            if (info.MotionId is { } motionId)
            {
                try
                {
                    var motionTemp = PhotoBackupNaming.MotionPath(planned, info.MotionName ?? ".MOV") + ".motion" + PartialSuffix;
                    motion = await DownloadAsync(photos, motionId, info.MotionName ?? ".MOV", motionTemp, progress, original.Size, temps, ct).ConfigureAwait(false);
                }
                catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.PathNotFound)
                {
                    // The phone lists a Live Photo whose video is gone: the still is backed up on its own.
                }
            }

            item.Size = original.Size + (motion?.Size ?? 0);
            asset = asset with { Size = original.Size };

            // Already in the folder as it came off the phone?
            string? still;
            await run.Placing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                still = await run.Index.FindAsync(original.Size, original.Hash, ct).ConfigureAwait(false);
                if (still is not null || !convert)
                {
                    return await PlaceAsync(run, info, asset, folder, still, original, null, motion, keptHeic: false, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                run.Placing.Release();
            }

            // Converted outside the lock: other photos keep moving while this one is encoded.
            var jpeg = await ConvertAsync(original, ct).ConfigureAwait(false);
            if (jpeg is null)
            {
                HeicConversionFailed?.Invoke(this, EventArgs.Empty);
            }

            await run.Placing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                still = jpeg is null
                    ? await run.Index.FindAsync(original.Size, original.Hash, ct).ConfigureAwait(false)
                    : await run.Index.FindAsync(jpeg.Length, jpeg.Hash, ct).ConfigureAwait(false);
                return await PlaceAsync(run, info, asset, folder, still, original, jpeg, motion, keptHeic: jpeg is null, ct).ConfigureAwait(false);
            }
            finally
            {
                run.Placing.Release();
            }
        }
        finally
        {
            foreach (var temp in temps)
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>
    /// Puts one photo in place, or only remembers it when it is there already (<paramref name="still"/>). Runs under
    /// <see cref="RunState.Placing"/>: the disk decides the free name, and the index sees every file as soon as it lands.
    /// </summary>
    private async Task<TransferItemState> PlaceAsync(RunState run, ItemSource info, PhotoBackupAsset asset, string folder, string? still,
        Downloaded original, Converted? jpeg, Downloaded? motion, bool keptHeic, CancellationToken ct)
    {
        if (still is not null)
        {
            // The photo is already in the folder. Its video belongs next to it under the same name.
            if (motion is null)
            {
                Remember(run, asset, still, null, duplicate: true);
                return TransferItemState.Skipped;
            }

            var paired = PhotoBackupNaming.MotionPath(still, motion.FileName);
            if (File.Exists(paired) && await run.Index.SameAsync(paired, motion.Size, motion.Hash, ct).ConfigureAwait(false))
            {
                Remember(run, asset, still, paired, duplicate: true);
                return TransferItemState.Skipped;
            }

            if (!File.Exists(paired) && !Directory.Exists(paired))
            {
                await PutAsync(motion, paired, info.TakenAt, run.Index, ct).ConfigureAwait(false);
                Remember(run, asset, still, paired, duplicate: false, bytes: motion.Size);
                return TransferItemState.Done;
            }

            // Something else has the video's name there: write both again, together, under a free name.
        }

        // The disk decides the name now ("IMG_0001", then "IMG_0001 (1)", …), not the plan's guess.
        var stillName = PhotoBackupNaming.StillName(info.FileName, convertHeic: jpeg is not null);
        var (stillPath, motionPath) = PhotoBackupNaming.FreeStem(folder, stillName, motion?.FileName, p => !File.Exists(p) && !Directory.Exists(p));
        long written;
        if (jpeg is not null)
        {
            await WritePcFileAsync(stillPath, jpeg.Length, async target => await target.WriteAsync(jpeg.Bytes, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            SetTimes(stillPath, info.TakenAt);
            run.Index.Add(stillPath, jpeg.Length, jpeg.Hash);
            written = jpeg.Length;
        }
        else
        {
            await PutAsync(original, stillPath, info.TakenAt, run.Index, ct).ConfigureAwait(false);
            written = original.Size;
        }

        if (motion is not null && motionPath is not null)
        {
            await PutAsync(motion, motionPath, info.TakenAt, run.Index, ct).ConfigureAwait(false);
            written += motion.Size;
        }

        Remember(run, asset, stillPath, motion is null ? null : motionPath, duplicate: false, bytes: written, keptHeic: keptHeic);
        return TransferItemState.Done;
    }

    /// <summary>The run's state for this context: made by the first item of each run (a resume reads the folder again).</summary>
    private RunState RunFor(TransferWorkContext context, PhotoBackupRequest request)
    {
        if (_runs.TryGetValue(context.JobId, out var run) && run.Context == context)
        {
            return run;
        }

        // The run's first items arrive together; one of them reads the folder and the ledger, the others wait for it.
        lock (_runs)
        {
            if (_runs.TryGetValue(context.JobId, out run))
            {
                if (run.Context == context)
                {
                    return run;
                }

                Save(run, all: true); // left over from an earlier run of the job
            }

            run = new RunState(context, PhotoDestinationIndex.Build(request.Destination, ledger), ledger.LoadKnown(context.Device.Summary.Id));
            _runs[context.JobId] = run;
            return run;
        }
    }

    private void Remember(RunState run, PhotoBackupAsset asset, string pcPath, string? motionPcPath, bool duplicate, long bytes = 0, bool keptHeic = false)
    {
        var record = new PhotoBackupRecord(asset.Key, asset.SourcePath, asset.FileName, asset.Size, asset.TakenAt, pcPath, motionPcPath, duplicate,
            run.Context.JobId, _time.GetLocalNow());
        run.Write(batch =>
        {
            batch.Record(run.Context.Device.Summary.Id, record);
            batch.AddToRun(run.Context.JobId, duplicate ? 0 : 1, duplicate ? 1 : 0, bytes, keptHeic ? 1 : 0);
        });
        Save(run, all: false);
    }

    /// <summary>Saves the run's ledger writes: when enough have gathered (or a second has passed), or all of them.</summary>
    private void Save(RunState run, bool all)
    {
        if (run.Take(all, run.Index) is { } batch)
        {
            ledger.Save(batch);
        }
    }

    /// <summary>One photo file off the phone, hashed on the way: into a temp file, or into memory when <paramref name="temp"/> is null.</summary>
    private sealed record Downloaded(string? Path, byte[]? Bytes, string FileName, long Size, byte[] Hash);

    private sealed record Converted(byte[] Bytes, byte[] Hash)
    {
        public long Length => Bytes.Length;
    }

    private static async Task<Downloaded> DownloadAsync(IPhotoLibrary photos, string assetId, string fileName, string? temp, IProgress<long> progress, long offset,
        List<string> temps, CancellationToken ct)
    {
        var report = new InlineProgress<long>(bytes => progress.Report(offset + bytes));
        if (temp is null)
        {
            using var memory = new MemoryStream();
            await using var hashing = new HashingWriteStream(memory);
            await photos.ReadOriginalAsync(assetId, hashing, report, ct).ConfigureAwait(false);
            return new Downloaded(null, memory.ToArray(), fileName, hashing.Written, hashing.Finish());
        }

        temps.Add(temp);
        await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        await using var hashingFile = new HashingWriteStream(file);
        await photos.ReadOriginalAsync(assetId, hashingFile, report, ct).ConfigureAwait(false);
        await hashingFile.FlushAsync(ct).ConfigureAwait(false);

        // No check against the size the library listed: MediaStore's _size and Photos.sqlite's original size can be
        // stale, and both transports end a short read with an error. What was written is what gets hashed and kept.

        return new Downloaded(temp, null, fileName, hashingFile.Written, hashingFile.Finish());
    }

    /// <summary>HEIC → JPEG with the photo's EXIF carried over; null when Windows can't decode it. The HEIC is read once.</summary>
    private async Task<Converted?> ConvertAsync(Downloaded heic, CancellationToken ct)
    {
        if (transcoder is null)
        {
            return null;
        }

        var bytes = heic.Bytes ?? await File.ReadAllBytesAsync(heic.Path!, ct).ConfigureAwait(false);
        var exif = PhotosHeicMetadata.ReadExif(new MemoryStream(bytes, writable: false));

        // The Windows Runtime stream wrapper closes the stream it was given: a stream of its own.
        var jpeg = await transcoder.ToJpegAsync(new MemoryStream(bytes, writable: false), null, ct).ConfigureAwait(false);
        if (jpeg is null)
        {
            return null;
        }

        var withExif = exif is null ? jpeg : PhotosHeicMetadata.WithExif(jpeg, exif);
        return new Converted(withExif, SHA256.HashData(withExif));
    }

    /// <summary>Moves a downloaded file into place (or writes it there through a .ddpart when it is in memory) with the photo's dates.</summary>
    private static async Task PutAsync(Downloaded file, string destination, DateTimeOffset? takenAt, PhotoDestinationIndex index, CancellationToken ct)
    {
        if (file.Path is { } temp)
        {
            File.Move(temp, destination);
        }
        else
        {
            await WritePcFileAsync(destination, file.Size, async target => await target.WriteAsync(file.Bytes, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        SetTimes(destination, takenAt);
        index.Add(destination, file.Size, file.Hash);
    }

    /// <summary>Explorer sorts by these: the file's dates become the photo's.</summary>
    private static void SetTimes(string path, DateTimeOffset? takenAt)
    {
        if (takenAt is not { } when)
        {
            return;
        }

        try
        {
            File.SetCreationTimeUtc(path, when.UtcDateTime);
            File.SetLastWriteTimeUtc(path, when.UtcDateTime);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
        }
    }

    /// <summary>What one run of a job keeps: the destination's index, what the ledger knew when it started, and ledger writes not saved yet.</summary>
    private sealed class RunState(TransferWorkContext context, PhotoDestinationIndex index, PhotoBackupKnownSet known)
    {
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);
        private const int SaveEvery = 32;

        private readonly Lock _gate = new();
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private PhotoBackupLedgerBatch _batch = new();
        private long _lastSave = Stopwatch.GetTimestamp();

        public TransferWorkContext Context { get; } = context;

        public PhotoDestinationIndex Index { get; } = index;

        /// <summary>Read once when the run starts; only this run writes the phone's ledger while it lasts.</summary>
        public PhotoBackupKnownSet Known { get; } = known;

        /// <summary>Held while a photo's name is chosen and its files are put in place.</summary>
        public SemaphoreSlim Placing { get; } = new(1, 1);

        public void EnsureFolder(string folder)
        {
            lock (_gate)
            {
                if (!_folders.Add(folder))
                {
                    return;
                }
            }

            Directory.CreateDirectory(folder);
        }

        public void Write(Action<PhotoBackupLedgerBatch> write)
        {
            lock (_gate)
            {
                write(_batch);
            }
        }

        /// <summary>The writes to save now (with the index's new sums), or null when it isn't time yet.</summary>
        public PhotoBackupLedgerBatch? Take(bool all, PhotoDestinationIndex index)
        {
            lock (_gate)
            {
                if (!all && _batch.Count < SaveEvery && Stopwatch.GetElapsedTime(_lastSave) < SaveInterval)
                {
                    return null;
                }

                var batch = _batch;
                _batch = new PhotoBackupLedgerBatch();
                _lastSave = Stopwatch.GetTimestamp();
                foreach (var (path, size, modified, sha256) in index.TakeUnsavedHashes())
                {
                    batch.PutHash(path, size, modified, sha256);
                }

                return batch;
            }
        }
    }

    /// <summary>What one item needs at run time, kept in the item's Source so a job resumes after a restart without re-reading the library.</summary>
    internal sealed record ItemSource(
        string Key,
        string AssetId,
        string FileName,
        PhotoKind Kind,
        DateTimeOffset? TakenAt,
        long Size,
        bool Hidden,
        string? SourcePath,
        string? MotionId,
        string? MotionName,
        long MotionSize)
    {
        public static ItemSource From(PhotoBackupAsset a) =>
            new(a.Key, a.AssetId, a.FileName, a.Kind, a.TakenAt, a.Size, a.Hidden, a.SourcePath, a.Motion?.AssetId, a.Motion?.FileName, a.Motion?.Size ?? 0);

        public PhotoBackupAsset ToAsset() => new(Key, AssetId, FileName, Kind, TakenAt, Size, InCloudOnly: false, Hidden, SourcePath,
            MotionId is null ? null : new PhotoBackupMotion(MotionId, MotionName ?? Path.ChangeExtension(FileName, ".MOV"), MotionSize));
    }
}
