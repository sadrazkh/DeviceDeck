using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>
/// Fake phones with known clean-up findings. A fake asset's content is its name, size and date, so assets that agree
/// on all three are exact copies: on each phone the copies are known, and so are the big app files.
/// </summary>
internal static class CleanupPhones
{
    public const string AndroidId = "clean-android";
    public const string IphoneId = "clean-iphone";

    private static readonly DateTimeOffset T = new(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(3.5));

    private static PhotoAsset Asset(string id, string name, PhotoKind kind, long size, int hoursAgo, bool cloudOnly = false) =>
        new(id, name, kind, T.AddHours(-hoursAgo), size, 400, 300, kind == PhotoKind.Video ? 12 : null, false, cloudOnly, null);

    public static FakeDeviceSpec Android() => new()
    {
        Summary = new DeviceSummary(AndroidId, "Clean Android", DevicePlatform.Android, "SM-T1", "Galaxy Test", "16", ConnectionKind.Usb, DeviceState.Ready, DeviceMode.Full,
            DeviceCapability.Info | DeviceCapability.Storage | DeviceCapability.Battery | DeviceCapability.PhotosRead | DeviceCapability.PhotosDelete |
            DeviceCapability.Files | DeviceCapability.AppsList | DeviceCapability.AppsUninstall | DeviceCapability.AppsExport),
        Storage = new StorageInfo([new StorageVolume("internal", "Internal storage", 128_000_000_000, 9_000_000_000, [])]),
        Battery = new BatteryInfo(64, true, 91, 412, 31.5, 5000, 4550),
        Details = new DeviceDetails([]),
        Assets =
        [
            Asset("1", "VID_big.mp4", PhotoKind.Video, 5_000_000, 1),
            Asset("2", "VID_dup.mp4", PhotoKind.Video, 2_000_000, 2),
            Asset("3", "VID_dup.mp4", PhotoKind.Video, 2_000_000, 2), // copy of 2
            Asset("4", "VID_small.mp4", PhotoKind.Video, 1_000_000, 3),
            Asset("5", "IMG_1.jpg", PhotoKind.Photo, 300_000, 4),
            Asset("6", "IMG_1.jpg", PhotoKind.Photo, 300_000, 4), // copy of 5
            Asset("7", "IMG_1.jpg", PhotoKind.Photo, 300_000, 4), // copy of 5
            Asset("8", "IMG_2.jpg", PhotoKind.Photo, 300_000, 5), // same size as 5, other content
            Asset("9", "Screenshot_1.png", PhotoKind.Screenshot, 800, 6),
            Asset("10", "Screenshot_1.png", PhotoKind.Screenshot, 800, 6), // copy of 9, smaller than the prefix
            Asset("11", "IMG_3.jpg", PhotoKind.Photo, 450_000, 7),
        ],
        Albums = [new FakeAlbum(new PhotoAlbum("all", "All", AlbumKind.All, 11), [])],
        Roots =
        [
            new FakeRoot(new FileRoot("internal", "Internal storage", FileRootKind.InternalStorage), Dir("",
                Dir("Android",
                    Dir("data", readOnly: true, denied: true),
                    Dir("media", Dir("com.whatsapp", Dir("WhatsApp", Dir("Media", File("big-video.mp4", 400_000, T), File("note.txt", 2_000, T)))))),
                Dir("DCIM", Dir("Camera", File("20260901_120000.jpg", 900_000, T))),
                Dir("Documents", File("report.pdf", 150_000, T)),
                Dir("Download", File("setup.apk", 250_000, T), File("small.txt", 500, T)))),
        ],
        Apps =
        [
            new FakeApp(new AppInfo("com.whatsapp", "WhatsApp", "2.26", 100_000, 4_000_000, AppSource.Store, false, T), (37, 211, 102)),
            new FakeApp(new AppInfo("com.example.game", "Big Game", "1.0", 3_000_000, 1_000, AppSource.Sideloaded, false, T), (200, 30, 30)),
            new FakeApp(new AppInfo("com.android.chrome", "Chrome", "141", 9_000_000, 9_000_000, AppSource.System, false, null), (219, 68, 55)),
        ],
    };

    public static FakeDeviceSpec Iphone() => new()
    {
        Summary = new DeviceSummary(IphoneId, "Clean iPhone", DevicePlatform.Ios, "iPhone17,3", "iPhone 16", "27.0", ConnectionKind.Usb, DeviceState.Ready, DeviceMode.Full,
            DeviceCapability.Info | DeviceCapability.Storage | DeviceCapability.Battery | DeviceCapability.PhotosRead | DeviceCapability.Files |
            DeviceCapability.AppDocuments | DeviceCapability.AppsList | DeviceCapability.AppsUninstall),
        Storage = new StorageInfo([new StorageVolume("internal", "iPhone storage", 128_000_000_000, 3_800_000_000, [])]),
        Battery = new BatteryInfo(80, false, 88, 530, 29, 3561, 3133),
        Details = new DeviceDetails([]),
        Assets =
        [
            Asset("DCIM/100APPLE/IMG_0001.MOV", "IMG_0001.MOV", PhotoKind.Video, 6_000_000, 1),
            Asset("DCIM/100APPLE/IMG_0002.HEIC", "IMG_0002.HEIC", PhotoKind.Photo, 2_000_000, 2),
            Asset("DCIM/100APPLE/IMG_0003.HEIC", "IMG_0002.HEIC", PhotoKind.Photo, 2_000_000, 2), // a copy, as an import twice leaves
            Asset("DCIM/100APPLE/IMG_0004.HEIC", "IMG_0004.HEIC", PhotoKind.Photo, 9_000_000, 3, cloudOnly: true),
        ],
        Albums = [new FakeAlbum(new PhotoAlbum("all", "All", AlbumKind.All, 4), [])],
        Roots =
        [
            new FakeRoot(new FileRoot("app:org.videolan.vlc-ios", "VLC", FileRootKind.AppDocuments, "org.videolan.vlc-ios"), Dir("",
                File("Movie.mkv", 300_000, T),
                Dir("Subtitles", File("Movie.srt", 2_000, T)))),
            new FakeRoot(new FileRoot("app:com.readdle.Documents", "Documents", FileRootKind.AppDocuments, "com.readdle.Documents"), Dir("",
                Dir("Downloads", File("archive.zip", 500_000, T)))),
        ],
        Apps =
        [
            new FakeApp(new AppInfo("org.videolan.vlc-ios", "VLC", "3.6", 80_000, 700_000, AppSource.Store, true, T), (255, 136, 0)),
            new FakeApp(new AppInfo("com.readdle.Documents", "Documents", "8.0", 120_000, 600_000, AppSource.Store, true, T), (60, 120, 220)),
        ],
    };
}
