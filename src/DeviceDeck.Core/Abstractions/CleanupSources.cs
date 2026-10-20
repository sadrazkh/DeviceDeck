namespace DeviceDeck.Core.Abstractions;

// Optional interfaces for storage clean-up (round 2). A device may implement any of them on top of IDevice;
// the clean-up service falls back to the round-1 members when it doesn't: originals read over USB and hashed
// on the PC, the AppDocuments roots walked folder by folder, no cache section.
// A member the device can't serve after all throws DeviceException(DeviceErrorCodes.NotSupported).

/// <summary>A device that hashes its own photos and videos, so the duplicate search doesn't copy whole files to the PC.</summary>
public interface IMediaHashSource
{
    /// <summary>
    /// Hashes the given photo-library assets. Android runs md5sum on the phone; an iPhone reads only
    /// <paramref name="prefixBytes"/> of each file over AFC for the cheap first pass.
    /// </summary>
    /// <param name="prefixBytes">Hash only the first N bytes of each file; null hashes whole files.</param>
    /// <param name="progress">Bytes hashed so far in this call.</param>
    /// <returns>Asset id → lowercase hex digest, for every asset the phone could read; the rest are left out.</returns>
    Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>A device that finds big files in the folders its apps keep in one go (Android: one `find` over shared storage).</summary>
public interface IAppFileScanner
{
    /// <summary>Files of at least <paramref name="minBytes"/>, biggest first, at most <paramref name="limit"/>.</summary>
    Task<AppFileScan> FindLargeAppFilesAsync(long minBytes, int limit, CancellationToken ct);
}

/// <param name="Root">The file root it lives in, as files.* methods take it ("internal", "app:org.videolan.vlc-ios").</param>
/// <param name="Path">Absolute inside the root, '/' separated.</param>
/// <param name="AppId">The app whose folder it is in; null for shared folders such as Download.</param>
/// <param name="Folder">A label for where it is when there is no app ("Download", "Documents").</param>
public sealed record AppFileHit(string Root, string Path, string Name, long Size, DateTimeOffset? Modified, string? AppId, string? Folder = null);

/// <param name="Unreadable">Folders the phone wouldn't let us list (Android/data on recent Android), for a note in the report.</param>
public sealed record AppFileScan(IReadOnlyList<AppFileHit> Files, IReadOnlyList<string> Unreadable);

/// <summary>A device whose apps' cache files can be cleared from the PC (Android: pm trim-caches).</summary>
public interface IAppCacheCleaner
{
    /// <summary>Cache bytes per app, as the phone last counted them.</summary>
    Task<AppCacheReport> GetCachesAsync(CancellationToken ct);

    /// <summary>Asks the system to delete every app's cache files. Returns the bytes freed (free space after minus before), never negative.</summary>
    Task<long> TrimCachesAsync(CancellationToken ct);
}

public sealed record AppCacheEntry(string AppId, long Bytes);

/// <param name="TotalBytes">Null when the phone hasn't counted its caches yet (Android counts about once a day, while charging).</param>
public sealed record AppCacheReport(long? TotalBytes, IReadOnlyList<AppCacheEntry> Apps);
