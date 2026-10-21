using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests;

/// <summary>
/// Edited photos on an iPhone: Photos.sqlite's ZHASADJUSTMENTS / ZADJUSTMENTTIMESTAMP say which and when, and the rendered
/// version is PhotoData/Mutations/&lt;DCIM path&gt;/Adjustments/FullSizeRender.*. Only what is there is reported.
/// </summary>
public sealed class PhotosEditsTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-edits-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly IosDeviceProvider _provider;

    public PhotosEditsTests()
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

    /// <summary>
    /// The iOS 17 fixture, with IMG_0001 (a photo) and IMG_0003 (a video) edited when <paramref name="withColumns"/>:
    /// in ZHASADJUSTMENTS, or, as on iOS 27 (no ZHASADJUSTMENTS), in ZADJUSTMENTSSTATE (2 and 3 seen on a real iPhone).
    /// </summary>
    private async Task<FakePhone> PhoneAsync(bool withColumns, bool ios27 = false)
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
        var path = PhotosTests.CreateDatabase("ios17.sql", Path.Combine(_dir, "Photos.sqlite"));
        if (withColumns)
        {
            using var db = new SqliteConnection($"Data Source={path};Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = ios27
                ? """
                  ALTER TABLE ZASSET ADD COLUMN ZADJUSTMENTSSTATE INTEGER;
                  ALTER TABLE ZASSET ADD COLUMN ZADJUSTMENTTIMESTAMP TIMESTAMP;
                  UPDATE ZASSET SET ZADJUSTMENTSSTATE = 0;
                  UPDATE ZASSET SET ZADJUSTMENTSSTATE = 3, ZADJUSTMENTTIMESTAMP = 780000000 WHERE Z_PK = 1;
                  UPDATE ZASSET SET ZADJUSTMENTSSTATE = 2 WHERE Z_PK = 3;
                  UPDATE ZASSET SET ZADJUSTMENTSSTATE = 3, ZADJUSTMENTTIMESTAMP = 780000500 WHERE Z_PK = 2;
                  """
                : """
                  ALTER TABLE ZASSET ADD COLUMN ZHASADJUSTMENTS INTEGER;
                  ALTER TABLE ZASSET ADD COLUMN ZADJUSTMENTTIMESTAMP TIMESTAMP;
                  UPDATE ZASSET SET ZHASADJUSTMENTS = 0;
                  UPDATE ZASSET SET ZHASADJUSTMENTS = 1, ZADJUSTMENTTIMESTAMP = 780000000 WHERE Z_PK = 1;
                  UPDATE ZASSET SET ZHASADJUSTMENTS = 1 WHERE Z_PK = 3;
                  UPDATE ZASSET SET ZHASADJUSTMENTS = 1, ZADJUSTMENTTIMESTAMP = 780000500 WHERE Z_PK = 2;
                  """;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        phone.Media.AddFile("/PhotoData/Photos.sqlite", await File.ReadAllBytesAsync(path, Ct));
        foreach (var f in PhotosTests.DcimFiles)
            phone.Media.AddFile("/" + f, System.Text.Encoding.UTF8.GetBytes(f + new string('.', 100)));
        // IMG_0001's edit was rendered; so was the video IMG_0003's (its Adjustments also holds the edit's recipe).
        // IMG_0002 says it was edited, but the phone has no rendered version where Photos keeps them.
        phone.Media.AddFile("/PhotoData/Mutations/DCIM/100APPLE/IMG_0001/Adjustments/Adjustments.plist", [1, 2, 3]);
        phone.Media.AddFile("/PhotoData/Mutations/DCIM/100APPLE/IMG_0001/Adjustments/FullSizeRender.heic", new byte[777]);
        phone.Media.AddFile("/PhotoData/Mutations/DCIM/100APPLE/IMG_0003/Adjustments/FullSizeRender.mov", new byte[5000]);
        phone.Media.AddFile("/PhotoData/Mutations/DCIM/100APPLE/IMG_0003/Adjustments/FullSizeRender.jpg", new byte[10]);
        return phone;
    }

    private async Task<IosPhotoLibrary> AttachAsync(FakePhone phone)
    {
        _mux.Attach(1, phone);
        await _provider.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            Assert.True(DateTime.UtcNow < deadline, "the phone never became ready");
            await Task.Delay(20, Ct);
        }

        return (IosPhotoLibrary)_provider.Find(Udid)!.Photos;
    }

    [Fact]
    public async Task On_ios_27_edits_come_from_ZADJUSTMENTSSTATE()
    {
        var photos = await AttachAsync(await PhoneAsync(withColumns: true, ios27: true));
        var assets = await photos.GetBackupAssetsAsync(Ct);
        PhotoBackupAsset Get(string name) => assets.Single(a => a.FileName == name);

        var adjustedAt = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(780000000).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new PhotoBackupEdit(adjustedAt), Get("IMG_0001.JPG").Edited);
        Assert.Equal(new PhotoBackupEdit("edited"), Get("IMG_0003.MOV").Edited);
        Assert.Null(Get("IMG_0004.PNG").Edited); // state 0: not edited
    }

    [Fact]
    public async Task Edits_come_from_photos_sqlite_and_their_rendered_versions_from_mutations()
    {
        var photos = await AttachAsync(await PhoneAsync(withColumns: true));
        var assets = await photos.GetBackupAssetsAsync(Ct);
        PhotoBackupAsset Get(string name) => assets.Single(a => a.FileName == name);

        var adjustedAt = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(780000000).UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new PhotoBackupEdit(adjustedAt), Get("IMG_0001.JPG").Edited);
        Assert.Equal(new PhotoBackupEdit("edited"), Get("IMG_0003.MOV").Edited); // no adjustment time: a stamp all the same
        Assert.Null(Get("IMG_0004.PNG").Edited);
        Assert.Null(Get("IMG_0007.JPG").Edited); // newer than the database copy: nothing to say yet

        var complete = await photos.CompleteAsync([Get("IMG_0001.JPG"), Get("IMG_0002.HEIC"), Get("IMG_0003.MOV")], Ct);
        Assert.Equal(new PhotoBackupEdit(adjustedAt, "PhotoData/Mutations/DCIM/100APPLE/IMG_0001/Adjustments/FullSizeRender.heic", "FullSizeRender.heic", 777),
            complete[0].Edited);
        Assert.Null(complete[1].Edited!.AssetId); // edited, but no rendered version: not found, and not guessed
        Assert.Equal(("PhotoData/Mutations/DCIM/100APPLE/IMG_0003/Adjustments/FullSizeRender.mov", 5000L),
            (complete[2].Edited!.AssetId, complete[2].Edited!.Size)); // a video's edit is the movie

        // The rendered version reads like an original; nothing outside DCIM and Mutations/DCIM does.
        using var copy = new MemoryStream();
        await photos.ReadOriginalAsync(complete[0].Edited!.AssetId!, copy, null, Ct);
        Assert.Equal(777, copy.Length);
        await Assert.ThrowsAsync<DeviceException>(() => photos.ReadOriginalAsync("PhotoData/Mutations/../Photos.sqlite", new MemoryStream(), null, Ct));
        await Assert.ThrowsAsync<DeviceException>(() => photos.ReadOriginalAsync("PhotoData/Photos.sqlite", new MemoryStream(), null, Ct));
    }

    [Fact]
    public async Task A_database_without_the_columns_reports_no_edits()
    {
        var photos = await AttachAsync(await PhoneAsync(withColumns: false));
        Assert.All(await photos.GetBackupAssetsAsync(Ct), a => Assert.Null(a.Edited));
    }

    [Fact]
    public void The_adjustments_folder_mirrors_the_dcim_path() =>
        Assert.Equal("PhotoData/Mutations/DCIM/105APPLE/IMG_5123/Adjustments", IosPhotoLibrary.AdjustmentsFolder("DCIM/105APPLE/IMG_5123.HEIC"));

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
