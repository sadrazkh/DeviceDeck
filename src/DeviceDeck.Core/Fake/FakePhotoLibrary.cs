using System.Runtime.CompilerServices;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Extensions;

namespace DeviceDeck.Core.Fake;

internal sealed class FakePhotoLibrary : IPhotoLibrary, IPhotoIndexProgressSource
{
    private readonly FakeDevice _device;
    private readonly List<PhotoAsset> _assets;
    private readonly Dictionary<string, PhotoAsset> _byId;
    private readonly List<(PhotoAlbum Album, List<string> Members)> _albums;
    private int _imported;

    public FakePhotoLibrary(FakeDevice device)
    {
        _device = device;
        _assets = device.Spec.Assets.ToList();
        _byId = _assets.ToDictionary(a => a.Id, StringComparer.Ordinal);
        _albums = device.Spec.Albums.Select(a => (a.Album, a.AssetIds.ToList())).ToList();
    }

    public event EventHandler<PhotoIndexProgress>? IndexProgress;

    internal int Count
    {
        get
        {
            lock (_device.Gate)
            {
                return _assets.Count;
            }
        }
    }

    internal void RaiseIndexProgress(int done, int total) => IndexProgress?.Invoke(this, new PhotoIndexProgress(done, total));

    public async IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        _device.EnsureConnected();
        _device.Require(DeviceCapability.PhotosRead, "show photos");
        PhotoAlbum[] albums;
        lock (_device.Gate)
        {
            albums = _albums.Select(a => a.Album with { Count = a.Album.Kind == AlbumKind.All ? _assets.Count : a.Members.Count }).ToArray();
        }

        foreach (var album in albums)
        {
            ct.ThrowIfCancellationRequested();
            yield return album;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct)
    {
        _device.EnsureConnected();
        _device.Require(DeviceCapability.PhotosRead, "show photos");
        lock (_device.Gate)
        {
            IReadOnlyList<PhotoAsset> source;
            var album = albumId is null ? default : _albums.FirstOrDefault(a => a.Album.Id == albumId);
            if (albumId is null || album.Album?.Kind == AlbumKind.All)
            {
                source = _assets;
            }
            else if (album.Album is null)
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That album isn't on the phone any more.");
            }
            else
            {
                source = album.Members.Select(id => _byId[id]).ToArray();
            }

            var items = source.Skip(offset).Take(limit).ToArray();
            return Task.FromResult(new Page<PhotoAsset>(items, source.Count));
        }
    }

    public Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct)
    {
        _device.EnsureConnected();
        PhotoAsset? asset;
        lock (_device.Gate)
        {
            _byId.TryGetValue(assetId, out asset);
        }

        if (asset is null || !_device.Spec.ThumbnailsOnPhone)
        {
            return Task.FromResult<byte[]?>(null);
        }

        var (w, h) = Fit(asset, Math.Clamp(size, 32, 1024));
        return Task.Run<byte[]?>(() => FakeArt.Photo(asset.Id, w, h, dark: asset.Kind == PhotoKind.Video), ct);
    }

    public async Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        _device.EnsureConnected();
        var asset = Find(assetId);
        if (asset.InCloudOnly)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "This photo is stored in iCloud only, so it can't be copied over USB.");
        }

        // A real picture first (so viewers open it), then filler up to the size the library reports.
        var (w, h) = Fit(asset, 1024);
        var head = await Task.Run(() => FakeArt.Photo(asset.Id, w, h, dark: asset.Kind == PhotoKind.Video), ct).ConfigureAwait(false);
        await FakeIo.ProduceAsync(_device, head, Math.Max(asset.Size, head.Length), destination, progress, ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct)
    {
        _device.EnsureConnected();
        _device.Require(DeviceCapability.PhotosDelete, "delete photos");
        lock (_device.Gate)
        {
            var doomed = assetIds.Where(_byId.ContainsKey).ToHashSet(StringComparer.Ordinal);
            if (doomed.Count == 0)
            {
                return Task.CompletedTask;
            }

            _assets.RemoveAll(a => doomed.Contains(a.Id));
            foreach (var id in doomed)
            {
                _byId.Remove(id);
            }

            foreach (var (_, members) in _albums)
            {
                members.RemoveAll(doomed.Contains);
            }
        }

        return Task.CompletedTask;
    }

    public async Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct)
    {
        _device.EnsureConnected();
        _device.Require(DeviceCapability.PhotosImport, "add photos");
        var received = await FakeIo.ConsumeAsync(_device, source, length, progress, ct).ConfigureAwait(false);
        var isVideo = Path.GetExtension(fileName).ToLowerInvariant() is ".mp4" or ".mov" or ".mkv" or ".3gp";
        lock (_device.Gate)
        {
            var asset = new PhotoAsset(
                $"imported-{Interlocked.Increment(ref _imported)}-{FakeArt.StableHash(fileName):x}",
                Path.GetFileName(fileName),
                isVideo ? PhotoKind.Video : PhotoKind.Photo,
                DateTimeOffset.Now,
                received,
                isVideo ? 1920 : 4000,
                isVideo ? 1080 : 3000,
                isVideo ? 10 : null,
                IsFavorite: false,
                InCloudOnly: false,
                LivePairId: null);
            _assets.Insert(0, asset);
            _byId[asset.Id] = asset;
            foreach (var (album, members) in _albums)
            {
                if (album.Kind == AlbumKind.Camera || (album.Kind == AlbumKind.Folder && album.Name == "Camera") || (isVideo && album.Kind == AlbumKind.Videos))
                {
                    members.Insert(0, asset.Id);
                }
            }
        }
    }

    private PhotoAsset Find(string assetId)
    {
        lock (_device.Gate)
        {
            return _byId.TryGetValue(assetId, out var asset)
                ? asset
                : throw new DeviceException(DeviceErrorCodes.PathNotFound, "That photo isn't on the phone any more.");
        }
    }

    private static (int Width, int Height) Fit(PhotoAsset asset, int longest)
    {
        double w = asset.Width ?? 4, h = asset.Height ?? 3;
        var scale = longest / Math.Max(w, h);
        return (Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
    }
}
