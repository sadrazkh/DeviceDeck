using System.Globalization;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Photos;

/// <summary>
/// The backup view of MediaStore. MediaStore's _id is not stable (a rebuilt media database renumbers everything), so
/// identity is the file's canonical path plus its size: "file:/storage/emulated/0/DCIM/Camera/PXL_….jpg|2817734".
/// An edited photo has a new size and is backed up again as a new version. Sizes come from MediaStore, so nothing
/// needs completing.
/// </summary>
internal sealed partial class AndroidPhotoLibrary : IPhotoBackupSource
{
    public async Task<IReadOnlyList<PhotoBackupAsset>> GetBackupAssetsAsync(CancellationToken ct)
    {
        var items = await GetIndexAsync(refresh: _index == null || DateTime.UtcNow - _indexTime > IndexMaxAge, ct).ConfigureAwait(false);
        return items.Select(item =>
        {
            var path = MediaScanner.CanonicalPath(item.Path);
            var asset = item.ToAsset();
            return new PhotoBackupAsset(
                Key: $"file:{path}|{item.Size.ToString(CultureInfo.InvariantCulture)}",
                AssetId: asset.Id,
                FileName: item.FileName,
                Kind: asset.Kind,
                TakenAt: item.TakenAt,
                Size: item.Size,
                InCloudOnly: false,
                Hidden: false,
                SourcePath: path,
                Motion: null);
        }).ToList();
    }

    public Task<IReadOnlyList<PhotoBackupAsset>> CompleteAsync(IReadOnlyList<PhotoBackupAsset> assets, CancellationToken ct) =>
        Task.FromResult(assets);
}
