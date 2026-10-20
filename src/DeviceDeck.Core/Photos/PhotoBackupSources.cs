using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Photos;

public static class PhotoBackupSources
{
    /// <summary>The library itself when it implements <see cref="IPhotoBackupSource"/>, else a reader over its pages.</summary>
    public static IPhotoBackupSource For(IPhotoLibrary library) => library as IPhotoBackupSource ?? new LibraryBackupSource(library);
}

/// <summary>
/// For a library that only has <see cref="IPhotoLibrary"/>: the asset id plus size is the identity, and a Live Photo's
/// motion half is the <see cref="PhotoAsset.LivePairId"/> with its size unknown.
/// </summary>
internal sealed class LibraryBackupSource(IPhotoLibrary library) : IPhotoBackupSource
{
    private const int PageSize = 1000;

    public async Task<IReadOnlyList<PhotoBackupAsset>> GetBackupAssetsAsync(CancellationToken ct)
    {
        var assets = new List<PhotoBackupAsset>();
        for (var offset = 0; ;)
        {
            var page = await library.GetAssetsAsync(null, offset, PageSize, ct).ConfigureAwait(false);
            assets.AddRange(page.Items.Select(From));
            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total)
            {
                return assets;
            }
        }
    }

    public Task<IReadOnlyList<PhotoBackupAsset>> CompleteAsync(IReadOnlyList<PhotoBackupAsset> assets, CancellationToken ct) =>
        Task.FromResult(assets);

    private static PhotoBackupAsset From(PhotoAsset a) => new(
        Key: $"id:{a.Id}|{a.Size}",
        AssetId: a.Id,
        FileName: a.FileName,
        Kind: a.Kind,
        TakenAt: a.TakenAt,
        Size: a.Size,
        InCloudOnly: a.InCloudOnly,
        Hidden: false,
        SourcePath: null,
        Motion: a.LivePairId is { } pair ? new PhotoBackupMotion(pair, MotionName(a.FileName, pair), 0) : null);

    private static string MotionName(string stillName, string pairId)
    {
        var last = pairId[(pairId.LastIndexOfAny(['/', '\\']) + 1)..];
        return Path.HasExtension(last) && PhotoBackupNaming.IsVideoName(last) ? last : Path.ChangeExtension(stillName, ".MOV");
    }
}
