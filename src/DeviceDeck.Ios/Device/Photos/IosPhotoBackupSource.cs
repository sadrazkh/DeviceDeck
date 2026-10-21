using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Device.Photos;

/// <summary>
/// The backup view of the library. Identity is the photo's ZUUID in Photos.sqlite ("uuid:…"), which outlives renames
/// and the IMG_ numbers coming round again; without the database it is the DCIM path ("dcim:…"). Every asset is
/// listed, Hidden and iCloud-only ones marked, Recently Deleted left out (as in the index). A photo edited in Photos
/// carries its edit stamp; its rendered file is looked up only when a backup asks (<see cref="CompleteAsync"/>).
/// </summary>
public sealed partial class IosPhotoLibrary : IPhotoBackupSource, IPhotoRangeReader
{
    /// <summary>Part of an original (a backup compares a sample before copying a whole photo), on a pooled connection.</summary>
    public async Task<byte[]> ReadRangeAsync(string assetId, long offset, int count, CancellationToken ct)
    {
        if (TryValidateId(assetId, out var id) is { } invalid)
            throw invalid;
        return await device.UseAsync(_ => _originals.UseAsync(afc => afc.ReadRangeAsync("/" + id, offset, count, ct), () => false, ct)).ConfigureAwait(false);
    }

    /// <summary>Where Photos keeps an edited photo's rendered version: Mutations, then the photo's DCIM folder and name.</summary>
    internal const string MutationsRoot = "PhotoData/Mutations/";

    public async Task<IReadOnlyList<PhotoBackupAsset>> GetBackupAssetsAsync(CancellationToken ct)
    {
        var index = await GetIndexAsync(refresh: false, ct).ConfigureAwait(false);
        return index.Assets.Select(a => new PhotoBackupAsset(
            Key: index.UuidOf(a.Id) is { } uuid ? "uuid:" + uuid : "dcim:" + a.Id,
            AssetId: a.Id,
            FileName: a.FileName,
            Kind: a.Kind,
            TakenAt: a.TakenAt,
            Size: a.Size,
            InCloudOnly: a.InCloudOnly,
            Hidden: index.IsHidden(a.Id),
            SourcePath: a.Id,
            Motion: a.LivePairId is { } pair ? new PhotoBackupMotion(pair, Path.GetFileName(pair), 0) : null,
            Edited: index.EditStampOf(a.Id) is { } stamp ? new PhotoBackupEdit(stamp) : null)).ToList();
    }

    /// <summary>
    /// One AFC stat per file whose size or date the index lacks (files taken after Photos.sqlite was copied) and per
    /// Live Photo video, on a few connections side by side; and for an edited photo, a look in its Adjustments folder.
    /// </summary>
    public async Task<IReadOnlyList<PhotoBackupAsset>> CompleteAsync(IReadOnlyList<PhotoBackupAsset> assets, CancellationToken ct)
    {
        var paths = assets
            .SelectMany(a => (a.Size == 0 || a.TakenAt is null ? [a.AssetId] : Array.Empty<string>())
                .Concat(a.Motion is { Size: 0 } m ? [m.AssetId] : []))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var edited = assets.Where(a => a.Edited is { AssetId: null }).Select(a => a.AssetId).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0 && edited.Count == 0)
            return assets;

        var stats = paths.Count == 0 ? [] : await StatAsync(paths, ct).ConfigureAwait(false);
        var renders = edited.Count == 0 ? [] : await FindRendersAsync(edited, ct).ConfigureAwait(false);

        return assets.Select(a =>
        {
            var still = stats.GetValueOrDefault(a.AssetId);
            var motion = a.Motion is { } m && stats.GetValueOrDefault(m.AssetId) is { } info ? m with { Size = info.Size } : a.Motion;
            var edit = a.Edited is { AssetId: null } e && renders.TryGetValue(a.AssetId, out var render)
                ? e with { AssetId = render.Path, FileName = Path.GetFileName(render.Path), Size = render.Size }
                : a.Edited;
            return a with
            {
                Size = a.Size > 0 ? a.Size : still?.Size ?? 0,
                TakenAt = a.TakenAt ?? still?.Created ?? still?.Modified,
                Motion = motion,
                Edited = edit,
            };
        }).ToList();
    }

    /// <summary>
    /// The rendered file of each edited photo: "FullSizeRender.*" in PhotoData/Mutations/&lt;DCIM folder&gt;/&lt;name&gt;/Adjustments/
    /// (an image for a photo, a movie for a video). A photo with no such folder or file is left out: not guessed.
    /// </summary>
    internal async Task<Dictionary<string, (string Path, long Size)>> FindRendersAsync(IReadOnlyList<string> assetIds, CancellationToken ct)
    {
        var lanes = Math.Clamp(assetIds.Count / 16, 1, StatLanes);
        var found = await Task.WhenAll(Enumerable.Range(0, lanes).Select(lane => device.UseAsync(_ => _originals.UseAsync(async afc =>
        {
            var mine = new List<(string AssetId, string Path, long Size)>();
            for (var i = lane; i < assetIds.Count; i += lanes)
            {
                var folder = AdjustmentsFolder(assetIds[i]);
                IReadOnlyList<string> names;
                try
                {
                    names = await afc.ReadDirectoryAsync("/" + folder, ct).ConfigureAwait(false);
                }
                catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PathNotFound or DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.Protocol)
                {
                    continue;
                }
                var video = PhotoIndex.IsVideoFile(assetIds[i]);
                var render = names
                    .Where(n => n.StartsWith("FullSizeRender.", StringComparison.OrdinalIgnoreCase) && PhotoIndex.IsMediaFile(n) && PhotoIndex.IsVideoFile(n) == video)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (render is null)
                    continue;
                var path = folder + "/" + render;
                if (await afc.GetFileInfoAsync("/" + path, ct).ConfigureAwait(false) is { IsDirectory: false } info)
                    mine.Add((assetIds[i], path, info.Size));
            }
            return mine;
        }, null, ct)))).ConfigureAwait(false);
        return found.SelectMany(f => f).ToDictionary(f => f.AssetId, f => (f.Path, f.Size), StringComparer.Ordinal);
    }

    /// <summary>"DCIM/100APPLE/IMG_0001.HEIC" → "PhotoData/Mutations/DCIM/100APPLE/IMG_0001/Adjustments".</summary>
    internal static string AdjustmentsFolder(string assetId) =>
        MutationsRoot + Path.ChangeExtension(assetId, null)!.Replace('\\', '/') + "/Adjustments";
}
