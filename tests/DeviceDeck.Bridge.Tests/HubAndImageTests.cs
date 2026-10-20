using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Hub;

namespace DeviceDeck.Bridge.Tests;

public sealed class HubTests
{
    /// <summary>A provider driven by hand: reports whatever summaries the test gives it.</summary>
    private sealed class ManualProvider(string id, bool failStart = false) : IDeviceProvider
    {
        private readonly List<IDevice> _devices = [];

        public string Id => id;
        public IReadOnlyList<IDevice> Devices => _devices;
        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

        public Task StartAsync(CancellationToken ct) => failStart ? throw new InvalidOperationException("usbmuxd is not running") : Task.CompletedTask;

        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(id, DriverState.Ready, "1", null));

        public void Raise(DeviceChange change, DeviceSummary summary) => DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(change, summary));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DeviceSummary Phone(DeviceMode mode) =>
        new("same-phone", "Phone", DevicePlatform.Ios, "iPhone18,1", null, "26.1", ConnectionKind.Usb, DeviceState.Ready, mode, DeviceCapability.Info);

    [Fact]
    public async Task The_full_mode_sighting_wins_and_the_phone_leaves_with_the_last_one()
    {
        var mtp = new ManualProvider("mtp");
        var usb = new ManualProvider("ios");
        await using var hub = new DeviceHub([mtp, usb]) { DriverPollInterval = TimeSpan.Zero };
        var seen = new List<(DeviceChange Change, DeviceMode Mode)>();
        hub.DeviceChanged += (_, e) => seen.Add((e.Change, e.Summary.Mode));
        await hub.StartAsync(CancellationToken.None);

        mtp.Raise(DeviceChange.Added, Phone(DeviceMode.Limited));
        usb.Raise(DeviceChange.Added, Phone(DeviceMode.Full));
        usb.Raise(DeviceChange.Changed, Phone(DeviceMode.Full)); // no change: no event
        Assert.Equal(DeviceMode.Full, hub.ListDevices().Single().Mode);

        usb.Raise(DeviceChange.Removed, Phone(DeviceMode.Full));
        Assert.Equal(DeviceMode.Limited, hub.ListDevices().Single().Mode);
        mtp.Raise(DeviceChange.Removed, Phone(DeviceMode.Limited));
        Assert.Empty(hub.ListDevices());

        Assert.Equal(
            [(DeviceChange.Added, DeviceMode.Limited), (DeviceChange.Changed, DeviceMode.Full), (DeviceChange.Changed, DeviceMode.Limited), (DeviceChange.Removed, DeviceMode.Limited)],
            seen);
    }

    [Fact]
    public async Task A_provider_that_fails_to_start_shows_in_driver_status_and_the_rest_carry_on()
    {
        var broken = new ManualProvider("ios", failStart: true);
        var fake = new FakeDeviceProvider(FakeOptions.ForTests(TestPhones.Android()));
        await using var hub = new DeviceHub([broken, fake]) { DriverPollInterval = TimeSpan.Zero };
        await hub.StartAsync(CancellationToken.None);

        Assert.Single(hub.ListDevices());
        var drivers = await hub.GetDriverStatusAsync(CancellationToken.None);
        Assert.Equal(DriverState.Error, drivers.Single(d => d.ProviderId == "ios").State);
        Assert.Equal("usbmuxd is not running", drivers.Single(d => d.ProviderId == "ios").Detail);
        Assert.Equal(DriverState.Ready, drivers.Single(d => d.ProviderId == "fake").State);
    }

    [Fact]
    public async Task Unusable_states_map_to_their_error_codes()
    {
        var fake = new FakeDeviceProvider(FakeOptions.ForTests(TestPhones.Iphone()));
        await using var hub = new DeviceHub([fake]) { DriverPollInterval = TimeSpan.Zero };
        await hub.StartAsync(CancellationToken.None);

        Assert.Equal(DeviceErrorCodes.NotFound, Assert.Throws<DeviceException>(() => hub.GetUsableDevice("nobody")).Code);
        fake.SetState(TestPhones.IphoneId, DeviceState.AwaitingTrust);
        Assert.Equal(DeviceErrorCodes.TrustRequired, Assert.Throws<DeviceException>(() => hub.GetUsableDevice(TestPhones.IphoneId)).Code);
        fake.SetState(TestPhones.IphoneId, DeviceState.Locked);
        Assert.NotNull(hub.GetUsableDevice(TestPhones.IphoneId));
    }
}

