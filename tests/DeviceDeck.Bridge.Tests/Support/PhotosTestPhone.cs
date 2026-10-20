using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>
/// A phone for the backup tests: a library whose photos the test adds and removes, with Live Photos, Hidden and
/// iCloud-only ones, stable keys (<see cref="IPhotoBackupSource"/>), a pace for reads and a cable that can be pulled.
/// One app shares a Documents folder (an in-memory file system) for photos.importToApp.
/// </summary>
internal sealed class PhotosTestProvider : IDeviceProvider
{
    private readonly List<PhotosTestPhone> _devices = [];

    public PhotosTestProvider(params PhotosTestPhone[] phones)
    {
        _devices.AddRange(phones);
        foreach (var phone in phones)
        {
            phone.Provider = this;
        }
    }

    public string Id => "photos-test";

    public IReadOnlyList<IDevice> Devices
    {
        get
        {
            lock (_devices)
            {
                return _devices.Where(d => d.Plugged).ToArray();
            }
        }
    }

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    public Task StartAsync(CancellationToken ct)
    {
        foreach (var device in Devices)
        {
            DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Added, device.Summary));
        }

        return Task.CompletedTask;
    }

    public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "1", null));

    public void Unplug(PhotosTestPhone phone)
    {
        phone.Plugged = false;
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Removed, phone.Summary with { State = DeviceState.Disconnected }));
    }

    public void PlugBack(PhotosTestPhone phone)
    {
        phone.Plugged = true;
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Added, phone.Summary));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class PhotosTestPhone : IDevice
{
    public const string AppId = "com.readdle.ReaddleDocsIPad";

    public PhotosTestPhone(string id = "photos-iphone", DevicePlatform platform = DevicePlatform.Ios, string name = "Test iPhone")
    {
        Summary = new DeviceSummary(id, name, platform, "iPhone17,3", name, "26.0", ConnectionKind.Usb, DeviceState.Ready, DeviceMode.Full,
            DeviceCapability.Info | DeviceCapability.PhotosRead | DeviceCapability.AppDocuments | DeviceCapability.AppsList);
        Library = new PhotosTestLibrary(this);
    }

    public PhotosTestProvider? Provider { get; set; }

    public bool Plugged { get; set; } = true;

    public DeviceSummary Summary { get; }

    public PhotosTestLibrary Library { get; }

    public IPhotoLibrary Photos => Library;

    /// <summary>The shared app's Documents: path → bytes ("/IMG_1.jpg").</summary>
    public ConcurrentDictionary<string, byte[]> Documents { get; } = new(StringComparer.Ordinal);

    public void EnsureConnected()
    {
        if (!Plugged)
        {
            throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone was disconnected.");
        }
    }

    public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FileRoot>>([new FileRoot("app:" + AppId, "Documents", FileRootKind.AppDocuments, AppId)]);

    public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) =>
        rootKey == "app:" + AppId
            ? Task.FromResult<IDeviceFileSystem>(new DocumentsFileSystem(this))
            : throw new DeviceException(DeviceErrorCodes.PathNotFound, "No such root.");

    public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => Task.FromResult(new DeviceDetails([]));
    public IAppManager Apps => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public IDeviceActions Actions => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");

    private sealed class DocumentsFileSystem(PhotosTestPhone phone) : IDeviceFileSystem
    {
        public async IAsyncEnumerable<FileEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
        {
            phone.EnsureConnected();
            foreach (var (name, bytes) in phone.Documents)
            {
                yield return new FileEntry(name, name.TrimStart('/'), false, bytes.Length, DateTimeOffset.Now);
            }

            await Task.CompletedTask;
        }

        public Task<FileEntry?> StatAsync(string path, CancellationToken ct)
        {
            phone.EnsureConnected();
            if (path == "/")
            {
                return Task.FromResult<FileEntry?>(new FileEntry("/", "", true, 0, null));
            }

            return Task.FromResult(phone.Documents.TryGetValue(path, out var bytes) ? new FileEntry(path, path.TrimStart('/'), false, bytes.Length, DateTimeOffset.Now) : null);
        }

        public Task ReadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) => throw new NotSupportedException();

        public async Task WriteAsync(string path, Stream source, long length, IProgress<long>? progress, CancellationToken ct)
        {
            phone.EnsureConnected();
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, ct);
            phone.Documents[path] = copy.ToArray();
            progress?.Report(copy.Length);
        }

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteAsync(string path, bool recursive, CancellationToken ct)
        {
            phone.Documents.TryRemove(path, out _);
            return Task.CompletedTask;
        }

        public Task RenameAsync(string from, string to, CancellationToken ct)
        {
            phone.EnsureConnected();
            if (phone.Documents.TryRemove(from, out var bytes))
            {
                phone.Documents[to] = bytes;
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The phone's photos. Content is made from the asset id unless given, so equal ids give equal bytes.</summary>
internal sealed class PhotosTestLibrary(PhotosTestPhone phone) : IPhotoLibrary, IPhotoBackupSource
{
    private readonly Lock _gate = new();
    private readonly List<PhotoBackupAsset> _assets = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    /// <summary>Bytes a second for reads (null = as fast as possible). Reads come in 16 KB chunks.</summary>
    public long? BytesPerSecond { get; set; }

    /// <summary>When true the list leaves sizes at 0 and <see cref="CompleteAsync"/> fills them.</summary>
    public bool SizesNeedCompleting { get; set; }

    public int CompleteCalls { get; private set; }

    public int Reads { get; private set; }

    public static readonly DateTimeOffset Base = new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);

    public static byte[] ContentFor(string id, int length = 40_000)
    {
        var seed = Encoding.UTF8.GetBytes(id + "|");
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed[i % seed.Length] ^ (i * 31 % 251));
        }

        return bytes;
    }

    /// <summary>Adds a photo (or video); <paramref name="motion"/> makes it a Live Photo with a video named like it.</summary>
    public PhotoBackupAsset Add(string fileName, DateTimeOffset? takenAt = null, byte[]? content = null, bool motion = false, bool hidden = false,
        bool cloudOnly = false, string? key = null, PhotoKind? kind = null)
    {
        var id = "DCIM/100APPLE/" + fileName + "#" + Guid.NewGuid().ToString("N")[..6];
        var bytes = content ?? ContentFor(id);
        PhotoBackupMotion? pair = null;
        if (motion)
        {
            var motionId = id + ".MOV";
            var motionBytes = ContentFor(motionId, 25_000);
            pair = new PhotoBackupMotion(motionId, Path.ChangeExtension(fileName, ".MOV"), motionBytes.Length);
            lock (_gate)
            {
                _files[motionId] = motionBytes;
            }
        }

        var asset = new PhotoBackupAsset(
            key ?? "uuid:" + id,
            id,
            fileName,
            kind ?? (motion ? PhotoKind.LivePhoto : PhotoKind.Photo),
            takenAt ?? Base,
            bytes.Length,
            cloudOnly,
            hidden,
            id,
            pair);
        lock (_gate)
        {
            _assets.Insert(0, asset);
            _files[id] = bytes;
        }

        return asset;
    }

    /// <summary>Takes the video half away (the phone lists a Live Photo whose .MOV is gone).</summary>
    public void DropFile(string assetId)
    {
        lock (_gate)
        {
            _files.Remove(assetId);
        }
    }

    public byte[] FileOf(string assetId)
    {
        lock (_gate)
        {
            return _files[assetId];
        }
    }

    public Task<IReadOnlyList<PhotoBackupAsset>> GetBackupAssetsAsync(CancellationToken ct)
    {
        phone.EnsureConnected();
        lock (_gate)
        {
            IReadOnlyList<PhotoBackupAsset> list = _assets
                .Select(a => SizesNeedCompleting ? a with { Size = 0, Motion = a.Motion is { } m ? m with { Size = 0 } : null } : a)
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<PhotoBackupAsset>> CompleteAsync(IReadOnlyList<PhotoBackupAsset> assets, CancellationToken ct)
    {
        phone.EnsureConnected();
        CompleteCalls++;
        lock (_gate)
        {
            IReadOnlyList<PhotoBackupAsset> list = assets.Select(a => _assets.First(x => x.AssetId == a.AssetId)).ToList();
            return Task.FromResult(list);
        }
    }

    public async IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new PhotoAlbum("all", "All", AlbumKind.All, _assets.Count);
        await Task.CompletedTask;
    }

    public Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct)
    {
        lock (_gate)
        {
            var items = _assets.Skip(offset).Take(limit)
                .Select(a => new PhotoAsset(a.AssetId, a.FileName, a.Kind, a.TakenAt, a.Size, 400, 300, null, false, a.InCloudOnly, a.Motion?.AssetId))
                .ToList();
            return Task.FromResult(new Page<PhotoAsset>(items, _assets.Count));
        }
    }

    public Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct) => Task.FromResult<byte[]?>(null);

    public async Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        phone.EnsureConnected();
        byte[]? bytes;
        lock (_gate)
        {
            _files.TryGetValue(assetId, out bytes);
            Reads++;
        }

        if (bytes is null)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That photo isn't on the phone any more.");
        }

        const int chunk = 16 * 1024;
        for (var done = 0; done < bytes.Length; done += chunk)
        {
            ct.ThrowIfCancellationRequested();
            phone.EnsureConnected();
            var count = Math.Min(chunk, bytes.Length - done);
            await destination.WriteAsync(bytes.AsMemory(done, count), ct);
            progress?.Report(done + count);
            if (BytesPerSecond is { } rate)
            {
                await Task.Delay(TimeSpan.FromSeconds((double)count / rate), ct);
            }
        }
    }

    public Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");

    public Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct) =>
        throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
}
