using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Photos;

/// <summary>
/// Photos and videos through MediaStore (`content query`), albums = buckets (folders). The index is read once and
/// reused for paging and album switches; asking for a first page again re-reads it once it is older than
/// <see cref="IndexMaxAge"/>, and deletes and imports drop it.
/// </summary>
internal sealed partial class AndroidPhotoLibrary(IAdbChannel channel, Func<int> sdk) : IPhotoLibrary
{
    public const string ImportFolder = "/sdcard/DCIM/DeviceDeck";

    /// <summary>Originals larger than this get MediaStore's thumbnail instead of being pulled whole.</summary>
    private const long MaxOriginalForThumbnail = 48L * 1024 * 1024;

    private readonly SemaphoreSlim _indexGate = new(1, 1);
    // Thumbnails pull originals; a gallery page asks for dozens at once.
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly MediaScanner _scanner = new(channel);
    private IReadOnlyList<MediaItem>? _index;
    private DateTime _indexTime;

    /// <summary>The index by MediaStore id, made once per index: a backup looks up every photo it copies.</summary>
    private (IReadOnlyList<MediaItem> Index, Dictionary<long, MediaItem> ById)? _byId;

    public static readonly TimeSpan IndexMaxAge = TimeSpan.FromSeconds(10);

    public async IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var items = await GetIndexAsync(refresh: _index == null, ct).ConfigureAwait(false);
        foreach (var album in MediaQuery.Albums(items))
        {
            yield return album;
        }
    }

    public async Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct)
    {
        bool stale = _index == null || (offset == 0 && DateTime.UtcNow - _indexTime > IndexMaxAge);
        var items = await GetIndexAsync(refresh: stale, ct).ConfigureAwait(false);
        var filtered = MediaQuery.Filter(items, albumId).ToList();
        var page = filtered.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 0, 10_000)).Select(i => i.ToAsset()).ToList();
        return new Page<PhotoAsset>(page, filtered.Count);
    }

    public async Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct)
    {
        var item = await FindAsync(assetId, ct).ConfigureAwait(false);
        await _thumbnailGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!item.IsVideo && Thumbnailer.CanDecode(item.MimeType) && item.Size is > 0 and <= MaxOriginalForThumbnail)
            {
                using var original = new MemoryStream((int)item.Size);
                try
                {
                    await channel.PullAsync(item.Path, original, null, ct).ConfigureAwait(false);
                    if (Thumbnailer.Create(original.ToArray(), size) is { } jpeg)
                    {
                        return jpeg;
                    }
                }
                catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PathNotFound or DeviceErrorCodes.PermissionDenied)
                {
                    // Moved or deleted since the index was read; MediaStore may still have a thumbnail.
                }
            }
            // Video, HEIC, RAW or a file Skia can't read: MediaStore makes one (openFile on .../media/<id>/thumbnail).
            using var thumb = new MemoryStream();
            string kind = item.IsVideo ? "video" : "images";
            var result = await channel.ShellToStreamAsync(
                $"content read --uri content://media/external/{kind}/media/{item.Id}/thumbnail", thumb, ct).ConfigureAwait(false);
            if (!result.Succeeded || thumb.Length == 0)
            {
                return null;
            }
            return Thumbnailer.Create(thumb.ToArray(), size);
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    public async Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var item = await FindAsync(assetId, ct).ConfigureAwait(false);
        await channel.PullAsync(item.Path, destination, progress, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct)
    {
        if (assetIds.Count == 0)
        {
            return;
        }
        var ids = assetIds.Select(ParseId).Distinct().ToList();
        foreach (var chunk in ids.Chunk(200))
        {
            string where = $"_id IN ({string.Join(',', chunk)})";
            // MediaProvider deletes the file along with the row; the shell is allowed to on Android 10-16.
            await channel.ShellAsync($"content delete --uri {MediaQuery.FilesUri} --where {ShellQuote.Quote(where)}", ct).ConfigureAwait(false);
        }

        // Whatever is still indexed (an OEM MediaProvider that refused): remove the file and rescan the path.
        var remaining = await QueryAsync($"_id IN ({string.Join(',', ids)})", ct).ConfigureAwait(false);
        if (remaining.Count > 0)
        {
            var leftovers = remaining.Select(i => i.Path).ToList();
            string rm = "rm -f -- " + string.Join(' ', leftovers.Select(ShellQuote.Quote));
            var result = await channel.ShellAsync(rm, ct).ConfigureAwait(false);
            await _scanner.ScanAsync(leftovers, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _index = null;
                throw AdbErrors.FromShell("rm", result);
            }
        }
        _index = null;
    }

    public async Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct)
    {
        string name = SafeFileName(fileName);
        await channel.ShellCheckedAsync($"mkdir -p {ShellQuote.Quote(ImportFolder)}", ct).ConfigureAwait(false);
        string target = await UniquePathAsync(ImportFolder, name, ct).ConfigureAwait(false);
        await channel.PushAsync(source, target, progress, ct).ConfigureAwait(false);

        await _scanner.ScanAsync([target], ct).ConfigureAwait(false);
        // The scan runs asynchronously on the phone; wait briefly so the next listing shows the import.
        string canonical = MediaScanner.CanonicalPath(target);
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if ((await QueryAsync($"_data={ShellQuote.SqlString(canonical)}", ct).ConfigureAwait(false)).Count > 0)
            {
                break;
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        _index = null;
    }

    private async Task<IReadOnlyList<MediaItem>> GetIndexAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _index != null)
        {
            return _index;
        }
        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _index != null)
            {
                return _index;
            }
            var result = await channel.ShellAsync(MediaQuery.Command(sdk()), ct).ConfigureAwait(false);
            if (!result.Succeeded && !result.Output.Contains("Row:", StringComparison.Ordinal))
            {
                throw AdbErrors.FromShell("content", result);
            }
            _index = MediaQuery.Parse(result.Output, sdk());
            _indexTime = DateTime.UtcNow;
            return _index;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task<IReadOnlyList<MediaItem>> QueryAsync(string where, CancellationToken ct)
    {
        var result = await channel.ShellAsync(MediaQuery.Command(sdk(), $"({where}) AND media_type IN (1,3)"), ct).ConfigureAwait(false);
        return MediaQuery.Parse(result.Output, sdk());
    }

    private async Task<MediaItem> FindAsync(string assetId, CancellationToken ct)
    {
        long id = ParseId(assetId);
        var items = await GetIndexAsync(refresh: false, ct).ConfigureAwait(false);
        var lookup = _byId;
        if (lookup is not { } current || !ReferenceEquals(current.Index, items))
        {
            var byId = new Dictionary<long, MediaItem>(items.Count);
            foreach (var each in items)
            {
                byId.TryAdd(each.Id, each);
            }
            lookup = (items, byId);
            _byId = lookup;
        }
        if (lookup.Value.ById.TryGetValue(id, out var item))
        {
            return item;
        }
        // Not in our snapshot (added since): ask MediaStore directly.
        return (await QueryAsync($"_id={id}", ct).ConfigureAwait(false)).FirstOrDefault()
            ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That photo isn't on the phone any more.");
    }

    private async Task<string> UniquePathAsync(string folder, string name, CancellationToken ct)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int n = 0; n < 1000; n++)
        {
            string candidate = $"{folder}/{(n == 0 ? name : $"{stem} ({n}){extension}")}";
            var exists = await channel.ShellAsync($"[ -e {ShellQuote.Quote(candidate)} ]", ct).ConfigureAwait(false);
            if (!exists.Succeeded)
            {
                return candidate;
            }
        }
        throw new DeviceException(DeviceErrorCodes.AlreadyExists, $"Too many files named {name} in DCIM/DeviceDeck.");
    }

    [GeneratedRegex(@"[\x00-\x1F/\\:*?""<>|]")]
    private static partial Regex UnsafeChars();

    internal static string SafeFileName(string fileName)
    {
        // Not Path.GetFileName: on Windows it reads "a:b.jpg" as a drive letter.
        string normalized = fileName.Replace('\\', '/');
        string name = UnsafeChars().Replace(normalized[(normalized.LastIndexOf('/') + 1)..], "_").Trim().TrimStart('.');
        if (name.Length == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "The file has no usable name.");
        }
        return name.Length > 200 ? name[..200] : name;
    }

    private static long ParseId(string assetId) =>
        long.TryParse(assetId, NumberStyles.None, CultureInfo.InvariantCulture, out long id)
            ? id
            : throw new DeviceException(DeviceErrorCodes.PathNotFound, "That isn't a photo id from this phone.");
}
