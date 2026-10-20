using DeviceDeck.Core.Abstractions;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Core.Fake;

/// <summary>
/// The plan's sample data (design brief, "Sample data"): Nima's iPhone 17 Pro and a Galaxy S25 Ultra.
/// All names are made up. Generated deterministically, so every run shows the same library.
/// </summary>
public static class FakeSampleData
{
    public const string IphoneId = "00008150-001A2D3C0E80801E";
    public const string AndroidId = "R5CY21ABCDE";

    private const long GB = 1_000_000_000L;
    private const long MB = 1_000_000L;
    private static readonly TimeSpan Tehran = TimeSpan.FromHours(3.5);
    private static readonly DateTimeOffset Newest = new(2026, 10, 19, 18, 40, 0, Tehran);

    public static IReadOnlyList<FakeDeviceSpec> Default() => [Iphone(), Android()];

    public static FakeDeviceSpec Iphone()
    {
        var summary = new DeviceSummary(
            IphoneId,
            "Nima's iPhone",
            DevicePlatform.Ios,
            "iPhone18,1",
            "iPhone 17 Pro",
            "26.1",
            ConnectionKind.Usb,
            DeviceState.Ready,
            DeviceMode.Full,
            DeviceCapability.Info | DeviceCapability.Storage | DeviceCapability.Battery |
            DeviceCapability.PhotosRead | DeviceCapability.PhotosDelete |
            DeviceCapability.Files | DeviceCapability.AppDocuments |
            DeviceCapability.AppsList | DeviceCapability.AppsInstall | DeviceCapability.AppsUninstall |
            DeviceCapability.Backup | DeviceCapability.Restore | DeviceCapability.Contacts | DeviceCapability.Messages |
            DeviceCapability.Screenshot | DeviceCapability.Syslog | DeviceCapability.CrashReports |
            DeviceCapability.Reboot | DeviceCapability.Shutdown,
            "#F26B2A"); // Cosmic Orange

        var storage = new StorageInfo([
            new StorageVolume("internal", "iPhone storage", 256 * GB, (long)(101.0 * GB), [
                new("photos", (long)(61.8 * GB)),
                new("videos", (long)(24.0 * GB)),
                new("apps", (long)(38.4 * GB)),
                new("media", (long)(6.2 * GB)),
                new("system", (long)(14.9 * GB)),
                new("other", (long)(9.7 * GB)),
            ]),
        ]);

        var battery = new BatteryInfo(78, true, 87, 412, 31.0, 4252, 3699);

        var details = new DeviceDetails([
            new("name", "Name", "Nima's iPhone"),
            new("model", "Model", "iPhone 17 Pro (iPhone18,1)"),
            new("modelNumber", "Model number", "MG8H4LL/A"),
            new("os", "iOS", "26.1 (23B85)"),
            new("colour", "Colour", "Cosmic Orange"),
            new("capacity", "Capacity", "256 GB"),
            new("serial", "Serial number", "F2LZK9X1Q7", Sensitive: true),
            new("imei", "IMEI", "35 302611 482913 6", Sensitive: true),
            new("udid", "UDID", IphoneId, Sensitive: true),
            new("wifi", "Wi-Fi address", "a4:c3:f0:12:7e:9b"),
            new("bluetooth", "Bluetooth", "a4:c3:f0:12:7e:9c"),
            new("carrier", "Carrier", "Sample Mobile"),
        ]);

        var (assets, albums) = IphoneLibrary();

        var vlc = new FakeApp(new AppInfo("org.videolan.vlc-ios", "VLC", "3.6.4", 214 * MB, (long)(3.1 * GB), AppSource.Store, true, Date(2025, 3, 2)), (255, 136, 0));
        var documents = new FakeApp(new AppInfo("com.readdle.ReaddleDocuments", "Documents", "8.9.2", 230 * MB, (long)(1.2 * GB), AppSource.Store, true, Date(2024, 11, 20)), (0, 122, 255));
        var infuse = new FakeApp(new AppInfo("com.firecore.infuse", "Infuse", "8.1.5", 160 * MB, (long)(4.8 * GB), AppSource.Store, true, Date(2025, 6, 14)), (255, 102, 0));

        var roots = new List<FakeRoot>
        {
            new(new FileRoot("media", "Media folder", FileRootKind.Media), Dir("",
                Dir("DCIM",
                    Dir("100APPLE", Enumerable.Range(0, 40).Select(i =>
                        File($"IMG_{4380 + i:D4}.HEIC", 2_400_000 + i * 37_000L, Newest.AddHours(-i * 7))).ToArray())),
                Dir("Downloads", File("downloads.28.sqlitedb", 98_304, Date(2026, 10, 12))),
                Dir("Books", Dir("Purchases", File("Purchases.plist", 4_210, Date(2026, 4, 2)))),
                Dir("Recordings", File("Recording 14.m4a", 3_812_002, Date(2026, 10, 3))),
                Dir("PhotoData", readOnly: true, denied: false, File("Photos.sqlite", 412 * MB, Newest, readOnly: true)))),

            new(new FileRoot("app:" + vlc.Info.Id, "VLC", FileRootKind.AppDocuments, vlc.Info.Id), Dir("",
                File("Dune Part Two (2024).mkv", 1_912_000_000, Date(2026, 8, 21)),
                File("Arrival (2016).mp4", 1_188_000_000, Date(2026, 5, 9)),
                Dir("Subtitles",
                    File("Dune Part Two (2024).fa.srt", 98_112, Date(2026, 8, 21)),
                    File("Arrival (2016).fa.srt", 81_540, Date(2026, 5, 9))))),

            new(new FileRoot("app:" + documents.Info.Id, "Documents", FileRootKind.AppDocuments, documents.Info.Id), Dir("",
                File("Lease agreement 1405.pdf", 2_841_190, Date(2026, 9, 30)),
                File("Tax return 2025.pdf", 1_204_331, Date(2026, 3, 18)),
                File("Passport scan.pdf", 3_918_002, Date(2025, 12, 1)),
                Dir("Books",
                    File("The Blind Owl.epub", 1_480_220, Date(2025, 2, 14)),
                    File("Rumi - Masnavi (English).pdf", 48_210_993, Date(2024, 12, 2)),
                    File("Designing Data-Intensive Applications.pdf", 25_611_008, Date(2025, 7, 7))),
                Dir("Manuals",
                    File("Espresso machine.pdf", 8_804_112, Date(2025, 10, 10)),
                    File("Car service book.pdf", 14_002_331, Date(2026, 1, 25))),
                Dir("Scans", Enumerable.Range(1, 24).Select(i =>
                    File($"Scan {i:D3}.pdf", 9_800_000 + i * 1_210_000L, Date(2026, 6, 1).AddDays(i * 3))).ToArray()),
                Dir("Archive", File("Old projects.zip", 1_020_000_000, Date(2024, 9, 9))))),

            new(new FileRoot("app:" + infuse.Info.Id, "Infuse", FileRootKind.AppDocuments, infuse.Info.Id), Dir("",
                Dir("Movies",
                    File("Blade Runner 2049 (2017).mkv", 3_412_000_000, Date(2026, 7, 4))),
                Dir("Shows",
                    Dir("Severance",
                        File("Severance S02E01.mkv", 702_000_000, Date(2026, 2, 2)),
                        File("Severance S02E02.mkv", 688_000_000, Date(2026, 2, 9))))))
        };

        var apps = new List<FakeApp>
        {
            vlc, documents, infuse,
            new(new AppInfo("ph.telegra.Telegraph", "Telegram", "12.1", 180 * MB, 232 * MB, AppSource.Store, false, Date(2023, 5, 1)), (42, 171, 238)),
            new(new AppInfo("net.whatsapp.WhatsApp", "WhatsApp", "26.20.74", 290 * MB, (long)(1.51 * GB), AppSource.Store, false, Date(2023, 5, 1)), (37, 211, 102)),
            new(new AppInfo("com.burbn.instagram", "Instagram", "402.0", 410 * MB, 280 * MB, AppSource.Store, false, Date(2024, 1, 15)), (225, 48, 108)),
            new(new AppInfo("com.spotify.client", "Spotify", "9.0.86", 160 * MB, (long)(2.14 * GB), AppSource.Store, false, Date(2023, 6, 20)), (30, 215, 96)),
            new(new AppInfo("com.google.Maps", "Google Maps", "26.41", 290 * MB, 90 * MB, AppSource.Store, false, Date(2024, 3, 9)), (66, 133, 244)),
            new(new AppInfo("com.niksoftware.snapseed", "Snapseed", "3.1", 120 * MB, 40 * MB, AppSource.Store, false, Date(2025, 9, 9)), (76, 175, 80)),
        };

        return new FakeDeviceSpec
        {
            Summary = summary,
            Storage = storage,
            Battery = battery,
            Details = details,
            Assets = assets,
            Albums = albums,
            Roots = roots,
            Apps = apps,
            Screen = (603, 1311),
        };
    }

