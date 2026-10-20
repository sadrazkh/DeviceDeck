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

    private (long Bytes, DateTime AtUtc)? _videoBytes;

    /// <summary>
    /// Bytes of the camera roll's videos (hidden and recently deleted included: they take space), from the DCIM
    /// listing plus one stat per video. A video whose name matches a photo's is that Live Photo's motion and counts
    /// as photo. About 0.3 s for 4,500 files on an idle USB link; cached for two minutes.
    /// </summary>
    public async Task<long> GetVideoBytesAsync(CancellationToken ct)
    {
        if (_videoBytes is { } cached && DateTime.UtcNow - cached.AtUtc < TimeSpan.FromMinutes(2))
            return cached.Bytes;
        var files = await device.UseMediaAfcAsync(afc => ListDcimAsync(afc, ct), ct).ConfigureAwait(false);
        var videos = StandAloneVideos(files).ToList();
        var total = await device.UseAsync(async c =>
        {
            // AFC answers one request at a time per connection, and while the link is busy (the Photos.sqlite copy)
            // each answer waits behind bulk data; a few connections side by side keep the stats flowing.
            var lanes = Math.Clamp(videos.Count / 16, 1, StatLanes);
            var sums = await Task.WhenAll(Enumerable.Range(0, lanes).Select(async lane =>
            {
                await using var afc = await c.StartAfcAsync(ct).ConfigureAwait(false);
                long sum = 0;
                for (var i = lane; i < videos.Count; i += lanes)
                    sum += (await afc.GetFileInfoAsync("/" + videos[i], ct).ConfigureAwait(false))?.Size ?? 0;
                return sum;
            })).ConfigureAwait(false);
            return sums.Sum();
        }).ConfigureAwait(false);
        _videoBytes = (total, DateTime.UtcNow);
        return total;
    }

    private const int StatLanes = 4;

    /// <summary>Video files that are not the motion half of a Live Photo (no photo with the same name beside them).</summary>
    internal static IEnumerable<string> StandAloneVideos(IReadOnlyCollection<string> dcimFiles)
    {
        var stills = dcimFiles.Where(f => !PhotoIndex.IsVideoFile(f))
            .Select(f => Path.ChangeExtension(f, null))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return dcimFiles.Where(f => PhotoIndex.IsVideoFile(f) && !stills.Contains(Path.ChangeExtension(f, null)));
    }

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
        return await device.UseMediaAfcAsync(async afc =>
        {
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
        }, ct).ConfigureAwait(false);
    }

    public Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct)
    {
        if (TryValidateId(assetId, out var id) is { } invalid)
            return Task.FromException<byte[]?>(invalid);
        return device.UseMediaAfcAsync<byte[]?>(async afc =>
        {
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
        }, ct);
    }

    public Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        if (TryValidateId(assetId, out var id) is { } invalid)
            return Task.FromException(invalid);
        // Not retried on a dead connection: part of the file may already be in destination.
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
            var files = await device.UseMediaAfcAsync(afc => ListDcimAsync(afc, ct), ct).ConfigureAwait(false);
            var database = await device.UseAsync(c => LoadDatabaseAsync(c, ct)).ConfigureAwait(false);
            _index = database.Rows is null
                ? PhotoIndex.Build(files, null, null, fallbackReason: database.Note)
                : PhotoIndex.Build(files, database.Rows, database.Albums, database.Variant, note: database.Note);
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

    private sealed record DatabaseResult(IReadOnlyList<PhotoRow>? Rows, IReadOnlyList<AlbumRow>? Albums, string? Variant, string Note);

    /// <summary>
    /// Copies Photos.sqlite (on its own AFC connection, so a 150 MB copy does not hold up thumbnails) and reads it.
    /// A dropped connection, or the database changing under the copy, is retried; anything else falls back to DCIM.
    /// </summary>
    private async Task<DatabaseResult> LoadDatabaseAsync(DeviceConnector c, CancellationToken ct)
    {
        const int attempts = 3;
        Exception? last = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string? local;
                string copied;
                await using (var afc = await c.StartAfcAsync(ct).ConfigureAwait(false))
                    (local, copied) = await CopyDatabaseAsync(afc, ct).ConfigureAwait(false);
                if (local is null)
                    return new DatabaseResult(null, null, null, "The phone has no /PhotoData/Photos.sqlite.");
                using var db = PhotosDatabase.Open(local);
                var rows = db.ReadAssets();
                var albums = db.ReadAlbums();
                var retried = attempt > 1 ? $", attempt {attempt} (before: {last?.GetType().Name}: {last?.Message})" : "";
                return new DatabaseResult(rows, albums, db.Schema.Variant, $"{copied}; ready in {sw.Elapsed.TotalSeconds:0.0} s{retried}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex))
            {
                last = ex;
                if (ex is SqliteException)
                    DeleteCache(); // a torn copy: start again from scratch
            }
            catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound or DeviceErrorCodes.Protocol)
            {
                return new DatabaseResult(null, null, null, "Photos.sqlite could not be copied: " + ex.Message);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                return new DatabaseResult(null, null, null, "Photos.sqlite could not be read: " + ex.Message);
            }
        }
        return new DatabaseResult(null, null, null, $"Photos.sqlite could not be copied or read after {attempts} attempts: {last?.Message}");
    }

    /// <summary>Connection drops, a database that changed while it was copied, and a copy SQLite cannot read.</summary>
    private static bool IsRetryable(Exception ex) =>
        ex is EndOfStreamException or DatabaseChangedException or SqliteException
        || ex is IOException { InnerException: System.Net.Sockets.SocketException }
        || ex is DeviceException { Code: DeviceErrorCodes.Disconnected };

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
    /// Brings the cached copy of Photos.sqlite and its -wal up to date and returns its path (null when the phone
    /// has none) with a note of what was copied. The 150 MB main file is copied only when it changed on the phone;
    /// usually only the few-MB WAL has. The -shm index is never copied; SQLite rebuilds it from the WAL.
    /// </summary>
    private async Task<(string? Path, string Copied)> CopyDatabaseAsync(AfcClient afc, CancellationToken ct)
    {
        var main = await afc.GetFileInfoAsync(PhotosDb, ct).ConfigureAwait(false);
        if (main is null)
            return (null, "");
        var wal = await afc.GetFileInfoAsync(PhotosDb + "-wal", ct).ConfigureAwait(false);
        var mainStamp = Stamp(main);
        var walStamp = wal is null ? "none" : Stamp(wal);

        Directory.CreateDirectory(CacheDirectory);
        var local = Path.Combine(CacheDirectory, "Photos.sqlite");
        var stampFile = Path.Combine(CacheDirectory, "Photos.stamp");
        var stored = File.Exists(stampFile) ? await File.ReadAllLinesAsync(stampFile, ct).ConfigureAwait(false) : [];
        var haveMain = File.Exists(local) && stored.Length == 2 && stored[0] == mainStamp;
        var haveWal = haveMain && stored[1] == walStamp && (wal is null || File.Exists(local + "-wal"));

        // The wal-index is only a cache of the WAL; a stale one must not outlive the WAL it described.
        File.Delete(local + "-shm");
        if (haveMain && haveWal)
            return (local, "reused the cached copy");

        File.Delete(stampFile);
        var copied = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!haveMain)
        {
            File.Delete(local);
            await CopyAsync(afc, PhotosDb, local, ct).ConfigureAwait(false);
            copied.Add(SizeText(main.Size));
        }
        File.Delete(local + "-wal");
        if (wal is not null)
        {
            await CopyAsync(afc, PhotosDb + "-wal", local + "-wal", ct).ConfigureAwait(false);
            copied.Add(SizeText(wal.Size) + " WAL");
        }

        // A checkpoint during the copy rewrites the main file and restarts the WAL: the two copies would not match.
        var after = await afc.GetFileInfoAsync(PhotosDb, ct).ConfigureAwait(false);
        if (after is null || Stamp(after) != mainStamp)
            throw new DatabaseChangedException();

        await File.WriteAllLinesAsync(stampFile, [mainStamp, walStamp], ct).ConfigureAwait(false);
        var seconds = sw.Elapsed.TotalSeconds;
        var bytes = (haveMain ? 0 : main.Size) + (wal?.Size ?? 0);
        var rate = seconds > 0 ? bytes / seconds / 1e6 : 0;
        return (local, $"copied {string.Join(" + ", copied)} in {seconds:0.0} s ({rate:0.0} MB/s)");
    }

    private static string Stamp(AfcFileInfo info) => $"{info.Size}:{info.Modified?.UtcTicks}";

    private static string SizeText(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1e6:0.0} MB" : $"{bytes / 1e3:0.0} KB";

    private static async Task CopyAsync(AfcClient afc, string devicePath, string localPath, CancellationToken ct)
    {
        var temp = localPath + ".part";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await afc.ReadFileAsync(devicePath, file, null, ct).ConfigureAwait(false);
        File.Move(temp, localPath, overwrite: true);
    }

    private sealed class DatabaseChangedException() : IOException("Photos.sqlite changed on the phone while it was being copied.");

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