public sealed class ImageHandlerTests
{
    private static DeviceImageHandler Handler(TestHost host) =>
        new(host.Hub, new ThumbnailCache(host.Paths.ThumbnailCache), host.Services.Screenshots, null, Path.Combine(host.Root, "viewer"));

    private static async Task<byte[]> ReadAsync(ImageResponse response)
    {
        using var memory = new MemoryStream();
        await response.Body!.CopyToAsync(memory);
        await response.Body.DisposeAsync();
        return memory.ToArray();
    }

    [Fact]
    public async Task Thumbnails_are_served_and_cached_on_disk()
    {
        await using var host = await TestHost.StartAsync();
        var handler = Handler(host);
        var url = new Uri(DeviceImageHandler.ThumbUrl(TestPhones.IphoneId, $"{TestPhones.IphoneId}-a1", 200));

        var first = await handler.HandleAsync(url, CancellationToken.None);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal("image/png", first.ContentType);
        Assert.Contains("Content-Type: image/png", first.Headers);
        Assert.NotEmpty(await ReadAsync(first));
        Assert.True(File.Exists(new ThumbnailCache(host.Paths.ThumbnailCache).PathFor(TestPhones.IphoneId, $"{TestPhones.IphoneId}-a1", 256)));

        // Unplugged: the cached thumbnail still shows, an uncached one is a 404.
        host.Fake.Disconnect(TestPhones.IphoneId);
        Assert.Equal(200, (await handler.HandleAsync(url, CancellationToken.None)).StatusCode);
        var uncached = await handler.HandleAsync(new Uri(DeviceImageHandler.ThumbUrl(TestPhones.IphoneId, $"{TestPhones.IphoneId}-a2")), CancellationToken.None);
        Assert.Equal(404, uncached.StatusCode);
    }

