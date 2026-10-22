using System.Collections.Concurrent;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>An iPhone end to end: PhotosDelete follows what Windows' camera connection shows, and deletes never go over AFC.</summary>
public sealed class PhotoDeleteDeviceTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-delete-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly FakeWpd _wpd = new();
    private readonly ConcurrentQueue<DeviceChangedEventArgs> _events = new();
    private readonly IosDeviceProvider _provider;

    public PhotoDeleteDeviceTests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
            Wpd = _wpd,
            PhotoDeleteLogDirectory = Path.Combine(_dir, "logs"),
            PhotoDeleteRetryInterval = TimeSpan.FromMilliseconds(30),
        });
        _provider.DeviceChanged += (_, e) => _events.Enqueue(e);
    }

    /// <summary>A paired phone with three photos and a Live Photo in 100APPLE (no Photos.sqlite: the DCIM listing alone).</summary>
    private FakePhone Phone()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0001.HEIC", new byte[1001]);
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0002.HEIC", new byte[1002]);
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0003.HEIC", new byte[1003]);
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0003.MOV", new byte[503]);
        return phone;
    }

    /// <summary>The same files the way Windows shows them; a delete there removes the file on the phone (as Photos would).</summary>
    private FakeWpdDevice Camera(FakePhone phone, bool canDelete = true)
    {
        var camera = _wpd.AddIphone(Udid);
        camera.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 1001), ("100APPLE", "IMG_0002.HEIC", 1002), ("100APPLE", "IMG_0003.HEIC", 1003),
            ("100APPLE", "IMG_0003.MOV", 503)], canDelete);
        camera.Deleted = (_, path) => phone.Media.Remove("/" + path);
        return camera;
    }

    private async Task<IosDevice> AttachAsync(FakePhone phone)
    {
        _mux.Attach(1, phone);
        await _provider.StartAsync(Ct);
        await WaitUntil(() => _provider.Find(Udid)?.Summary.State == DeviceState.Ready, "Ready");
        return _provider.Find(Udid)!;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_ready_phone_windows_shows_as_deletable_gets_PhotosDelete_and_deletes_through_windows_only()
    {
        var phone = Phone();
        var camera = Camera(phone);
        var device = await AttachAsync(phone);
        await WaitUntil(() => device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete), "PhotosDelete");
        Assert.Contains(_events, e => e.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete));

        var page = await device.Photos.GetAssetsAsync(null, 0, 100, Ct);
        Assert.Equal(3, page.Total);
        var live = page.Items.Single(a => a.Id == "DCIM/100APPLE/IMG_0003.HEIC");
        var results = await device.DeleteConfirmedAsync([new("DCIM/100APPLE/IMG_0001.HEIC", 1001), new(live.Id, live.Size)], null, Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal(["IMG_0003.MOV"], results[1].Also);
        Assert.All(results, r => Assert.Equal(PhotoDeleteDestination.Permanent, r.WentTo));
        Assert.False(phone.Media.Exists("/DCIM/100APPLE/IMG_0001.HEIC") || phone.Media.Exists("/DCIM/100APPLE/IMG_0003.MOV"));
        Assert.Equal(["DCIM/100APPLE/IMG_0002.HEIC"], (await device.Photos.GetAssetsAsync(null, 0, 100, Ct)).Items.Select(a => a.Id));

        // Never over AFC: deleting DCIM files there would leave Photos.sqlite pointing at missing files.
        Assert.DoesNotContain(phone.Media.Operations, o => o is AfcOperation.RemovePath or AfcOperation.RemovePathAndContents or AfcOperation.RenamePath);
        var log = await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(_dir, "logs"), "photo-deletes-*.log").Single(), Ct);
        Assert.Contains("deleted DCIM/100APPLE/IMG_0001.HEIC (1,001 bytes)", log);
        Assert.Contains("deleted DCIM/100APPLE/IMG_0003.MOV (503 bytes), the Live Photo's video", log);
        Assert.Equal(3, camera.DeleteCalls.Single().Count); // IMG_0001, IMG_0003 and its video in one batch
    }

    [Fact]
    public async Task The_photo_library_delete_goes_through_windows_and_stops_at_a_refusal()
    {
        var phone = Phone();
        var camera = Camera(phone);
        camera.Answer = o => o.FileName == "IMG_0002.HEIC" ? Ios.Wpd.WpdErrors.AccessDenied : 0;
        var device = await AttachAsync(phone);
        await WaitUntil(() => device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete), "PhotosDelete");

        await device.Photos.DeleteAsync(["DCIM/100APPLE/IMG_0001.HEIC"], Ct);
        var refused = await Assert.ThrowsAsync<DeviceException>(() => device.Photos.DeleteAsync(["DCIM/100APPLE/IMG_0002.HEIC"], Ct));

        Assert.Equal(DeviceErrorCodes.PermissionDenied, refused.Code);
        Assert.Contains("0 of 1 deleted", refused.Message);
        Assert.False(phone.Media.Exists("/DCIM/100APPLE/IMG_0001.HEIC"));
        Assert.True(phone.Media.Exists("/DCIM/100APPLE/IMG_0002.HEIC"));
    }

    [Fact]
    public async Task Without_a_camera_in_windows_there_is_no_PhotosDelete_until_a_recheck_finds_one()
    {
        var phone = Phone();
        var device = await AttachAsync(phone);
        await WaitUntil(() => device.PhotoDeleter.Current.Reason == PhotoDeleteBlock.NotCamera, "the check");

        Assert.False(device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete));
        var refused = await Assert.ThrowsAsync<DeviceException>(() => device.DeleteConfirmedAsync([new("DCIM/100APPLE/IMG_0001.HEIC", 1001)], null, Ct));
        Assert.Equal(DeviceErrorCodes.NotSupported, refused.Code);
        Assert.Equal(DeviceErrorCodes.NotSupported,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Photos.DeleteAsync(["DCIM/100APPLE/IMG_0001.HEIC"], Ct))).Code);

        Camera(phone);
        var support = await device.GetDeleteSupportAsync(recheck: true, Ct);

        Assert.True(support.CanDelete);
        Assert.True(device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete));
    }

    [Fact]
    public async Task Read_only_photos_give_no_PhotosDelete_and_a_reason()
    {
        var phone = Phone();
        Camera(phone, canDelete: false);
        var device = await AttachAsync(phone);
        await WaitUntil(() => device.PhotoDeleter.Current.Reason != PhotoDeleteBlock.Checking, "the check");

        var support = await device.GetDeleteSupportAsync(recheck: false, Ct);
        Assert.Equal(PhotoDeleteBlock.ReadOnly, support.Reason);
        Assert.False(device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete));
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
}
