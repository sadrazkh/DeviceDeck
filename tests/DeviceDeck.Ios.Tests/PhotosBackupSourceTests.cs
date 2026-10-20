using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests;

/// <summary>The iPhone library as a backup source: ZUUID keys, Hidden and Live Photos marked, missing sizes filled by stats.</summary>
public sealed class PhotosBackupSourceTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-backup-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly IosDeviceProvider _provider;

    public PhotosBackupSourceTests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
    }

    private async Task<IosDevice> AttachAsync(FakePhone phone)
    {
        _mux.Attach(1, phone);
        await _provider.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            Assert.True(DateTime.UtcNow < deadline, "the phone never became ready");
            await Task.Delay(20, Ct);
        }

        return _provider.Find(Udid)!;
    }

    private async Task<FakePhone> PhoneWithLibraryAsync()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
        var db = PhotosTests.CreateDatabase("ios17.sql", Path.Combine(_dir, "Photos.sqlite"));
        SqliteConnection.ClearAllPools();
        phone.Media.AddFile("/PhotoData/Photos.sqlite", await File.ReadAllBytesAsync(db, Ct));
        foreach (var f in PhotosTests.DcimFiles)
            phone.Media.AddFile("/" + f, System.Text.Encoding.UTF8.GetBytes(f + new string('.', 100)));
        return phone;
    }

    [Fact]
    public async Task Uuid_keys_hidden_and_live_photos_and_stats_only_for_what_is_missing()
    {
        var phone = await PhoneWithLibraryAsync();
        var device = await AttachAsync(phone);
        var source = Assert.IsAssignableFrom<IPhotoBackupSource>(device.Photos);

        var assets = await source.GetBackupAssetsAsync(Ct);
        PhotoBackupAsset Get(string name) => assets.Single(a => a.FileName == name);

        Assert.Equal("uuid:UUID-1", Get("IMG_0001.JPG").Key);
        Assert.Equal("DCIM/100APPLE/IMG_0001.JPG", Get("IMG_0001.JPG").SourcePath);
        Assert.Equal(2_500_000, Get("IMG_0001.JPG").Size);      // ZORIGINALFILESIZE, no stat
        var live = Get("IMG_0002.HEIC");
        Assert.Equal(PhotoKind.LivePhoto, live.Kind);
        Assert.Equal(new PhotoBackupMotion("DCIM/100APPLE/IMG_0002.MOV", "IMG_0002.MOV", 0), live.Motion);
        Assert.True(Get("IMG_0006.JPG").Hidden);
        Assert.DoesNotContain(assets, a => a.FileName == "IMG_0005.JPG"); // Recently Deleted
        var loose = Get("IMG_0007.JPG");                         // newer than the database copy
        Assert.Equal(("dcim:DCIM/100APPLE/IMG_0007.JPG", 0L), (loose.Key, loose.Size));
        Assert.All(assets.Where(a => a != loose && a != Get("IMG_0006.JPG")), a => Assert.False(a.Hidden));

        var statsBefore = phone.Media.Operations.Count(o => o == AfcOperation.GetFileInfo);
        var complete = await source.CompleteAsync([Get("IMG_0001.JPG"), live, loose], Ct);
        Assert.Equal(2, phone.Media.Operations.Count(o => o == AfcOperation.GetFileInfo) - statsBefore); // the .MOV and IMG_0007
        Assert.Equal(2_500_000, complete[0].Size);
        Assert.Equal("DCIM/100APPLE/IMG_0002.MOV".Length + 100, complete[1].Motion!.Size);
        Assert.Equal("DCIM/100APPLE/IMG_0007.JPG".Length + 100, complete[2].Size);
        Assert.NotNull(complete[2].TakenAt);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