    [Theory]
    [InlineData("https://device.local/thumb/test-iphone/nope?s=256")]
    [InlineData("https://device.local/icon/test-iphone/no.such.app")]
    [InlineData("https://device.local/shot/test-iphone/nope")]
    [InlineData("https://device.local/photo/nobody/x")]
    [InlineData("https://device.local/other/test-iphone/x")]
    [InlineData("https://device.local/thumb/test-iphone")]
    public async Task Anything_missing_is_a_404(string url)
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(404, (await Handler(host).HandleAsync(new Uri(url), CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Originals_icons_and_screenshots_are_served()
    {
        await using var host = await TestHost.StartAsync();
        var handler = Handler(host);

        var photo = await handler.HandleAsync(new Uri($"https://device.local/photo/{TestPhones.IphoneId}/{TestPhones.IphoneId}-a0"), CancellationToken.None);
        Assert.Equal(200, photo.StatusCode);
        Assert.Equal("image/png", photo.ContentType);
        Assert.Equal(200_000, (await ReadAsync(photo)).Length);

        var icon = await handler.HandleAsync(new Uri($"https://device.local/icon/{TestPhones.IphoneId}/org.videolan.vlc-ios"), CancellationToken.None);
        Assert.Equal(200, icon.StatusCode);
        Assert.Equal("image/png", icon.ContentType);

        var shot = (await host.ResultAsync("tools.screenshot", new { deviceId = TestPhones.IphoneId }))!["url"]!.GetValue<string>();
        var served = await handler.HandleAsync(new Uri(shot), CancellationToken.None);
        Assert.Equal(200, served.StatusCode);
    }

    [Fact]
    public async Task Ids_in_paths_are_uri_decoded()
    {
        var odd = TestPhones.Iphone(id: "odd id/1");
        await using var host = await TestHost.StartAsync(odd);
        var url = DeviceImageHandler.ThumbUrl("odd id/1", "odd id/1-a0");
        Assert.Contains("odd%20id%2F1", url);
        Assert.Equal(200, (await Handler(host).HandleAsync(new Uri(url), CancellationToken.None)).StatusCode);
    }

    [Theory]
    [InlineData("", 256)]
    [InlineData("?s=100", 128)]
    [InlineData("?s=256", 256)]
    [InlineData("?s=300", 512)]
    [InlineData("?s=4000", 1024)]
    [InlineData("?s=abc", 256)]
    public void Thumbnail_sizes_round_up_to_cache_buckets(string query, int expected) =>
        Assert.Equal(expected, DeviceImageHandler.ParseSize(query));
}

public sealed class SampleDataTests
{
    [Fact]
    public void The_iphone_matches_the_plan()
    {
        var iphone = FakeSampleData.Iphone();
        Assert.Equal("Nima's iPhone", iphone.Summary.Name);
        Assert.Equal(12_481, iphone.Assets.Count(a => a.Kind != PhotoKind.Video));
        Assert.Equal(732, iphone.Assets.Count(a => a.Kind == PhotoKind.Video));
        Assert.Equal(18, iphone.Albums.Count);
        Assert.Equal(iphone.Assets.OrderByDescending(a => a.TakenAt).Select(a => a.Id), iphone.Assets.Select(a => a.Id));
        var volume = iphone.Storage.Volumes.Single();
        Assert.Equal(volume.TotalBytes, volume.FreeBytes + volume.Categories.Sum(c => c.Bytes));
        Assert.Equal(["VLC", "Documents", "Infuse"], iphone.Apps.Where(a => a.Info.FileSharing).Select(a => a.Info.Name));
    }

    [Fact]
    public void The_android_phone_matches_the_plan()
    {
        var android = FakeSampleData.Android();
        Assert.Equal(21_906, android.Assets.Count(a => a.Kind != PhotoKind.Video));
        Assert.Equal(1_140, android.Assets.Count(a => a.Kind == PhotoKind.Video));
        Assert.Equal(34, android.Albums.Count(a => a.Album.Kind == AlbumKind.Folder));
        Assert.Equal(android.Assets.Count, android.Albums.Where(a => a.Album.Kind == AlbumKind.Folder).Sum(a => a.AssetIds.Count));
        var volume = android.Storage.Volumes.Single();
        Assert.Equal(volume.TotalBytes, volume.FreeBytes + volume.Categories.Sum(c => c.Bytes));
        Assert.Equal(ConnectionKind.Wifi, android.Summary.Connection);
        Assert.Equal(2, android.Apps.Count(a => a.Info.Source == AppSource.System));
    }

    [Fact]
    public async Task The_default_fake_brings_both_phones_and_reports_indexing()
    {
        await using var fake = new FakeDeviceProvider(new FakeOptions { ArrivalDelay = TimeSpan.FromMilliseconds(20), IndexDuration = TimeSpan.Zero });
        var ready = new List<string>();
        fake.DeviceChanged += (_, e) =>
        {
            if (e.Summary.State == DeviceState.Ready)
            {
                lock (ready)
                {
                    ready.Add(e.Summary.Id);
                }
            }
        };
        await fake.StartAsync(CancellationToken.None);
        Assert.All(fake.Devices, d => Assert.Equal(DeviceState.Connecting, d.Summary.State));
        await Wait.UntilAsync(() => { lock (ready) { return ready.Count == 2; } }, "both phones");
        Assert.Equal([FakeSampleData.IphoneId, FakeSampleData.AndroidId], fake.Devices.Select(d => d.Summary.Id).Order(StringComparer.Ordinal));

        var page = await fake.Devices[0].Photos.GetAssetsAsync(null, 0, 60, CancellationToken.None);
        Assert.Equal(60, page.Items.Count);
        var thumb = await fake.Devices[0].Photos.GetThumbnailAsync(page.Items[0].Id, 256, CancellationToken.None);
        Assert.Equal("image/png", Core.Imaging.ImageFormats.Sniff(thumb));
    }
}
