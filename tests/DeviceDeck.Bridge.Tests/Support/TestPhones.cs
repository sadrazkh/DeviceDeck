using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>Small fake phones for tests: a few photos of known size, one app-documents root, two apps.</summary>
internal static class TestPhones
{
    public const string IphoneId = "test-iphone";
    public const string AndroidId = "test-android";

    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(3.5));

    public static FakeDeviceSpec Iphone(int photos = 4, long photoSize = 200_000, string id = IphoneId, DeviceMode mode = DeviceMode.Full) =>
        Spec(id, "Test iPhone", DevicePlatform.Ios, photos, photoSize, mode);

    public static FakeDeviceSpec Android(int photos = 4, long photoSize = 200_000, string id = AndroidId) =>
        Spec(id, "Test Android", DevicePlatform.Android, photos, photoSize, DeviceMode.Full);

    private static FakeDeviceSpec Spec(string id, string name, DevicePlatform platform, int photos, long photoSize, DeviceMode mode)
    {
        var assets = Enumerable.Range(0, photos).Select(i => new PhotoAsset(
            $"{id}-a{i}",
            platform == DevicePlatform.Ios ? $"IMG_{100 + i:D4}.HEIC" : $"2026100{i % 9 + 1}_120000.jpg",
            PhotoKind.Photo,
            Base.AddMonths(-i),
            photoSize,
            400,
            300,
            null,
            IsFavorite: i == 0,
            InCloudOnly: false,
            LivePairId: null)).ToList();

        var docs = new FakeRoot(new FileRoot("app:org.videolan.vlc-ios", "VLC", FileRootKind.AppDocuments, "org.videolan.vlc-ios"), Dir("",
            File("Movie.mkv", 300_000, Base),
            Dir("Subtitles", File("Movie.srt", 2_000, Base)),
            Dir("Locked", readOnly: true, denied: false)));

        return new FakeDeviceSpec
        {
            Summary = new DeviceSummary(id, name, platform, "Model1,1", name, "26.1", ConnectionKind.Usb, DeviceState.Ready, mode,
                DeviceCapability.Info | DeviceCapability.Storage | DeviceCapability.Battery | DeviceCapability.PhotosRead |
                DeviceCapability.PhotosDelete | DeviceCapability.Files | DeviceCapability.AppDocuments |
                DeviceCapability.AppsList | DeviceCapability.AppsInstall | DeviceCapability.AppsUninstall |
                DeviceCapability.Screenshot | DeviceCapability.Reboot | DeviceCapability.Shutdown |
                (platform == DevicePlatform.Android ? DeviceCapability.PhotosImport | DeviceCapability.AppsExport : DeviceCapability.None)),
            Storage = new StorageInfo([new StorageVolume("internal", "Storage", 64_000_000_000, 32_000_000_000, [new StorageCategory("photos", 1_000_000)])]),
            Battery = new BatteryInfo(80, false, 95, 100, 30, 4000, 3800),
            Details = new DeviceDetails([new DetailRow("serial", "Serial number", "SERIAL1", Sensitive: true)]),
            Assets = assets,
            Albums =
            [
                new FakeAlbum(new PhotoAlbum("all", "All", AlbumKind.All, assets.Count), []),
                new FakeAlbum(new PhotoAlbum("favorites", "Favourites", AlbumKind.Favorites, 1), [assets[0].Id]),
            ],
            Roots = [docs],
            Apps =
            [
                new FakeApp(new AppInfo("org.videolan.vlc-ios", "VLC", "3.6", 100, 200, AppSource.Store, true, Base), (255, 136, 0)),
                new FakeApp(new AppInfo("com.android.chrome", "Chrome", "141", 100, 200, AppSource.System, false, null), (219, 68, 55)),
            ],
            Screen = (60, 120),
        };
    }
}
