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

        if (request.HiddenFolder is not null && request.HiddenRoot() is not null)
        {
            PhotoBackupHiddenRules.ValidateFolder(request.HiddenFolder);
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
        var hidden = request.HiddenMode();
        bool Eligible(PhotoBackupAsset a) => !a.InCloudOnly && PhotoBackupHiddenRules.Wants(hidden, a.Hidden) && PhotoBackupRange.Includes(cutoff, a.TakenAt);
        var wanted = all.Where(a => Eligible(a) && !known.Contains(a)).Select(a => request.SaveEdited ? a : a with { Edited = null }).ToList();

        // Photos copied before whose edit the PC doesn't have yet (edited since, or never looked for).
        var editsOnly = request.SaveEdited
            ? all.Where(a => Eligible(a) && known.Contains(a) && PhotoBackupEdits.Wanted(a, known)).ToList()
            : [];
        var asked = wanted.Concat(editsOnly).ToList();
        IReadOnlyList<PhotoBackupAsset> complete = asked.Count == 0 ? asked : await source.CompleteAsync(asked, ct).ConfigureAwait(false);

        // A photo the list had no date for got one from the phone just now: the range applies to it as well.
        complete = complete.Where(a => PhotoBackupRange.Includes(cutoff, a.TakenAt)).ToList();
        var editKeys = editsOnly.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var photos = complete.Where(a => !editKeys.Contains(a.Key)).ToList();
        var convert = request.ConvertHeic && transcoder is not null;

        // A hidden photo goes under the hidden folder (dated inside the same way) when the options keep them apart; its
        // Live Photo video and its edited version follow it there. An edit of a photo copied before goes next to that copy.
        var paths = PhotoBackupNaming.Plan(photos, request.Destination, request.FolderPattern, convert, request.HiddenRoot());
        var plans = new List<TransferItemPlan>(complete.Count);
        for (var i = 0; i < photos.Count; i++)
        {
            var asset = photos[i];
            var itemSource = JsonSerializer.Serialize(ItemSource.From(asset), DeviceDeckJson.Options);
            plans.Add(new TransferItemPlan(itemSource, paths[i].Still, asset.FileName, asset.Size + (asset.Motion?.Size ?? 0) + (asset.Edited?.Size ?? 0)));
        }

        foreach (var asset in complete.Where(a => editKeys.Contains(a.Key)))
        {
            known.TryGetCopy(asset.Key, out var copy);
            var edited = PhotoBackupEdits.EditedPath(copy.PcPath, asset.Edited?.FileName ?? Path.GetExtension(copy.PcPath), convert);
            var itemSource = JsonSerializer.Serialize(ItemSource.From(asset) with { EditOnly = true }, DeviceDeckJson.Options);
            plans.Add(new TransferItemPlan(itemSource, edited, Path.GetFileName(edited), asset.Edited?.Size ?? 0));
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
        if (info.EditOnly)
        {
            return await RunEditAsync(context, request, item, info, run, progress, ct).ConfigureAwait(false);
        }

        if (run.Known.Contains(asset))
        {
            // Copied before the app stopped: its ledger entry was saved, the item's state not yet.
            return TransferItemState.Skipped;
        }

        var photos = context.Device.Photos;
        var planned = item.Destination;
        var folder = Path.GetDirectoryName(planned)!;
        var convert = request.ConvertHeic && transcoder is not null && ImageFormats.IsHeic(info.FileName);
        if (await AlreadyThereAsync(context, run, info, asset, convert, ct).ConfigureAwait(false))
        {
            item.Size = info.Size + info.MotionSize;
            return TransferItemState.Skipped;
        }

        // Made only for a photo that is downloaded: one found elsewhere in the destination (the other tree, say) leaves no empty folder.
        run.EnsureFolder(folder);

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

            var edit = info.EditId is null ? null
                : await DownloadEditAsync(photos, info, planned, progress, original.Size + (motion?.Size ?? 0), temps, request.ConvertHeic, ct).ConfigureAwait(false);
            item.Size = original.Size + (motion?.Size ?? 0) + (edit?.File.Size ?? 0);
            asset = asset with { Size = original.Size };

            // Already in the folder as it came off the phone?
            string? still;
            await run.Placing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                still = await run.Index.FindAsync(original.Size, original.Hash, ct).ConfigureAwait(false);
                if (still is not null || !convert)
                {
                    return await PlaceAsync(run, info, asset, folder, still, original, null, motion, edit, keptHeic: false, ct).ConfigureAwait(false);
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
                return await PlaceAsync(run, info, asset, folder, still, original, jpeg, motion, edit, keptHeic: jpeg is null, ct).ConfigureAwait(false);
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
        Downloaded original, Converted? jpeg, Downloaded? motion, EditFile? edit, bool keptHeic, CancellationToken ct)
    {
        if (still is not null)
        {
            // The photo is already in the folder. Its video (and its edit) belong next to it under the same name.
            if (motion is null)
            {
                var editPath = await PlaceEditAsync(run, info, still, edit, null, ct).ConfigureAwait(false);
                Remember(run, asset, still, null, duplicate: true, edit: (info, editPath, edit));
                return editPath is null ? TransferItemState.Skipped : TransferItemState.Done;
            }

            var paired = PhotoBackupNaming.MotionPath(still, motion.FileName);
            if (File.Exists(paired) && await run.Index.SameAsync(paired, motion.Size, motion.Hash, ct).ConfigureAwait(false))
            {
                var editPath = await PlaceEditAsync(run, info, still, edit, null, ct).ConfigureAwait(false);
                Remember(run, asset, still, paired, duplicate: true, edit: (info, editPath, edit));
                return editPath is null ? TransferItemState.Skipped : TransferItemState.Done;
            }

            if (!File.Exists(paired) && !Directory.Exists(paired))
            {
                await PutAsync(motion, paired, info.TakenAt, run.Index, ct).ConfigureAwait(false);
                var editPath = await PlaceEditAsync(run, info, still, edit, null, ct).ConfigureAwait(false);
                Remember(run, asset, still, paired, duplicate: false, bytes: motion.Size, edit: (info, editPath, edit));
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
            await WriteFileAsync(stillPath, jpeg.Bytes, overwrite: false, ct).ConfigureAwait(false);
            SetTimes(stillPath, info.TakenAt);
            run.Index.Add(stillPath, jpeg.Length, jpeg.Hash, jpeg.Origin);
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

        var placedEdit = await PlaceEditAsync(run, info, stillPath, edit, null, ct).ConfigureAwait(false);
        Remember(run, asset, stillPath, motion is null ? null : motionPath, duplicate: false, bytes: written, keptHeic: keptHeic, edit: (info, placedEdit, edit));
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

    /// <param name="edit">The photo's edit (when it has one and the options save it): where it went, or null when the phone had none to read.</param>
    private void Remember(RunState run, PhotoBackupAsset asset, string pcPath, string? motionPcPath, bool duplicate, long bytes = 0, bool keptHeic = false,
        (ItemSource Info, string? Path, EditFile? File)? edit = null)
    {
        var stamp = edit?.Info.EditStamp;
        var record = new PhotoBackupRecord(asset.Key, asset.SourcePath, asset.FileName, asset.Size, asset.TakenAt, pcPath, motionPcPath, duplicate,
            run.Context.JobId, _time.GetLocalNow(), stamp, edit?.Path);
        var editBytes = edit?.Path is null ? 0 : edit.Value.File?.Length ?? 0;
        run.Write(batch =>
        {
            batch.Record(run.Context.Device.Summary.Id, record);
            batch.AddToRun(run.Context.JobId, duplicate ? 0 : 1, duplicate ? 1 : 0, bytes + editBytes, keptHeic ? 1 : 0,
                edited: edit?.Path is null ? 0 : 1, editsMissing: stamp is not null && edit?.Path is null ? 1 : 0);
        });
        Save(run, all: false);
    }

    /// <summary>
    /// An edit-only item: a photo copied before was edited (again) on the phone. Its edited version goes next to the copy,
    /// replacing the one an earlier backup put there when nobody changed that file since; else under a free name.
    /// </summary>
    private async Task<TransferItemState> RunEditAsync(TransferWorkContext context, PhotoBackupRequest request, TransferItem item, ItemSource info, RunState run,
        IProgress<long> progress, CancellationToken ct)
    {
        if (!run.Known.TryGetCopy(info.Key, out var copy) || copy.EditStamp == info.EditStamp)
        {
            return TransferItemState.Skipped; // forgotten since, or done before the app stopped
        }

        var deviceId = context.Device.Summary.Id;
        if (info.EditId is null)
        {
            // The phone says it was edited, but has no edited version where Photos keeps one: said, not guessed.
            run.Write(batch =>
            {
                batch.RecordEdit(deviceId, info.Key, info.EditStamp!, null);
                batch.AddToRun(context.JobId, 0, 0, 0, 0, editsMissing: 1);
            });
            Save(run, all: false);
            return TransferItemState.Skipped;
        }

        var temps = new List<string>();
        try
        {
            var anchor = Directory.Exists(Path.GetDirectoryName(copy.PcPath)) ? copy.PcPath : Path.Combine(Path.GetDirectoryName(item.Destination)!, Path.GetFileName(copy.PcPath));
            run.EnsureFolder(Path.GetDirectoryName(anchor)!);
            var edit = await DownloadEditAsync(context.Device.Photos, info, item.Destination, progress, 0, temps, request.ConvertHeic, ct).ConfigureAwait(false);
            if (edit is null)
            {
                run.Write(batch =>
                {
                    batch.RecordEdit(deviceId, info.Key, info.EditStamp!, null);
                    batch.AddToRun(context.JobId, 0, 0, 0, 0, editsMissing: 1);
                });
                Save(run, all: false);
                return TransferItemState.Skipped;
            }

            item.Size = edit.Length;
            string placed;
            await run.Placing.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                placed = (await PlaceEditAsync(run, info, anchor, edit, copy.EditPcPath, ct).ConfigureAwait(false))!;
            }
            finally
            {
                run.Placing.Release();
            }

            run.Write(batch =>
            {
                batch.RecordEdit(deviceId, info.Key, info.EditStamp!, placed);
                batch.AddToRun(context.JobId, 0, 0, edit.Length, 0, edited: 1);
            });
            Save(run, all: false);
            return TransferItemState.Done;
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
    /// Before anything is copied: when the folder has a file of the photo's size and name (or a JPEG this PC made from it),
    /// a sample of the phone's file — its size, first and last 64 KB — is compared with it. A match is remembered as
    /// there already, with a Live Photo's video checked the same way. So after "forget history", or under a new device id,
    /// a library already on the PC isn't downloaded again. Anything less than a match goes the usual way (copy, then
    /// SHA-256). A photo whose edited version is to be saved goes the usual way too.
    /// </summary>
    private async Task<bool> AlreadyThereAsync(TransferWorkContext context, RunState run, ItemSource info, PhotoBackupAsset asset, bool convert, CancellationToken ct)
    {
        if (info.Size <= 0 || info.EditStamp is not null || context.Device.Photos is not IPhotoRangeReader reader)
        {
            return false;
        }

        var stem = PhotoBackupSample.Stem(info.FileName);
        if (!run.Index.HasNamesake(info.Size, stem) && !(convert && ledger.FindByOrigin(info.Size).Count > 0))
        {
            return false;
        }

        var sample = await SampleAsync(reader, info.AssetId, info.Size, ct).ConfigureAwait(false);
        if (sample is null)
        {
            return false;
        }

        string? motionPath = null;
        await run.Placing.WaitAsync(ct).ConfigureAwait(false);
        string? still;
        try
        {
            still = await run.Index.FindBySampleAsync(info.Size, sample, stem, ct).ConfigureAwait(false)
                ?? (convert ? run.Index.FindConverted(info.Size, sample, stem) : null);
        }
        finally
        {
            run.Placing.Release();
        }

        if (still is null)
        {
            return false;
        }

        if (info.MotionId is { } motionId)
        {
            // The video must be there as well, next to the still under its name, with the same sample.
            motionPath = PhotoBackupNaming.MotionPath(still, info.MotionName ?? ".MOV");
            if (info.MotionSize <= 0 || !File.Exists(motionPath) || new FileInfo(motionPath).Length != info.MotionSize)
            {
                return false;
            }

            var phone = await SampleAsync(reader, motionId, info.MotionSize, ct).ConfigureAwait(false);
            var pc = await PhotoBackupSample.OfFileAsync(motionPath, info.MotionSize, ct).ConfigureAwait(false);
            if (phone is null || pc is null || !phone.AsSpan().SequenceEqual(pc))
            {
                return false;
            }
        }

        Remember(run, asset, still, motionPath, duplicate: true);
        return true;
    }

    /// <summary>The phone's sample of a file; null when the phone can't read part of one (then the photo is copied and compared whole).</summary>
    private static async Task<byte[]?> SampleAsync(IPhotoRangeReader reader, string assetId, long size, CancellationToken ct)
    {
        try
        {
            return await PhotoBackupSample.OfPhoneAsync(reader, assetId, size, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported or DeviceErrorCodes.Protocol or DeviceErrorCodes.PathNotFound)
        {
            return null;
        }
    }

    /// <summary>An edited version off the phone (converted to JPEG when asked and possible); null when the phone has it no more.</summary>
    private sealed record EditFile(Downloaded File, Converted? Jpeg)
    {
        public long Length => Jpeg?.Length ?? File.Size;
    }

    private async Task<EditFile?> DownloadEditAsync(IPhotoLibrary photos, ItemSource info, string planned, IProgress<long> progress, long offset,
        List<string> temps, bool convertHeic, CancellationToken ct)
    {
        var name = info.EditName ?? "FullSizeRender.jpg";
        var convert = convertHeic && transcoder is not null && ImageFormats.IsHeic(name);
        Downloaded file;
        try
        {
            file = await DownloadAsync(photos, info.EditId!, name, convert && info.EditSize is > 0 and <= InMemoryHeic ? null : planned + ".edited" + PartialSuffix,
                progress, offset, temps, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PathNotFound or DeviceErrorCodes.NotFound)
        {
            return null; // edited again (or reverted) since the plan
        }

        var jpeg = convert ? await ConvertAsync(file, ct).ConfigureAwait(false) : null;
        return new EditFile(file, jpeg);
    }

    /// <summary>
    /// Puts an edited version next to <paramref name="stillPath"/> as "IMG_1234 (edited).jpg". It replaces
    /// <paramref name="previous"/> (an earlier edit this backup wrote) only when that file is still exactly as written —
    /// its size and the photo's date as its write time, as the ledger saw it. Otherwise a free name. Caller holds Placing.
    /// </summary>
    private async Task<string?> PlaceEditAsync(RunState run, ItemSource info, string stillPath, EditFile? edit, string? previous, CancellationToken ct)
    {
        if (edit is null)
        {
            return null;
        }

        var wanted = PhotoBackupEdits.EditedPath(stillPath, edit.File.FileName, edit.Jpeg is not null);
        var target = previous is not null && IsUnchangedCopy(previous, info.TakenAt)
            ? previous
            : FreeName(wanted);
        if (edit.Jpeg is { } jpeg)
        {
            await WriteFileAsync(target, jpeg.Bytes, overwrite: target == previous, ct).ConfigureAwait(false);
            SetTimes(target, info.TakenAt);
            run.Index.Add(target, jpeg.Length, jpeg.Hash);
        }
        else if (edit.File.Path is { } temp)
        {
            File.Move(temp, target, overwrite: target == previous);
            SetTimes(target, info.TakenAt);
            run.Index.Add(target, edit.File.Size, edit.File.Hash);
        }
        else
        {
            await PutAsync(edit.File, target, info.TakenAt, run.Index, ct, overwrite: target == previous).ConfigureAwait(false);
        }

        return target;

        bool IsUnchangedCopy(string path, DateTimeOffset? takenAt)
        {
            var file = new FileInfo(path);
            return file.Exists && takenAt is { } when && file.LastWriteTimeUtc == when.UtcDateTime
                && ledger.GetHash(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks) is not null;
        }

        static string FreeName(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }

            var (free, _) = PhotoBackupNaming.FreeStem(Path.GetDirectoryName(path)!, Path.GetFileName(path), null, p => !File.Exists(p) && !Directory.Exists(p));
            return free;
        }
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

    /// <param name="Origin">The HEIC it was made from: its size and sample.</param>
    private sealed record Converted(byte[] Bytes, byte[] Hash, (long Size, byte[] Sample)? Origin = null)
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
        return new Converted(withExif, SHA256.HashData(withExif), (bytes.Length, PhotoBackupSample.Of(bytes)));
    }

    /// <summary>Moves a downloaded file into place (or writes it there through a .ddpart when it is in memory) with the photo's dates.</summary>
    private static async Task PutAsync(Downloaded file, string destination, DateTimeOffset? takenAt, PhotoDestinationIndex index, CancellationToken ct,
        bool overwrite = false)
    {
        if (file.Path is { } temp)
        {
            File.Move(temp, destination, overwrite);
        }
        else
        {
            await WriteFileAsync(destination, file.Bytes!, overwrite, ct).ConfigureAwait(false);
        }

        SetTimes(destination, takenAt);
        index.Add(destination, file.Size, file.Hash);
    }

    /// <summary>The photo backup's own temp files: "IMG_0001.HEIC.original.ddpart" and its kin. Nothing else is named so.</summary>
    public static readonly IReadOnlyList<string> TempSuffixes =
        [".original" + PartialSuffix, ".motion" + PartialSuffix, ".edited" + PartialSuffix, ".backup" + PartialSuffix];

    /// <summary>Bytes in memory onto the PC through "&lt;name&gt;.backup.ddpart" and a rename, so a half-written file never has the real name.</summary>
    private static async Task WriteFileAsync(string destination, byte[] bytes, bool overwrite, CancellationToken ct)
    {
        var partial = destination + ".backup" + PartialSuffix;
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            File.Move(partial, destination, overwrite);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
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
                foreach (var (path, size, modified, sha256, originSize, originSample) in index.TakeUnsavedHashes())
                {
                    batch.PutHash(path, size, modified, sha256, originSize, originSample);
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
        long MotionSize,
        string? EditStamp = null,
        string? EditId = null,
        string? EditName = null,
        long EditSize = 0,
        bool EditOnly = false)
    {
        public static ItemSource From(PhotoBackupAsset a) =>
            new(a.Key, a.AssetId, a.FileName, a.Kind, a.TakenAt, a.Size, a.Hidden, a.SourcePath, a.Motion?.AssetId, a.Motion?.FileName, a.Motion?.Size ?? 0,
                a.Edited?.Stamp, a.Edited?.AssetId, a.Edited?.FileName, a.Edited?.Size ?? 0);

        public PhotoBackupAsset ToAsset() => new(Key, AssetId, FileName, Kind, TakenAt, Size, InCloudOnly: false, Hidden, SourcePath,
            MotionId is null ? null : new PhotoBackupMotion(MotionId, MotionName ?? Path.ChangeExtension(FileName, ".MOV"), MotionSize),
            EditStamp is null ? null : new PhotoBackupEdit(EditStamp, EditId, EditName, EditSize));
    }
}
