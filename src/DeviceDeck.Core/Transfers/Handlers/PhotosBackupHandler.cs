using System.Collections.Concurrent;
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
/// </summary>
public sealed class PhotosBackupHandler(PhotoBackupLedger ledger, IImageTranscoder? transcoder = null, TimeProvider? time = null)
    : TransferHandler<PhotoBackupRequest>
{
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, PhotoDestinationIndex> _indexes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public override TransferKind Kind => TransferKind.PhotosBackup;

    /// <summary>Raised when a HEIC could not be converted (Windows lacks the HEIF/HEVC extensions); it was kept as HEIC.</summary>
    public event EventHandler? HeicConversionFailed;

    /// <summary>Drops the folder index a finished job kept in memory.</summary>
    public void Release(string jobId) => _indexes.TryRemove(jobId, out _);

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
        ledger.StartRun(context.JobId, deviceId, request.Destination, _time.GetLocalNow());
        var source = PhotoBackupSources.For(context.Device.Photos);
        var all = await source.GetBackupAssetsAsync(ct).ConfigureAwait(false);
        var known = ledger.LoadKnown(deviceId);
        var wanted = all.Where(a => !a.InCloudOnly && (request.IncludeHidden || !a.Hidden) && !known.Contains(a)).ToList();
        var complete = wanted.Count == 0 ? wanted : await source.CompleteAsync(wanted, ct).ConfigureAwait(false);

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
        var deviceId = context.Device.Summary.Id;
        var info = JsonSerializer.Deserialize<ItemSource>(item.Source, DeviceDeckJson.Options)
            ?? throw new ArgumentException("The backup item is malformed.");
        var asset = info.ToAsset();
        if (ledger.IsKnown(deviceId, asset))
        {
            // Another run (or another way of reading the phone) got to it first.
            ledger.AddToRun(context.JobId, 0, 1, 0, 0);
            return TransferItemState.Skipped;
        }

        var index = GetIndex(context.JobId, request.Destination);
        var photos = context.Device.Photos;
        var planned = item.Destination;
        var folder = Path.GetDirectoryName(planned)!;
        Directory.CreateDirectory(folder);
        var temps = new List<string>();
        try
        {
            var original = await DownloadAsync(photos, info.AssetId, info.FileName, planned + ".original" + PartialSuffix, progress, 0, temps, ct).ConfigureAwait(false);
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

            var still = await index.FindAsync(original.Size, original.Hash, ct).ConfigureAwait(false);
            byte[]? jpeg = null;
            var keptHeic = false;
            if (still is null && request.ConvertHeic && ImageFormats.IsHeic(info.FileName))
            {
                jpeg = await ConvertAsync(original.Path, ct).ConfigureAwait(false);
                if (jpeg is null)
                {
                    keptHeic = true;
                    HeicConversionFailed?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    still = await index.FindAsync(jpeg.Length, SHA256.HashData(jpeg), ct).ConfigureAwait(false);
                }
            }

            if (still is not null)
            {
                // The photo is already in the folder. Its video belongs next to it under the same name.
                if (motion is null)
                {
                    Remember(context, asset, still, null, duplicate: true);
                    return TransferItemState.Skipped;
                }

                var paired = PhotoBackupNaming.MotionPath(still, motion.FileName);
                if (File.Exists(paired) && await index.SameAsync(paired, motion.Size, motion.Hash, ct).ConfigureAwait(false))
                {
                    Remember(context, asset, still, paired, duplicate: true);
                    return TransferItemState.Skipped;
                }

                if (!File.Exists(paired) && !Directory.Exists(paired))
                {
                    Place(motion, paired, info.TakenAt, index);
                    Remember(context, asset, still, paired, duplicate: false, bytes: motion.Size);
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
                await WritePcFileAsync(stillPath, jpeg.Length, async target => await target.WriteAsync(jpeg, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                SetTimes(stillPath, info.TakenAt);
                index.Add(stillPath, jpeg.Length, SHA256.HashData(jpeg));
                written = jpeg.Length;
            }
            else
            {
                Place(original, stillPath, info.TakenAt, index);
                written = original.Size;
            }

            if (motion is not null && motionPath is not null)
            {
                Place(motion, motionPath, info.TakenAt, index);
                written += motion.Size;
            }

            Remember(context, asset, stillPath, motion is null ? null : motionPath, duplicate: false, bytes: written, keptHeic: keptHeic);
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

    private PhotoDestinationIndex GetIndex(string jobId, string root)
    {
        if (_indexes.TryGetValue(jobId, out var index) && index.Root == root && DateTime.UtcNow - index.BuiltUtc < IndexLifetime)
        {
            return index;
        }

        index = PhotoDestinationIndex.Build(root, ledger);
        _indexes[jobId] = index;
        return index;
    }

    private sealed record Downloaded(string Path, string FileName, long Size, byte[] Hash);

    private static async Task<Downloaded> DownloadAsync(IPhotoLibrary photos, string assetId, string fileName, string temp, IProgress<long> progress, long offset,
        List<string> temps, CancellationToken ct)
    {
        temps.Add(temp);
        await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        await using var hashing = new HashingWriteStream(file);
        await photos.ReadOriginalAsync(assetId, hashing, new InlineProgress<long>(bytes => progress.Report(offset + bytes)), ct).ConfigureAwait(false);
        await hashing.FlushAsync(ct).ConfigureAwait(false);

        // No check against the size the library listed: MediaStore's _size and Photos.sqlite's original size can be
        // stale, and both transports end a short read with an error. What was written is what gets hashed and kept.

        return new Downloaded(temp, fileName, hashing.Written, hashing.Finish());
    }

    /// <summary>HEIC → JPEG with the photo's EXIF carried over; null when Windows can't decode it.</summary>
    private async Task<byte[]?> ConvertAsync(string heicPath, CancellationToken ct)
    {
        if (transcoder is null)
        {
            return null;
        }

        // EXIF first: the Windows Runtime stream wrapper closes the stream it was given.
        byte[]? exif;
        await using (var file = new FileStream(heicPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        {
            exif = PhotosHeicMetadata.ReadExif(file);
        }

        await using var heic = new FileStream(heicPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        var jpeg = await transcoder.ToJpegAsync(heic, null, ct).ConfigureAwait(false);
        return jpeg is null ? null : exif is null ? jpeg : PhotosHeicMetadata.WithExif(jpeg, exif);
    }

    private static void Place(Downloaded file, string destination, DateTimeOffset? takenAt, PhotoDestinationIndex index)
    {
        File.Move(file.Path, destination);
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

    private void Remember(TransferWorkContext context, PhotoBackupAsset asset, string pcPath, string? motionPcPath, bool duplicate, long bytes = 0, bool keptHeic = false)
    {
        ledger.Record(context.Device.Summary.Id, new PhotoBackupRecord(
            asset.Key, asset.SourcePath, asset.FileName, asset.Size, asset.TakenAt, pcPath, motionPcPath, duplicate, context.JobId, _time.GetLocalNow()));
        ledger.AddToRun(context.JobId, duplicate ? 0 : 1, duplicate ? 1 : 0, bytes, keptHeic ? 1 : 0);
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