    public static FakeDeviceSpec Android()
    {
        var summary = new DeviceSummary(
            AndroidId,
            "Galaxy S25 Ultra",
            DevicePlatform.Android,
            "SM-S938B",
            "Galaxy S25 Ultra",
            "16",
            ConnectionKind.Wifi,
            DeviceState.Ready,
            DeviceMode.Full,
            DeviceCapability.Info | DeviceCapability.Storage | DeviceCapability.Battery |
            DeviceCapability.PhotosRead | DeviceCapability.PhotosDelete | DeviceCapability.PhotosImport |
            DeviceCapability.Files | DeviceCapability.AppDocuments |
            DeviceCapability.AppsList | DeviceCapability.AppsInstall | DeviceCapability.AppsUninstall | DeviceCapability.AppsExport |
            DeviceCapability.Backup | DeviceCapability.Screenshot | DeviceCapability.ScreenMirror | DeviceCapability.Syslog | DeviceCapability.CrashReports |
            DeviceCapability.Reboot | DeviceCapability.Shutdown | DeviceCapability.RebootToRecovery | DeviceCapability.RebootToBootloader,
            "#7D8FA6"); // Titanium Silverblue

        var storage = new StorageInfo([
            new StorageVolume("internal", "Internal storage", 512 * GB, 278 * GB, [
                new("photos", (long)(88.3 * GB)),
                new("videos", (long)(41.7 * GB)),
                new("apps", (long)(52.9 * GB)),
                new("media", (long)(7.4 * GB)),
                new("system", (long)(28.6 * GB)),
                new("other", (long)(15.1 * GB)),
            ]),
        ]);

        var battery = new BatteryInfo(64, false, 92, 268, 29.0, 5000, 4600);

        var details = new DeviceDetails([
            new("name", "Name", "Galaxy S25 Ultra"),
            new("model", "Model", "Samsung SM-S938B"),
            new("os", "Android", "16 (One UI 8.0)"),
            new("securityPatch", "Security patch", "1 October 2026"),
            new("colour", "Colour", "Titanium Silverblue"),
            new("capacity", "Capacity", "512 GB"),
            new("serial", "Serial number", AndroidId, Sensitive: true),
            new("imei", "IMEI", "35 881210 772046 1", Sensitive: true),
            new("wifi", "Wi-Fi address", "f0:5c:77:3a:91:e2"),
            new("bluetooth", "Bluetooth", "f0:5c:77:3a:91:e3"),
            new("carrier", "Carrier", "Sample Mobile"),
            new("connection", "Connection", "Wireless debugging, 192.168.1.42:41917"),
        ]);

        var (assets, albums) = AndroidLibrary();

        var roots = new List<FakeRoot>
        {
            new(new FileRoot("internal", "Internal storage", FileRootKind.InternalStorage), Dir("",
                Dir("Alarms"),
                Dir("Android",
                    Dir("data", readOnly: true, denied: true),
                    Dir("obb", readOnly: true, denied: true),
                    Dir("media",
                        Dir("com.whatsapp", Dir("WhatsApp", Dir("Media", Dir("WhatsApp Documents",
                            File("Invoice-0921.pdf", 211_002, Date(2026, 9, 21)))))),
                        Dir("org.telegram.messenger", Dir("Telegram",
                            Dir("Telegram Documents", File("Lecture 7 slides.pdf", 6_120_400, Date(2026, 10, 14))),
                            Dir("Telegram Video", File("VID_20261012_211500.mp4", 48_220_118, Date(2026, 10, 12))))),
                        Dir("com.spotify.music"))),
                Dir("DCIM",
                    Dir("Camera", Enumerable.Range(0, 30).Select(i =>
                        File($"{Newest.AddHours(-i * 9):yyyyMMdd_HHmmss}.jpg", 3_100_000 + i * 51_000L, Newest.AddHours(-i * 9))).ToArray()),
                    Dir("Screenshots", File("Screenshot_20261018_101544_Chrome.jpg", 812_330, Date(2026, 10, 18)))),
                Dir("Documents",
                    File("CV - 2026.pdf", 402_118, Date(2026, 8, 1)),
                    File("Budget.xlsx", 88_120, Date(2026, 10, 2))),
                Dir("Download",
                    File("Termux_v0.119.apk", 104_230_118, Date(2026, 7, 7)),
                    File("Ticket - Mashhad.pdf", 302_991, Date(2026, 10, 11)),
                    File("wallpaper-4k.jpg", 6_212_330, Date(2026, 9, 2))),
                Dir("Movies", File("Kids recital.mp4", 812_000_000, Date(2026, 6, 6))),
                Dir("Music",
                    File("Shajarian - Morgh-e Sahar.flac", 41_002_118, Date(2025, 1, 2)),
                    File("Podcast 112.mp3", 61_220_001, Date(2026, 10, 1))),
                Dir("Pictures", Dir("Snapseed", File("IMG_20260909_edit.jpg", 4_212_009, Date(2026, 9, 9)))),
                Dir("Ringtones"))),
        };

        var apps = new List<FakeApp>
        {
            new(new AppInfo("org.telegram.messenger", "Telegram", "12.1.1", 140 * MB, 2_310 * MB, AppSource.Store, false, Date(2023, 2, 1)), (42, 171, 238)),
            new(new AppInfo("com.whatsapp", "WhatsApp", "2.26.20.74", 120 * MB, 3_880 * MB, AppSource.Store, false, Date(2023, 2, 1)), (37, 211, 102)),
            new(new AppInfo("com.instagram.android", "Instagram", "402.0.0.38", 210 * MB, 640 * MB, AppSource.Store, false, Date(2024, 1, 15)), (225, 48, 108)),
            new(new AppInfo("org.videolan.vlc", "VLC", "3.6.5", 80 * MB, 12 * MB, AppSource.Store, false, Date(2024, 8, 8)), (255, 136, 0)),
            new(new AppInfo("com.spotify.music", "Spotify", "9.0.86.514", 130 * MB, 4_120 * MB, AppSource.Store, false, Date(2023, 6, 20)), (30, 215, 96)),
            new(new AppInfo("com.termux", "Termux", "0.119.0", 104 * MB, 1_410 * MB, AppSource.Sideloaded, false, Date(2026, 7, 7)), (34, 34, 34)),
            new(new AppInfo("com.android.chrome", "Chrome", "141.0.7390.122", 410 * MB, 880 * MB, AppSource.System, false, null), (219, 68, 55)),
            new(new AppInfo("com.samsung.android.app.notes", "Samsung Notes", "4.4.30.43", 120 * MB, 64 * MB, AppSource.System, false, null), (250, 160, 30)),
        };

        return new FakeDeviceSpec
        {
            Summary = summary,
            Storage = storage,
            Battery = battery,
            Details = details,
            Assets = assets,
            Albums = albums,
            Roots = roots,
            Apps = apps,
            Screen = (576, 1248),
        };
    }

