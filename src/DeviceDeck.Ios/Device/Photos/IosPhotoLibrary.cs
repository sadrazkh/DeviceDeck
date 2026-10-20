using System.Runtime.CompilerServices;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Afc;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Device.Photos;

/// <summary>
/// Read-only photo library. Files come from DCIM over AFC; albums, favourites, dates and Live pairs come from
/// a temp copy of /PhotoData/Photos.sqlite when it can be copied and read, else from the DCIM listing alone.
/// Thumbnails are the phone's own JPEGs in /PhotoData/Thumbnails/V2 when present.
/// </summary>
public sealed class IosPhotoLibrary(IosDevice device, string cacheDirectory) : IPhotoLibrary
{
    private const string PhotosDb = "/PhotoData/Photos.sqlite";
    private const string ThumbnailRoot = "/PhotoData/Thumbnails/V2/";
    private const int MaxThumbnailBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private PhotoIndex? _index;
    private DateTime _indexBuiltUtc;

    public string CacheDirectory { get; } = cacheDirectory;

    public async IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var index = await GetIndexAsync(refresh: false, ct).ConfigureAwait(false);
        foreach (var album in index.Albums)
            yield return album;
    }

    public async Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct)
    {
        var index = await GetIndexAsync(refresh: false, ct).ConfigureAwait(false);
        var page = index.Page(albumId, offset, limit);
        if (!page.Items.Any(NeedsStat))
            return page;

        // Size and date for files Photos.sqlite did not describe: one stat each, only for this page.
        return await device.UseAsync(async c =>
        {
            await using var afc = await c.StartAfcAsync(ct).ConfigureAwait(false);
            var items = new List<PhotoAsset>(page.Items.Count);
            foreach (var asset in page.Items)
            {
                var filled = asset;
                if (NeedsStat(asset) && await afc.GetFileInfoAsync("/" + asset.Id, ct).ConfigureAwait(false) is { } info)
                {
                    filled = asset with { Size = asset.Size > 0 ? asset.Size : info.Size, TakenAt = asset.TakenAt ?? info.Created ?? info.Modified };
                    index.Update(filled);
                }
                items.Add(filled);
            }
            return new Page<PhotoAsset>(items, page.Total);
        }).ConfigureAwait(false);
    }

    public Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct)
    {
        if (TryValidateId(assetId, out var id) is { } invalid)
            return Task.FromException<byte[]?>(invalid);
        return device.UseAsync<byte[]?>(async c =>
        {
            await using var afc = await c.StartAfcAsync(ct).ConfigureAwait(false);
            IReadOnlyList<string> names;
            try
            {
                names = await afc.ReadDirectoryAsync(ThumbnailRoot + id, ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PathNotFound or DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.Protocol)
            {
                return null;
            }

            byte[]? best = null;
            var bestScore = int.MaxValue;
            foreach (var name in names.Where(n => n.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase)))
            {
                var path = ThumbnailRoot + id + "/" + name;
                var info = await afc.GetFileInfoAsync(path, ct).ConfigureAwait(false);
                if (info is null || info.Size is <= 0 or > MaxThumbnailBytes)
                    continue;
                using var buffer = new MemoryStream((int)info.Size);
                await afc.ReadFileAsync(path, buffer, null, ct).ConfigureAwait(false);
                var bytes = buffer.ToArray();
                if (JpegInfo.Size(bytes) is not { } dims)
                    continue;
                // Prefer the smallest one at least as big as asked for; otherwise the biggest.
                var longest = Math.Max(dims.Width, dims.Height);
                var score = longest >= size ? longest - size : 100_000 + (size - longest);
                if (score < bestScore)
                {
                    best = bytes;
                    bestScore = score;
                }
            }
            return best;
        });
    }

    public Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        if (TryValidateId(assetId, out var id) is { } invalid)
            return Task.FromException(invalid);
        return device.UseAsync(async c =>
        {
            await using var afc = await c.StartAfcAsync(ct).ConfigureAwait(false);
            await afc.ReadFileAsync("/" + id, destination, progress, ct).ConfigureAwait(false);
        });
    }

    public Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct) =>
        Task.FromException(IosErrors.NotSupported("Deleting photos on an iPhone is not supported; the library is read-only over USB."));

    public Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct) =>
        Task.FromException(IosErrors.NotSupported("Adding photos to an iPhone's library is not supported over USB."));

    /// <summary>The current index, rebuilt when older than a minute or when <paramref name="refresh"/> is set.</summary>
    public async Task<PhotoIndex> GetIndexAsync(bool refresh, CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _index is not null && DateTime.UtcNow - _indexBuiltUtc < IndexLifetime)
                return _index;
            _index = await device.UseAsync(c => BuildIndexAsync(c, ct)).ConfigureAwait(false);
            _indexBuiltUtc = DateTime.UtcNow;
            return _index;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    internal void DeleteCache()
    {
        try
        {
            if (Directory.Exists(CacheDirectory))
                Directory.Delete(CacheDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task<PhotoIndex> BuildIndexAsync(DeviceConnector c, CancellationToken ct)
    {
        await using var afc = await c.StartAfcAsync(ct).ConfigureAwait(false);
        var files = await ListDcimAsync(afc, ct).ConfigureAwait(false);

        string? reason;
        try
        {
            var local = await CopyDatabaseAsync(afc, ct).ConfigureAwait(false);
            if (local is null)
            {
                reason = "The phone has no /PhotoData/Photos.sqlite.";
            }
            else
            {
                using var db = PhotosDatabase.Open(local);
                return PhotoIndex.Build(files, db.ReadAssets(), db.ReadAlbums(), db.Schema.Variant);
            }
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound or DeviceErrorCodes.Protocol)
        {
            reason = "Photos.sqlite could not be copied: " + ex.Message;
        }
        catch (Exception ex) when (ex is SqliteException or NotSupportedException or InvalidOperationException or IOException)
        {
            reason = "Photos.sqlite could not be read: " + ex.Message;
        }
        return PhotoIndex.Build(files, null, null, fallbackReason: reason);
    }

    /// <summary>Every media file in /DCIM/*/, as "DCIM/&lt;folder&gt;/&lt;name&gt;".</summary>
    private static async Task<List<string>> ListDcimAsync(AfcClient afc, CancellationToken ct)
    {
        var files = new List<string>();
        IReadOnlyList<string> folders;
        try
        {
            folders = await afc.ReadDirectoryAsync("/DCIM", ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.PathNotFound)
        {
            return files; // a phone that never took a photo
        }
        foreach (var folder in folders.Where(f => !f.StartsWith('.')))
        {
            var info = await afc.GetFileInfoAsync("/DCIM/" + folder, ct).ConfigureAwait(false);
            if (info is not { IsDirectory: true })
                continue;
            var names = await afc.ReadDirectoryAsync("/DCIM/" + folder, ct).ConfigureAwait(false);
            files.AddRange(names.Where(PhotoIndex.IsMediaFile).Select(n => $"DCIM/{folder}/{n}"));
        }
        return files;
    }

    /// <summary>
    /// Copies Photos.sqlite and its -wal into the cache (only when they changed on the phone) and returns the
    /// local path, or null when the phone has none. The -shm index is not copied; SQLite rebuilds it.
    /// </summary>
    private async Task<string?> CopyDatabaseAsync(AfcClient afc, CancellationToken ct)
    {
        var main = await afc.GetFileInfoAsync(PhotosDb, ct).ConfigureAwait(false);
        if (main is null)
            return null;
        var wal = await afc.GetFileInfoAsync(PhotosDb + "-wal", ct).ConfigureAwait(false);
        var stamp = $"{main.Size}:{main.Modified?.UtcTicks}:{wal?.Size}:{wal?.Modified?.UtcTicks}";

        Directory.CreateDirectory(CacheDirectory);
        var local = Path.Combine(CacheDirectory, "Photos.sqlite");
        var stampFile = Path.Combine(CacheDirectory, "Photos.stamp");
        if (File.Exists(local) && File.Exists(stampFile) && await File.ReadAllTextAsync(stampFile, ct).ConfigureAwait(false) == stamp)
            return local;

        foreach (var stale in new[] { local, local + "-wal", local + "-shm", stampFile })
            File.Delete(stale);
        await CopyAsync(afc, PhotosDb, local, ct).ConfigureAwait(false);
        if (wal is not null)
            await CopyAsync(afc, PhotosDb + "-wal", local + "-wal", ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(stampFile, stamp, ct).ConfigureAwait(false);
        return local;
    }

    private static async Task CopyAsync(AfcClient afc, string devicePath, string localPath, CancellationToken ct)
    {
        var temp = localPath + ".part";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await afc.ReadFileAsync(devicePath, file, null, ct).ConfigureAwait(false);
        File.Move(temp, localPath, overwrite: true);
    }

    private static bool NeedsStat(PhotoAsset a) => !a.InCloudOnly && (a.Size == 0 || a.TakenAt is null);

    /// <summary>Asset ids are DCIM paths; anything else (or anything climbing out) is refused.</summary>
    private static DeviceException? TryValidateId(string assetId, out string id)
    {
        id = (assetId ?? "").Replace('\\', '/').TrimStart('/');
        if (!id.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase) || id.Split('/').Any(p => p is ".." or "." or ""))
            return new DeviceException(DeviceErrorCodes.NotFound, $"'{assetId}' is not a photo on this iPhone.");
        return null;
    }
}
