using System.Globalization;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.PhotoDelete;

/// <summary>
/// Deleting confirmed photos item by item on any device: through <see cref="IPhotoDeleter"/> when the device has it
/// (iPhone), else through <see cref="IPhotoLibrary.DeleteAsync"/> in small batches, each checked against the library.
/// </summary>
public static class PhotoDeletes
{
    /// <summary>Items per <see cref="IPhotoLibrary.DeleteAsync"/> call on a device without <see cref="IPhotoDeleter"/>.</summary>
    public const int LibraryBatchSize = 25;

    /// <summary>Whether the device's photos can be deleted from the PC now, and if not, why.</summary>
    public static async Task<PhotoDeleteSupport> SupportOf(IDevice device, bool recheck, CancellationToken ct)
    {
        if (device is IPhotoDeleter deleter)
        {
            return await deleter.GetDeleteSupportAsync(recheck, ct).ConfigureAwait(false);
        }

        // Android's MediaStore deletes the file with its row: there is no Recently Deleted for a delete from the PC.
        return device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete)
            ? new PhotoDeleteSupport(true, PhotoDeleteBlock.None, null, PhotoDeleteDestination.Permanent, false, DateTimeOffset.Now)
            : PhotoDeleteSupport.No(PhotoDeleteBlock.Unsupported, $"{device.Summary.Name} can't delete photos from the PC.");
    }

    /// <summary>
    /// Deletes exactly these items. Every item is checked against the library first and nothing is deleted unless all of
    /// them match; then they go in small batches, stopping at the first one that fails. One result per item, in order.
    /// </summary>
    public static async Task<IReadOnlyList<PhotoDeleteResult>> DeleteItemsAsync(IDevice device, IReadOnlyList<PhotoDeleteItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        if (device is IPhotoDeleter deleter)
        {
            return await deleter.DeleteConfirmedAsync(items, ct).ConfigureAwait(false);
        }

        var results = new PhotoDeleteResult?[items.Count];
        var library = await ReadLibraryAsync(device, ct).ConfigureAwait(false);
        for (var i = 0; i < items.Count; i++)
        {
            if (!library.TryGetValue(items[i].Id, out var asset))
            {
                results[i] = Result(items[i], null, PhotoDeleteOutcome.Mismatch, DeviceErrorCodes.PathNotFound, "It's no longer on the phone.");
            }
            else if (asset.Size != items[i].Size)
            {
                results[i] = Result(items[i], asset, PhotoDeleteOutcome.Mismatch, DeviceErrorCodes.PathNotFound, "It changed since it was selected.");
            }
        }

        if (results.Any(r => r is not null))
        {
            return Fill(items, results, library, "Nothing was deleted, because another selected item didn't match.");
        }

        for (var start = 0; start < items.Count; start += LibraryBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = Enumerable.Range(start, Math.Min(LibraryBatchSize, items.Count - start)).ToList();
            DeviceException? error = null;
            try
            {
                await device.Photos.DeleteAsync(batch.Select(i => items[i].Id).ToList(), ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code is not (DeviceErrorCodes.Disconnected or DeviceErrorCodes.TrustRequired or DeviceErrorCodes.Unauthorized))
            {
                error = ex;
            }

            var after = await ReadLibraryAsync(device, ct).ConfigureAwait(false);
            var stop = false;
            foreach (var i in batch)
            {
                var asset = library[items[i].Id];
                if (!after.ContainsKey(items[i].Id))
                {
                    results[i] = Result(items[i], asset, PhotoDeleteOutcome.Deleted, null, null) with { WentTo = PhotoDeleteDestination.Permanent };
                }
                else
                {
                    results[i] = Result(items[i], asset, PhotoDeleteOutcome.Failed, error?.Code ?? DeviceErrorCodes.Protocol,
                        error?.Message ?? "The phone still lists it after the delete.");
                    stop = true;
                }
            }

            if (stop)
            {
                break;
            }
        }

        return Fill(items, results, library, "Not tried: the delete stopped at an earlier item.");
    }

    /// <summary>
    /// Copies the photos to a PC folder before they are deleted: one photos.export job, plus, on an iPhone, a copy of each
    /// Live Photo's video beside its still (photos.export copies stills only; the video isn't an asset of its own).
    /// Returns the job ids; the caller waits for all of them before deleting anything.
    /// </summary>
    public static async Task<IReadOnlyList<string>> EnqueueCopiesAsync(TransferQueue transfers, IDevice device, IReadOnlyList<string> assetIds,
        string destination, string folderPattern, CancellationToken ct)
    {
        var deviceId = device.Summary.Id;
        var jobs = new List<string>();
        var export = await transfers.EnqueueAsync(deviceId, TransferKind.PhotosExport,
            new PhotosExportRequest(assetIds, destination, ConvertHeic: false, folderPattern), ConflictRule.KeepBoth, ct).ConfigureAwait(false);
        jobs.Add(export.Id);

        if (device.Summary.Platform != DevicePlatform.Ios || !device.Summary.Capabilities.HasFlag(DeviceCapability.Files))
        {
            return jobs;
        }

        var library = await ReadLibraryAsync(device, ct).ConfigureAwait(false);
        var videos = assetIds
            .Select(id => library.GetValueOrDefault(id))
            .Where(a => a is { LivePairId: not null })
            .Select(a => a!)
            .GroupBy(a => folderPattern == AppSettings.PatternYearMonth && a.TakenAt is { } when
                ? Path.Combine(destination, when.ToString("yyyy", CultureInfo.InvariantCulture), when.ToString("MM", CultureInfo.InvariantCulture))
                : destination, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in videos)
        {
            var job = await transfers.EnqueueAsync(deviceId, TransferKind.CopyToPc,
                new CopyToPcRequest(IosMediaRoot, folder.Select(a => "/" + a.LivePairId).ToList(), folder.Key), ConflictRule.KeepBoth, ct).ConfigureAwait(false);
            jobs.Add(job.Id);
        }

        return jobs;
    }

    /// <summary>The iPhone's AFC media root, where DCIM is (IosDevice.MediaRoot).</summary>
    private const string IosMediaRoot = "media";

    /// <summary>Where the deleted items went: the one place they all went to, else what the device said before.</summary>
    public static PhotoDeleteDestination WentTo(IReadOnlyList<PhotoDeleteResult> results, PhotoDeleteDestination fallback)
    {
        var seen = results.Where(r => r.Outcome == PhotoDeleteOutcome.Deleted && r.WentTo != PhotoDeleteDestination.Unknown)
            .Select(r => r.WentTo).Distinct().ToList();
        return seen.Count == 1 ? seen[0] : fallback;
    }

    private static PhotoDeleteResult[] Fill(IReadOnlyList<PhotoDeleteItem> items, PhotoDeleteResult?[] results, Dictionary<string, PhotoAsset> library, string why)
    {
        for (var i = 0; i < results.Length; i++)
        {
            results[i] ??= Result(items[i], library.GetValueOrDefault(items[i].Id), PhotoDeleteOutcome.NotTried, null, why);
        }

        return results!;
    }

    private static PhotoDeleteResult Result(PhotoDeleteItem item, PhotoAsset? asset, PhotoDeleteOutcome outcome, string? code, string? message) =>
        new(item.Id, asset?.FileName ?? item.Id[(item.Id.LastIndexOf('/') + 1)..], item.Size, outcome, code, message, [], PhotoDeleteDestination.Unknown);

    internal static async Task<Dictionary<string, PhotoAsset>> ReadLibraryAsync(IDevice device, CancellationToken ct)
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
}
