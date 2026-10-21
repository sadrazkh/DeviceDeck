using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Device.Photos;

/// <summary>
/// The backup view of the library. Identity is the photo's ZUUID in Photos.sqlite ("uuid:…"), which outlives renames
/// and the IMG_ numbers coming round again; without the database it is the DCIM path ("dcim:…"). Every asset is
/// listed, Hidden and iCloud-only ones marked, Recently Deleted left out (as in the index).
/// </summary>
public sealed partial class IosPhotoLibrary : IPhotoBackupSource
{
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
            Motion: a.LivePairId is { } pair ? new PhotoBackupMotion(pair, Path.GetFileName(pair), 0) : null)).ToList();
    }

    /// <summary>
    /// One AFC stat per file whose size or date the index lacks (files taken after Photos.sqlite was copied) and per
    /// Live Photo video, on a few connections side by side.
    /// </summary>
    public async Task<IReadOnlyList<PhotoBackupAsset>> CompleteAsync(IReadOnlyList<PhotoBackupAsset> assets, CancellationToken ct)
    {
        var paths = assets
            .SelectMany(a => (a.Size == 0 || a.TakenAt is null ? [a.AssetId] : Array.Empty<string>())
                .Concat(a.Motion is { Size: 0 } m ? [m.AssetId] : []))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (paths.Count == 0)
            return assets;

        var stats = await StatAsync(paths, ct).ConfigureAwait(false);

        return assets.Select(a =>
        {
            var still = stats.GetValueOrDefault(a.AssetId);
            var motion = a.Motion is { } m && stats.GetValueOrDefault(m.AssetId) is { } info ? m with { Size = info.Size } : a.Motion;
            return a with
            {
                Size = a.Size > 0 ? a.Size : still?.Size ?? 0,
                TakenAt = a.TakenAt ?? still?.Created ?? still?.Modified,
                Motion = motion,
            };
        }).ToList();
    }
}
