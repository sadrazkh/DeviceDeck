using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Settings;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.Transfers.Handlers;

/// <summary>photos.export: originals into a PC folder, optionally in yyyy/MM sub-folders and with HEIC turned into JPEG.</summary>
public sealed class PhotosExportHandler(IImageTranscoder? transcoder = null) : TransferHandler<PhotosExportRequest>
{
    private const int PageSize = 500;

    public override TransferKind Kind => TransferKind.PhotosExport;

    protected override Task<string> DescribeAsync(IDevice device, PhotosExportRequest request, CancellationToken ct)
    {
        RequireList(request.AssetIds, "photo");
        RequirePcFolder(request.Destination, "The destination");
        if (request.FolderPattern is not (AppSettings.PatternYearMonth or AppSettings.PatternNone))
        {
            throw new ArgumentException("folderPattern must be \"yyyy/MM\" or \"\".");
        }

        return Task.FromResult($"Exporting {Plural(request.AssetIds.Distinct().Count(), "photo", "photos")} to {request.Destination}");
    }

    protected override async Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, PhotosExportRequest request, CancellationToken ct)
    {
        // IPhotoLibrary has no lookup by id, so read the library page by page and keep the selected ones.
        var wanted = request.AssetIds.Distinct(StringComparer.Ordinal).ToList();
        var lookup = wanted.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, PhotoAsset>(StringComparer.Ordinal);
        for (var offset = 0; found.Count < lookup.Count;)
        {
            var page = await context.Device.Photos.GetAssetsAsync(null, offset, PageSize, ct).ConfigureAwait(false);
            foreach (var asset in page.Items)
            {
                if (lookup.Contains(asset.Id))
                {
                    found[asset.Id] = asset;
                }
            }

            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total)
            {
                break;
            }
        }

        var convert = request.ConvertHeic && transcoder is not null;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<TransferItemPlan>(wanted.Count);
        foreach (var id in wanted)
        {
            if (!found.TryGetValue(id, out var asset))
            {
                // Deleted since it was selected: keep an item so the job reports it as failed.
                plans.Add(new TransferItemPlan(id, Path.Combine(request.Destination, SafeName(id)), id, 0));
                continue;
            }

            var folder = request.FolderPattern == AppSettings.PatternYearMonth && asset.TakenAt is { } when
                ? Path.Combine(request.Destination, when.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), when.ToString("MM", System.Globalization.CultureInfo.InvariantCulture))
                : request.Destination;
            var name = SafeName(asset.FileName);
            if (convert && ImageFormats.IsHeic(name))
            {
                name = Path.ChangeExtension(name, ".jpg");
            }

            // Two photos with one name (IMG_0001 comes round again) get "(1)" here, before any file exists.
            var destination = Path.Combine(folder, name);
            for (var n = 1; !taken.Add(destination); n++)
            {
                destination = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            }

            plans.Add(new TransferItemPlan(asset.Id, destination, asset.FileName, asset.Size));
        }

        return plans;
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, PhotosExportRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var destination = ResolvePcDestination(context, item, item.Destination);
        if (destination is null)
        {
            return TransferItemState.Skipped;
        }

        item.Destination = destination;
        var photos = context.Device.Photos;
        var convert = request.ConvertHeic && transcoder is not null && ImageFormats.IsHeic(item.Name);
        if (!convert)
        {
            await WritePcFileAsync(destination, item.Size, file => photos.ReadOriginalAsync(item.Source, file, progress, ct), ct).ConfigureAwait(false);
            return TransferItemState.Done;
        }

        // Fetch the HEIC next to the target, convert, then keep the JPEG (or the HEIC when Windows lacks the codec).
        var original = destination + ".heic" + PartialSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var file = new FileStream(original, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                await photos.ReadOriginalAsync(item.Source, file, progress, ct).ConfigureAwait(false);
                if (item.Size > 0 && file.Length != item.Size)
                {
                    throw new DeviceException(DeviceErrorCodes.Protocol, "The copy came out the wrong size.", new IOException($"Expected {item.Size} bytes, got {file.Length}."));
                }

                // The photo's EXIF (date, camera, place) goes into the JPEG; read it first, the transcoder may close the stream.
                file.Position = 0;
                var exif = PhotosHeicMetadata.ReadExif(file);
                file.Position = 0;
                var jpeg = await transcoder!.ToJpegAsync(file, null, ct).ConfigureAwait(false);
                if (jpeg is not null)
                {
                    jpeg = exif is null ? jpeg : PhotosHeicMetadata.WithExif(jpeg, exif);
                    await WritePcFileAsync(destination, jpeg.Length, async target => await target.WriteAsync(jpeg, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                    return TransferItemState.Done;
                }
            }

            var keep = Path.ChangeExtension(destination, Path.GetExtension(item.Name));
            if (File.Exists(keep))
            {
                keep = UniquePcPath(keep);
            }

            File.Move(original, keep);
            item.Destination = keep;
            return TransferItemState.Done;
        }
        finally
        {
            TryDelete(original);
        }
    }
}