    // 12,481 photos (live, screenshots and stills) + 732 videos, 18 albums.
    private static (IReadOnlyList<PhotoAsset>, IReadOnlyList<FakeAlbum>) IphoneLibrary()
    {
        const int live = 3104, screenshots = 1962, stills = 12481 - live - screenshots, videos = 732;
        var random = new Random(12481);
        var kinds = Shuffle(random,
            Enumerable.Repeat(PhotoKind.Photo, stills)
                .Concat(Enumerable.Repeat(PhotoKind.LivePhoto, live))
                .Concat(Enumerable.Repeat(PhotoKind.Screenshot, screenshots))
                .Concat(Enumerable.Repeat(PhotoKind.Video, videos)).ToArray());

        var assets = new List<PhotoAsset>(kinds.Length);
        var when = Newest;
        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            var number = (kinds.Length - i - 1) % 9999 + 1;
            var portrait = random.NextDouble() < 0.7;
            var (name, size, width, height, duration) = kind switch
            {
                PhotoKind.Screenshot => ($"IMG_{number:D4}.PNG", 400_000 + random.NextInt64(1_600_000), 1206, 2622, (double?)null),
                PhotoKind.Video => ($"IMG_{number:D4}.MOV", 3 * MB + (long)(Math.Pow(random.NextDouble(), 2.2) * 400 * MB), portrait ? 2160 : 3840, portrait ? 3840 : 2160, Math.Round(4 + Math.Pow(random.NextDouble(), 2) * 240, 1)),
                _ => ($"IMG_{number:D4}.HEIC", 3_500_000 + random.NextInt64(3_500_000), portrait ? 3024 : 4032, portrait ? 4032 : 3024, (double?)null),
            };

            var id = $"ios-{i:D5}";
            assets.Add(new PhotoAsset(
                id,
                name,
                kind,
                when,
                size,
                width,
                height,
                duration,
                IsFavorite: random.NextDouble() < 0.033,
                InCloudOnly: kind != PhotoKind.Screenshot && random.NextDouble() < 0.025,
                LivePairId: kind == PhotoKind.LivePhoto ? id + "-mov" : null));

            when = when.AddMinutes(-(5 + Math.Pow(random.NextDouble(), 3) * 900));
        }

