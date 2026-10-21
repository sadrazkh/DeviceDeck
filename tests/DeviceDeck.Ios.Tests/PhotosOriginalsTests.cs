using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

/// <summary>Originals off an iPhone for the backup: a few pooled connections for many files, reads ahead, and PC errors told apart.</summary>
public sealed class PhotosOriginalsTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private const int Chunk = AfcClient.ReadChunk;
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-originals-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly FakePhone _phone = new(Udid) { ExpectedHostId = TestPairing.HostId };
    private readonly IosDeviceProvider _provider;

    public PhotosOriginalsTests()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
        for (var i = 0; i < 12; i++)
            _phone.Media.AddGeneratedFile($"/DCIM/100APPLE/IMG_{i:D4}.HEIC", 200_000 + i, (ulong)i);
        _phone.Media.AddGeneratedFile("/DCIM/100APPLE/IMG_0100.MOV", Chunk * 5L / 2, 100);
        _phone.Media.AddGeneratedFile("/DCIM/100APPLE/IMG_0101.MOV", Chunk * 5L / 2, 101);
    }

    private async Task<IosPhotoLibrary> AttachAsync()
    {
        _mux.Attach(1, _phone);
        await _provider.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            Assert.True(DateTime.UtcNow < deadline, "the phone never became ready");
            await Task.Delay(20, Ct);
        }

        return (IosPhotoLibrary)_provider.Find(Udid)!.Photos;
    }

    private static byte[] Expected(long length, ulong seed)
    {
        var bytes = new byte[length];
        FakeAfcServer.Generate(seed, 0, bytes);
        return bytes;
    }

    [Fact]
    public async Task Many_originals_share_a_few_connections()
    {
        var photos = await AttachAsync();
        var before = _mux.Connections;

        for (var i = 0; i < 6; i++)
        {
            using var copy = new MemoryStream();
            await photos.ReadOriginalAsync($"DCIM/100APPLE/IMG_{i:D4}.HEIC", copy, null, Ct);
            Assert.Equal(Expected(200_000 + i, (ulong)i), copy.ToArray());
        }

        // One AFC connection (a lockdown session plus the service), not one per file.
        Assert.Equal(2, _mux.Connections - before);
        Assert.Equal(1, photos.OriginalConnectionsOpened);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var copy = new MemoryStream();
            await photos.ReadOriginalAsync($"DCIM/100APPLE/IMG_{i:D4}.HEIC", copy, null, Ct);
            Assert.Equal(200_000 + i, copy.Length);
        }));
        Assert.InRange(photos.OriginalConnectionsOpened, 2, 4);
    }

    [Fact]
    public async Task A_phone_that_drops_the_first_read_ahead_is_read_one_chunk_at_a_time_from_then_on()
    {
        var photos = await AttachAsync();
        _phone.Media.HangUpWhileReading = "/DCIM/100APPLE/IMG_0100.MOV";

        var dropped = await Assert.ThrowsAsync<DeviceException>(() => photos.ReadOriginalAsync("DCIM/100APPLE/IMG_0100.MOV", new MemoryStream(), null, Ct));
        Assert.Equal(DeviceErrorCodes.Disconnected, dropped.Code); // the queue waits a moment and tries the photo again

        var reads = _phone.Media.Operations.Count(o => o == AfcOperation.FileRead);
        using var copy = new MemoryStream();
        await photos.ReadOriginalAsync("DCIM/100APPLE/IMG_0101.MOV", copy, null, Ct);
        Assert.Equal(Expected(Chunk * 5L / 2, 101), copy.ToArray());
        Assert.Equal(3 + 1, _phone.Media.Operations.Count(o => o == AfcOperation.FileRead) - reads); // three chunks and the end, nothing ahead
    }

    [Fact]
    public async Task Reads_ahead_once_they_worked_stay_on()
    {
        var photos = await AttachAsync();
        await photos.ReadOriginalAsync("DCIM/100APPLE/IMG_0100.MOV", new MemoryStream(), null, Ct);

        var reads = _phone.Media.Operations.Count(o => o == AfcOperation.FileRead);
        await photos.ReadOriginalAsync("DCIM/100APPLE/IMG_0101.MOV", new MemoryStream(), null, Ct);
        Assert.Equal(2 + 3, _phone.Media.Operations.Count(o => o == AfcOperation.FileRead) - reads);
    }

    [Fact]
    public async Task A_full_pc_disk_is_the_pcs_error_not_a_dropped_phone()
    {
        var photos = await AttachAsync();
        var full = await Assert.ThrowsAsync<IOException>(() => photos.ReadOriginalAsync("DCIM/100APPLE/IMG_0100.MOV", new FullDisk(), null, Ct));
        Assert.Equal(0x70, full.HResult & 0xFFFF);

        // The connection it broke is replaced; the next photo reads fine.
        using var copy = new MemoryStream();
        await photos.ReadOriginalAsync("DCIM/100APPLE/IMG_0001.HEIC", copy, null, Ct);
        Assert.Equal(200_001, copy.Length);
    }

    [Fact]
    public async Task Ids_outside_dcim_are_refused()
    {
        var photos = await AttachAsync();
        var refused = await Assert.ThrowsAsync<DeviceException>(() => photos.ReadOriginalAsync("PhotoData/Photos.sqlite", new MemoryStream(), null, Ct));
        Assert.Equal(DeviceErrorCodes.NotFound, refused.Code);
        await Assert.ThrowsAsync<DeviceException>(() => photos.ReadOriginalAsync("DCIM/../PhotoData/Photos.sqlite", new MemoryStream(), null, Ct));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A destination whose disk is full (ERROR_DISK_FULL).</summary>
    private sealed class FullDisk : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070)));
    }
}
