namespace DeviceDeck.Core.Abstractions;

// Optional: implemented by an IPhotoLibrary that can name its photos by a stable identity, so a backup to the PC
// copies each photo once. A library without it is read through IPhotoLibrary pages (see PhotoBackupSources).

/// <summary>A photo library that lists its originals for an incremental backup.</summary>
public interface IPhotoBackupSource
{
    /// <summary>
    /// Every photo and video in the library, newest first, including the ones only in iCloud (marked) and the
    /// Hidden album (marked). Fast: it may leave <see cref="PhotoBackupAsset.Size"/> at 0 and the Live Photo
    /// motion size unknown when finding them out costs one call per file; <see cref="CompleteAsync"/> fills them in.
    /// </summary>
    Task<IReadOnlyList<PhotoBackupAsset>> GetBackupAssetsAsync(CancellationToken ct);

    /// <summary>The same assets with sizes (and dates) the list left out; one stat per file that needs it.</summary>
    Task<IReadOnlyList<PhotoBackupAsset>> CompleteAsync(IReadOnlyList<PhotoBackupAsset> assets, CancellationToken ct);
}

/// <param name="Key">
/// Stable identity of the photo on this phone: "uuid:&lt;Photos.sqlite ZUUID&gt;" on an iPhone,
/// "file:&lt;path&gt;|&lt;size&gt;" on Android (a new size is a new version of the photo). Not a file name: IMG_0001 comes round again.
/// </param>
/// <param name="AssetId">The <see cref="IPhotoLibrary"/> id, for <see cref="IPhotoLibrary.ReadOriginalAsync"/>.</param>
/// <param name="Size">Bytes of the original; 0 = not known yet.</param>
/// <param name="SourcePath">Where the original lives on the phone ("DCIM/100APPLE/IMG_0001.HEIC"), when the library knows.</param>
/// <param name="Hidden">In the iPhone's Hidden album.</param>
/// <param name="Motion">The video half of a Live Photo; copied next to the still under the same name.</param>
public sealed record PhotoBackupAsset(
    string Key,
    string AssetId,
    string FileName,
    PhotoKind Kind,
    DateTimeOffset? TakenAt,
    long Size,
    bool InCloudOnly,
    bool Hidden,
    string? SourcePath,
    PhotoBackupMotion? Motion);

/// <param name="AssetId">Readable with <see cref="IPhotoLibrary.ReadOriginalAsync"/> (on an iPhone, the .MOV's DCIM path).</param>
/// <param name="Size">0 = not known yet.</param>
public sealed record PhotoBackupMotion(string AssetId, string FileName, long Size);