        string[] userAlbums = ["Isfahan trip", "Family", "Tehran nights", "Cats", "Food", "Whiteboards", "Birthday 2026", "Caspian summer", "Receipts", "Wallpapers", "Concert", "Hiking"];
        var albums = new List<FakeAlbum>
        {
            new(new PhotoAlbum("all", "Recents", AlbumKind.All, assets.Count), []),
            Smart("camera", "Camera", AlbumKind.Camera, assets.Where(a => a.Kind != PhotoKind.Screenshot)),
            Smart("favorites", "Favourites", AlbumKind.Favorites, assets.Where(a => a.IsFavorite)),
            Smart("videos", "Videos", AlbumKind.Videos, assets.Where(a => a.Kind == PhotoKind.Video)),
            Smart("live", "Live Photos", AlbumKind.Live, assets.Where(a => a.Kind == PhotoKind.LivePhoto)),
            Smart("screenshots", "Screenshots", AlbumKind.Screenshots, assets.Where(a => a.Kind == PhotoKind.Screenshot)),
        };

        for (var u = 0; u < userAlbums.Length; u++)
        {
            // Each user album is a run of nearby photos, like a trip or an evening.
            var start = 200 + u * 977 % (assets.Count - 1200);
            var length = 40 + (u * 131 % 520);
            var members = assets.Skip(start).Take(length).Where(a => a.Kind != PhotoKind.Screenshot);
            albums.Add(Smart($"user-{u + 1}", userAlbums[u], AlbumKind.User, members));
        }

        return (assets, albums);
    }

    // 21,906 photos + 1,140 videos in 34 folders.
    private static (IReadOnlyList<PhotoAsset>, IReadOnlyList<FakeAlbum>) AndroidLibrary()
    {
        (string Name, double Weight, string Kind)[] folders =
        [
            ("Camera", 52, "camera"), ("Screenshots", 9, "shot"), ("WhatsApp Images", 9, "wa"), ("WhatsApp Video", 1.6, "wavideo"),
            ("Telegram", 5, "tg"), ("Instagram", 2.2, "plain"), ("Download", 2, "plain"), ("Pictures", 1.5, "plain"),
            ("Snapseed", 1.2, "plain"), ("Screen recordings", 0.8, "recording"), ("Edited", 1, "plain"), ("Collage", 0.4, "plain"),
            ("Bale", 0.9, "plain"), ("Rubika", 0.7, "plain"), ("Eitaa", 0.3, "plain"), ("Facebook", 0.6, "plain"),
            ("Messenger", 0.5, "plain"), ("Twitter", 0.6, "plain"), ("Signal", 0.4, "plain"), ("Slack", 0.3, "plain"),
            ("Discord", 0.4, "plain"), ("Pinterest", 0.5, "plain"), ("Reddit", 0.4, "plain"), ("Lightroom", 0.8, "plain"),
            ("PicsArt", 0.3, "plain"), ("CapCut", 0.5, "plain"), ("VSCO", 0.3, "plain"), ("Wallpapers", 0.6, "plain"),
            ("Bluetooth", 0.4, "plain"), ("Documents", 0.5, "plain"), ("Shared", 0.4, "plain"), ("Google Photos", 0.6, "plain"),
            ("Scanned", 0.5, "plain"), ("Gallery", 0.9, "plain"),
        ];

        const int photos = 21906, videos = 1140;
        var random = new Random(21906);
        var kinds = Shuffle(random, Enumerable.Repeat(false, photos).Concat(Enumerable.Repeat(true, videos)).ToArray());
        var total = folders.Sum(f => f.Weight);
        var members = folders.Select(_ => new List<string>()).ToArray();

        var assets = new List<PhotoAsset>(kinds.Length);
        var when = Newest.AddMinutes(-12);
        for (var i = 0; i < kinds.Length; i++)
        {
            var isVideo = kinds[i];
            var folder = PickFolder(random, folders, total, isVideo);
            var stamp = when.ToString("yyyyMMdd_HHmmss");
            var kind = folders[folder].Kind;
            var portrait = random.NextDouble() < 0.65;
            var (name, photoKind, size, width, height, duration) = (isVideo, kind) switch
            {
                (true, "wavideo") => ($"VID-{when:yyyyMMdd}-WA{i % 9999:D4}.mp4", PhotoKind.Video, 1 * MB + random.NextInt64(30 * MB), 848, 480, (double?)Math.Round(3 + random.NextDouble() * 90, 1)),
                (true, "recording") => ($"Screen_Recording_{stamp}.mp4", PhotoKind.Video, 5 * MB + random.NextInt64(90 * MB), 1440, 3120, Math.Round(5 + random.NextDouble() * 200, 1)),
                (true, _) => ($"{stamp}.mp4", PhotoKind.Video, 8 * MB + (long)(Math.Pow(random.NextDouble(), 2.2) * 700 * MB), portrait ? 2160 : 3840, portrait ? 3840 : 2160, Math.Round(3 + Math.Pow(random.NextDouble(), 2) * 300, 1)),
                (false, "shot") => ($"Screenshot_{stamp}_Chrome.jpg", PhotoKind.Screenshot, 300_000 + random.NextInt64(1_200_000), 1440, 3120, null),
                (false, "wa") => ($"IMG-{when:yyyyMMdd}-WA{i % 9999:D4}.jpg", PhotoKind.Photo, 80_000 + random.NextInt64(400_000), 1600, 1200, null),
                (false, "tg") => ($"photo_{when:yyyy-MM-dd_HH-mm-ss}.jpg", PhotoKind.Photo, 100_000 + random.NextInt64(500_000), 1280, 960, null),
                _ => ($"{stamp}.jpg", PhotoKind.Photo, 2_800_000 + random.NextInt64(4_000_000), portrait ? 3000 : 4000, portrait ? 4000 : 3000, null),
            };

            var id = (1_000_000_000L + i * 7L).ToString(System.Globalization.CultureInfo.InvariantCulture);
            assets.Add(new PhotoAsset(id, name, photoKind, when, size, width, height, duration,
                IsFavorite: random.NextDouble() < 0.02, InCloudOnly: false, LivePairId: null));
            members[folder].Add(id);
            when = when.AddMinutes(-(3 + Math.Pow(random.NextDouble(), 3) * 500));
        }

        var albums = new List<FakeAlbum>
        {
            new(new PhotoAlbum("all", "All photos", AlbumKind.All, assets.Count), []),
            Smart("videos", "Videos", AlbumKind.Videos, assets.Where(a => a.Kind == PhotoKind.Video)),
            Smart("favorites", "Favourites", AlbumKind.Favorites, assets.Where(a => a.IsFavorite)),
        };
        for (var f = 0; f < folders.Length; f++)
        {
            albums.Add(new FakeAlbum(new PhotoAlbum("folder-" + Slug(folders[f].Name), folders[f].Name, AlbumKind.Folder, members[f].Count), members[f]));
        }

        return (assets, albums);
    }

    private static int PickFolder(Random random, (string Name, double Weight, string Kind)[] folders, double total, bool isVideo)
    {
        if (isVideo)
        {
            var r = random.NextDouble();
            return r < 0.62 ? 0 : r < 0.82 ? 3 : r < 0.9 ? 9 : 4 + random.Next(folders.Length - 4);
        }

        var pick = random.NextDouble() * total;
        for (var i = 0; i < folders.Length; i++)
        {
            pick -= folders[i].Weight;
            if (pick <= 0)
            {
                return folders[i].Kind is "wavideo" or "recording" ? 0 : i;
            }
        }

        return 0;
    }

    private static FakeAlbum Smart(string id, string name, AlbumKind kind, IEnumerable<PhotoAsset> members)
    {
        var ids = members.Select(a => a.Id).ToArray();
        return new FakeAlbum(new PhotoAlbum(id, name, kind, ids.Length), ids);
    }

    private static T[] Shuffle<T>(Random random, T[] items)
    {
        random.Shuffle(items);
        return items;
    }

    private static string Slug(string name) => string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));

    private static DateTimeOffset Date(int year, int month, int day) => new(year, month, day, 12, 0, 0, Tehran);
}
