using System.IO.Compression;
using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Apps;
using DeviceDeck.Android.Files;
using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Core.Abstractions;
using SkiaSharp;

namespace DeviceDeck.Android.Tests;

public class DeviceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    internal static async Task<(AndroidDevice Device, FakeAdbChannel Fake)> ReadyAsync(string set, Action<FakeAdbChannel>? setup = null)
    {
        var fake = FakeAdbChannel.FromFixture(FixtureSet.Synthetic(set));
        setup?.Invoke(fake);
        var entry = new AdbDeviceEntry(fake.Serial, "device", TransportId: 1);
        var options = new AndroidProviderOptions { CacheDirectory = null, AppLabelBudget = TimeSpan.FromSeconds(5) };
        var device = new AndroidDevice(fake, entry, new AppMetadataCache(null), options);
        var profile = await device.LoadProfileAsync(None);
        device.SetSummary(SummaryBuilder.Ready(entry, profile));
        return (device, fake);
    }

    [Fact]
    public async Task Profile_reads_device_name_and_zone()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        Assert.Equal("Redmi Note 11", device.Profile!.DeviceName);
        Assert.Equal(TimeSpan.FromHours(3.5), device.Profile.UtcOffset);
        Assert.Equal("Redmi Note 11", device.Summary.Name);
        Assert.Empty(fake.Unmatched);

        var (oneplus, _) = await ReadyAsync("android-15-oneplus-cph2581");
        Assert.Null(oneplus.Profile!.DeviceName); // "null" from settings
    }

    [Fact]
    public async Task Storage_battery_and_details_in_one_round_trip_each()
    {
        var (device, fake) = await ReadyAsync("android-13-samsung-sm-a536b");
        fake.Commands.Clear();
        var storage = await device.GetStorageAsync(None);
        var battery = await device.GetBatteryAsync(None);
        var details = await device.GetDetailsAsync(None);
        Assert.Equal(3, fake.Commands.Count);
        Assert.Equal(2, storage.Volumes.Count);
        Assert.Equal(57, battery.LevelPercent);
        Assert.Contains(details.Rows, r => r.Key == "serial" && r.Value == "R5CT20ABCDE");
        Assert.Empty(fake.Unmatched);
    }

    [Fact]
    public async Task Missing_battery_state_is_an_error()
    {
        var (device, _) = await ReadyAsync("android-14-google-pixel-7", f => f.On("dumpsys battery", "Can't find service: battery\n"));
        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.GetBatteryAsync(None));
        Assert.Equal(DeviceErrorCodes.Protocol, ex.Code);
    }

    [Fact]
    public async Task Unauthorized_device_refuses_with_the_prompt_hint()
    {
        var fake = new FakeAdbChannel();
        var device = new AndroidDevice(fake, new AdbDeviceEntry("x", "unauthorized"), new AppMetadataCache(null), new AndroidProviderOptions { CacheDirectory = null });
        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.GetStorageAsync(None));
        Assert.Equal(DeviceErrorCodes.Unauthorized, ex.Code);
        Assert.Contains("Allow USB debugging", ex.Message, StringComparison.Ordinal);
        Assert.Empty(fake.Commands);
    }

    [Fact]
    public async Task File_roots_include_the_sd_card()
    {
        var (device, _) = await ReadyAsync("android-13-samsung-sm-a536b");
        var roots = await device.GetFileRootsAsync(None);
        Assert.Equal(
            [new FileRoot("internal", "Internal storage", FileRootKind.InternalStorage), new FileRoot("sd:3A1F-12C9", "SD card", FileRootKind.SdCard)],
            roots);
        await using var sd = await device.OpenFileSystemAsync("sd:3A1F-12C9", None);
        Assert.Equal("/storage/3A1F-12C9", ((AndroidFileSystem)sd).BasePath);
        await Assert.ThrowsAsync<DeviceException>(() => device.OpenFileSystemAsync("sd:../../data", None));
        await Assert.ThrowsAsync<DeviceException>(() => device.OpenFileSystemAsync("app:com.x", None));
    }

    [Fact]
    public async Task Listing_uses_stat_and_returns_root_relative_paths()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        await using var fs = await device.OpenFileSystemAsync("internal", None);
        var entries = await fs.ListAsync("/", None).ToListAsync();
        Assert.Equal(13, entries.Count);
        Assert.True(entries.Take(8).All(e => e.IsDirectory)); // folders first
        Assert.Contains(entries, e => e is { Path: "/Telegram Files", IsDirectory: true });
        Assert.Contains(entries, e => e is { Path: "/music link", IsDirectory: true }); // symlink to a folder, followed
        var pdf = entries.Single(e => e.Name.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Equal("/گزارش مالی ۱۴۰۳.pdf", pdf.Path);
        Assert.Equal(20480, pdf.Size);
        Assert.Contains(entries, e => e.Path == "/a|b.txt");
        Assert.Empty(fake.Unmatched);
    }

    [Fact]
    public async Task Listing_falls_back_to_ls_when_find_or_stat_is_missing()
    {
        var (device, _) = await ReadyAsync("android-12-xiaomi-2201117tg",
            f => f.On(FileListingParser.StatListCommand("'/sdcard/'"), "", 1, "find: Unknown option '-mindepth'"));
        await using var fs = await device.OpenFileSystemAsync("internal", None);
        var entries = await fs.ListAsync("", None).ToListAsync();
        Assert.Equal(12, entries.Count);
        var notes = entries.Single(e => e.Name == "My Notes (copy).txt");
        Assert.Equal(new DateTimeOffset(2024, 4, 2, 7, 15, 0, TimeSpan.FromHours(3.5)), notes.Modified);
    }

    [Fact]
    public async Task Listing_a_missing_folder_is_path_not_found()
    {
        var (device, _) = await ReadyAsync("android-12-xiaomi-2201117tg",
            f => f.On(FileListingParser.StatListCommand("'/sdcard/Nope/'"), "", 1, "find: '/sdcard/Nope/': No such file or directory"));
        await using var fs = await device.OpenFileSystemAsync("internal", None);
        var ex = await Assert.ThrowsAsync<DeviceException>(() => fs.ListAsync("/Nope", None).ToListAsync().AsTask());
        Assert.Equal(DeviceErrorCodes.PathNotFound, ex.Code);
    }

    [Fact]
    public async Task Stat_returns_null_for_missing_paths()
    {
        var (device, _) = await ReadyAsync("android-12-xiaomi-2201117tg", f =>
        {
            f.On("stat -L -c '%f|%s|%Y|%n' -- '/sdcard/DCIM'", "45f8|3452|1714546800|/sdcard/DCIM\n");
            f.On("stat -L -c '%f|%s|%Y|%n' -- '/sdcard/missing.txt'", "", 1, "stat: '/sdcard/missing.txt': No such file or directory");
        });
        await using var fs = await device.OpenFileSystemAsync("internal", None);
        var dcim = await fs.StatAsync("/DCIM/", None);
        Assert.Equal(new FileEntry("/DCIM", "DCIM", true, 0, DateTimeOffset.FromUnixTimeSeconds(1714546800)), dcim);
        Assert.Null(await fs.StatAsync("/missing.txt", None));
    }

    [Fact]
    public async Task Write_and_read_go_through_sync_and_android_10_gets_a_media_scan()
    {
        var fake = FakeAdbChannel.FromFixture(FixtureSet.Synthetic("android-12-xiaomi-2201117tg"));
        var fs = new AndroidFileSystem(fake, "/sdcard", sdk: 29, TimeSpan.Zero);
        byte[] content = Encoding.UTF8.GetBytes("hello");
        long reported = 0;
        await fs.WriteAsync("/DCIM/my photo.jpg", new MemoryStream(content), content.Length, new Progress<long>(v => reported = v), None);
        Assert.Equal(content, fake.Files["/sdcard/DCIM/my photo.jpg"]);
        Assert.Contains(fake.Commands, c => c.Contains(
            "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d 'file:///storage/emulated/0/DCIM/my%20photo.jpg' -p com.google.android.providers.media.module",
            StringComparison.Ordinal));

        var copy = new MemoryStream();
        await fs.ReadAsync("/DCIM/my photo.jpg", copy, null, None);
        Assert.Equal(content, copy.ToArray());

        var android14 = new AndroidFileSystem(fake, "/sdcard", sdk: 34, TimeSpan.Zero);
        fake.Commands.Clear();
        await android14.WriteAsync("/x.txt", new MemoryStream(content), content.Length, null, None);
        Assert.DoesNotContain(fake.Commands, c => c.StartsWith("am broadcast", StringComparison.Ordinal));
        await fs.DisposeAsync();
    }

    [Fact]
    public async Task Delete_rename_and_mkdir_scripts_and_errors()
    {
        var fake = new FakeAdbChannel();
        var fs = new AndroidFileSystem(fake, "/sdcard", sdk: 34, TimeSpan.Zero);

        fake.On(c => c.Contains("rm -rf -- '/sdcard/Old Stuff'", StringComparison.Ordinal), _ => FakeReply.Text(""));
        await fs.DeleteAsync("/Old Stuff", recursive: true, None);

        fake.On(c => c.Contains("'/sdcard/gone.txt'", StringComparison.Ordinal), _ => FakeReply.Text("", 2, "No such file or directory"));
        Assert.Equal(DeviceErrorCodes.PathNotFound, (await Assert.ThrowsAsync<DeviceException>(() => fs.DeleteAsync("/gone.txt", false, None))).Code);

        fake.On(c => c.Contains("rmdir -- '/sdcard/full'", StringComparison.Ordinal), _ => FakeReply.Text("", 1, "rmdir: '/sdcard/full': Directory not empty"));
        var notEmpty = await Assert.ThrowsAsync<DeviceException>(() => fs.DeleteAsync("/full", false, None));
        Assert.Contains("isn't empty", notEmpty.Message, StringComparison.Ordinal);

        Assert.Equal(DeviceErrorCodes.PermissionDenied, (await Assert.ThrowsAsync<DeviceException>(() => fs.DeleteAsync("/", true, None))).Code);
        Assert.Equal(DeviceErrorCodes.PermissionDenied, (await Assert.ThrowsAsync<DeviceException>(() => fs.DeleteAsync("/a/../../data", true, None))).Code);

        fake.On(c => c.StartsWith("if [ -e '/sdcard/b.txt' ]", StringComparison.Ordinal), _ => FakeReply.Text("", 17, "File exists"));
        Assert.Equal(DeviceErrorCodes.AlreadyExists, (await Assert.ThrowsAsync<DeviceException>(() => fs.RenameAsync("/a.txt", "/b.txt", None))).Code);

        fake.On("mkdir -- '/sdcard/New Folder'", "");
        await fs.CreateDirectoryAsync("New Folder", None);
        fake.On("mkdir -- '/sdcard/DCIM'", "", 1, "mkdir: '/sdcard/DCIM': File exists");
        Assert.Equal(DeviceErrorCodes.AlreadyExists, (await Assert.ThrowsAsync<DeviceException>(() => fs.CreateDirectoryAsync("/DCIM", None))).Code);
    }

    [Theory]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("DCIM//Camera/", "/DCIM/Camera")]
    [InlineData("\\DCIM\\Camera", "/DCIM/Camera")]
    [InlineData("/./a/./b", "/a/b")]
    public void Root_paths_normalise(string input, string expected)
    {
        Assert.Equal(expected, RootPath.Normalize(input));
    }

    [Fact]
    public async Task Photos_albums_paging_and_filters()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        var albums = await device.Photos.GetAlbumsAsync(None).ToListAsync();
        Assert.Equal(["all", "videos", "favorites"], albums.Take(3).Select(a => a.Id));
        Assert.Contains(albums, a => a is { Name: "Camera", Kind: AlbumKind.Camera, Count: 2 });

        var page = await device.Photos.GetAssetsAsync(null, 0, 2, None);
        Assert.Equal(5, page.Total);
        Assert.Equal(["1003", "1002"], page.Items.Select(a => a.Id));
        var next = await device.Photos.GetAssetsAsync(null, 2, 2, None);
        Assert.Equal(["1001", "1004"], next.Items.Select(a => a.Id));
        Assert.Equal(["1002"], (await device.Photos.GetAssetsAsync("videos", 0, 10, None)).Items.Select(a => a.Id));
        Assert.Equal(2, (await device.Photos.GetAssetsAsync("favorites", 0, 10, None)).Total);
        string camera = albums.First(a => a.Kind == AlbumKind.Camera).Id;
        Assert.Equal(2, (await device.Photos.GetAssetsAsync(camera, 0, 10, None)).Total);
        await Assert.ThrowsAsync<DeviceException>(() => device.Photos.GetAssetsAsync("nope", 0, 10, None));

        // One snapshot serves paging and album switches.
        Assert.Single(fake.Commands, c => c.StartsWith("content query", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Thumbnails_from_originals_and_from_mediastore()
    {
        byte[] small = TestApks.Jpeg(64, 48, SKColors.Blue);
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg", f =>
        {
            f.On(c => c == "content read --uri content://media/external/images/media/1005/thumbnail", _ => new FakeReply(0, small, ""));
            f.On(c => c == "content read --uri content://media/external/video/media/1002/thumbnail", _ => new FakeReply(0, small, ""));
        });
        fake.Files["/storage/emulated/0/DCIM/Camera/IMG_20240501_101500.jpg"] = TestApks.Jpeg(800, 600, SKColors.Red);
        await device.Photos.GetAssetsAsync(null, 0, 10, None);

        using (var jpeg = SKBitmap.Decode(await device.Photos.GetThumbnailAsync("1001", 256, None)))
        {
            Assert.Equal((256, 192), (jpeg.Width, jpeg.Height));
        }
        Assert.NotNull(await device.Photos.GetThumbnailAsync("1005", 256, None)); // HEIC
        Assert.NotNull(await device.Photos.GetThumbnailAsync("1002", 256, None)); // video
        Assert.Null(await device.Photos.GetThumbnailAsync("1004", 256, None));    // original missing, MediaStore has none
        await Assert.ThrowsAsync<DeviceException>(() => device.Photos.GetThumbnailAsync("not-an-id", 256, None));
    }

    [Fact]
    public async Task Original_is_pulled_by_path()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        fake.Files["/storage/emulated/0/Pictures/Trips/Trip, Day 1, sunset.jpg"] = [1, 2, 3];
        var copy = new MemoryStream();
        await device.Photos.ReadOriginalAsync("1004", copy, null, None);
        Assert.Equal([1, 2, 3], copy.ToArray());
    }

    [Fact]
    public async Task Delete_uses_content_delete_and_cleans_up_what_mediastore_kept()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        string leftover = "Row: 0 _id=1002, _display_name=VID_20240502_183000.mp4, _data=/storage/emulated/0/DCIM/Camera/VID_20240502_183000.mp4, media_type=3, mime_type=video/mp4, _size=52428800, datetaken=1714660200000, date_added=1714660216, date_modified=1714660215, width=1920, height=1080, duration=15433, orientation=0, bucket_id=-1739773001, bucket_display_name=Camera, relative_path=DCIM/Camera/, is_favorite=0\n";
        fake.On(c => c.StartsWith("content delete", StringComparison.Ordinal), _ => FakeReply.Text("Deleted 1 rows\n"));
        fake.On(c => c.StartsWith("content query", StringComparison.Ordinal) && c.Contains("_id IN (1001,1002)", StringComparison.Ordinal), _ => FakeReply.Text(leftover));
        fake.On(c => c.StartsWith("rm -f -- ", StringComparison.Ordinal), _ => FakeReply.Text(""));

        await device.Photos.DeleteAsync(["1001", "1002"], None);
        Assert.Contains("content delete --uri content://media/external/file --where '_id IN (1001,1002)'", fake.Commands);
        Assert.Contains("rm -f -- '/storage/emulated/0/DCIM/Camera/VID_20240502_183000.mp4'", fake.Commands);
        Assert.Contains(fake.Commands, c => c.Contains("MEDIA_SCANNER_SCAN_FILE -d 'file:///storage/emulated/0/DCIM/Camera/VID_20240502_183000.mp4'", StringComparison.Ordinal));
        await Assert.ThrowsAsync<DeviceException>(() => device.Photos.DeleteAsync(["1; rm -rf /"], None));
    }

    [Fact]
    public async Task Import_pushes_to_dcim_devicedeck_with_a_unique_name_and_scans()
    {
        var (device, fake) = await ReadyAsync("android-14-google-pixel-7", f =>
        {
            f.On("mkdir -p '/sdcard/DCIM/DeviceDeck'", "");
            f.On("[ -e '/sdcard/DCIM/DeviceDeck/trip.jpg' ]", "", 0);   // taken
            f.On("[ -e '/sdcard/DCIM/DeviceDeck/trip (1).jpg' ]", "", 1);
            f.On(c => c.Contains("_data='/storage/emulated/0/DCIM/DeviceDeck/trip (1).jpg'", StringComparison.Ordinal),
                _ => FakeReply.Text("Row: 0 _id=99, _display_name=trip (1).jpg, _data=/storage/emulated/0/DCIM/DeviceDeck/trip (1).jpg, media_type=1, mime_type=image/jpeg, _size=3, datetaken=1, date_added=1, date_modified=1, width=1, height=1, duration=NULL, orientation=0, bucket_id=5, bucket_display_name=DeviceDeck, relative_path=DCIM/DeviceDeck/, is_favorite=0\n"));
        });
        await device.Photos.ImportAsync(@"C:\Users\me\Pictures\trip.jpg", new MemoryStream([1, 2, 3]), 3, null, None);
        Assert.Equal([1, 2, 3], fake.Files["/sdcard/DCIM/DeviceDeck/trip (1).jpg"]);
        Assert.Contains(fake.Commands, c => c.StartsWith(
            "am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d 'file:///storage/emulated/0/DCIM/DeviceDeck/trip%20%281%29.jpg' -p com.google.android.providers.media.module",
            StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData(@"C:\a\b\photo.jpg", "photo.jpg")]
    [InlineData("a:b*c?.jpg", "a_b_c_.jpg")]
    [InlineData("..hidden.png", "hidden.png")]
    public void Import_names_are_made_safe(string input, string expected)
    {
        Assert.Equal(expected, AndroidPhotoLibrary.SafeFileName(input));
    }

    [Fact]
    public async Task Apps_list_joins_pm_dumpsys_and_diskstats()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg",
            f => f.OnPrefix("du -sk ", "123\t/data/app/~~QmF6YWFyQXBwUGF0aA==/com.farsitel.bazaar-YmF6YWFyX2luc3RhbGw==\n"));
        var apps = await device.Apps.ListAsync(includeSystem: false, None);
        Assert.Equal(["com.farsitel.bazaar", "com.instagram.android", "com.whatsapp", "ir.mservices.market", "org.telegram.messenger"],
            apps.Select(a => a.Id).Order());
        var whatsapp = apps.Single(a => a.Id == "com.whatsapp");
        Assert.Equal("com.whatsapp", whatsapp.Name); // no APK to read a label from in this fake
        Assert.Equal("2.23.12.75", whatsapp.Version);
        Assert.Equal(104857600, whatsapp.AppBytes);
        Assert.Equal(2147483648 + 52428800, whatsapp.DataBytes);
        Assert.Equal(AppSource.Store, whatsapp.Source);
        Assert.Equal(new DateTimeOffset(2021, 9, 2, 14, 5, 33, TimeSpan.FromHours(3.5)), whatsapp.InstalledAt);
        Assert.Equal(AppSource.Sideloaded, apps.Single(a => a.Id == "ir.mservices.market").Source);
        Assert.Equal(AppSource.Store, apps.Single(a => a.Id == "com.instagram.android").Source); // from Cafe Bazaar
        Assert.Equal("41622", apps.Single(a => a.Id == "org.telegram.messenger").Version);   // not in dumpsys: version code
        Assert.Equal(123 * 1024, apps.Single(a => a.Id == "com.farsitel.bazaar").AppBytes);  // du fallback

        var all = await device.Apps.ListAsync(includeSystem: true, None);
        Assert.Equal(11, all.Count);
        Assert.Equal(AppSource.System, all.Single(a => a.Id == "com.android.chrome").Source);
    }

    [Fact]
    public async Task App_labels_and_icons_are_read_from_the_apk_and_cached()
    {
        const string sourceDir = "/data/app/~~p5Zx0hAQ0JcHzaQ2Hk3s1A==/com.whatsapp-Jw7qQ3r_lwGv0g9sF7ukXg==/base.apk";
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg", f =>
        {
            f.OnPrefix("du -sk ", "");
            f.On($"{AndroidAppManager.ListCommand} 'com.whatsapp'",
                $"package:{sourceDir}=com.whatsapp versionCode:231275003  installer=com.android.vending uid:10213\n");
        });
        fake.Files[sourceDir] = TestApks.BitmapIcon(package: "com.whatsapp");

        var apps = await device.Apps.ListAsync(false, None);
        Assert.Equal("Test App", apps.Single(a => a.Id == "com.whatsapp").Name);

        fake.Commands.Clear();
        await device.Apps.ListAsync(false, None);
        Assert.DoesNotContain(fake.Commands, c => c.Contains(sourceDir, StringComparison.Ordinal)); // label came from the cache

        byte[]? icon = await device.Apps.GetIconAsync("com.whatsapp", None);
        using var bitmap = SKBitmap.Decode(icon);
        Assert.Equal(192, bitmap.Width);
        Assert.Null(await device.Apps.GetIconAsync("com.not.installed", None));
        await Assert.ThrowsAsync<DeviceException>(() => device.Apps.GetIconAsync("not a package", None));
    }

    [Fact]
    public async Task Single_apk_install_streams_with_the_size()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        string apk = TempFile("app.apk", 5000);
        var stages = new List<string>();
        await device.Apps.InstallAsync([apk], new SyncProgress<InstallProgress>(p => stages.Add(p.Stage)), None);
        var (command, input) = Assert.Single(fake.Execs);
        Assert.Equal("cmd package install -r -S 5000", command);
        Assert.Equal(5000, input.Length);
        Assert.Equal(["preparing", "uploading", "installing", "done"], stages.Distinct());
    }

    [Fact]
    public async Task Xiaomi_install_via_usb_off_is_explained()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        fake.ExecHandler = (_, _) => "Failure [INSTALL_FAILED_USER_RESTRICTED: Install canceled by user]\n";
        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.Apps.InstallAsync([TempFile("app.apk", 10)], null, None));
        Assert.Equal(DeviceErrorCodes.InstallFailed, ex.Code);
        Assert.Contains("Install via USB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Split_apks_use_an_install_session_and_skip_foreign_abis()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        fake.ExecHandler = (command, _) => command.StartsWith("cmd package install-create", StringComparison.Ordinal)
            ? "Success: created install session [1234]\n"
            : "Success\n";
        string folder = Path.Combine(Path.GetTempPath(), "dd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string[] files =
        [
            TempFile(Path.Combine(folder, "base.apk"), 100),
            TempFile(Path.Combine(folder, "split_config.arm64_v8a.apk"), 20),
            TempFile(Path.Combine(folder, "split_config.x86_64.apk"), 30),
            TempFile(Path.Combine(folder, "split_config.xxhdpi.apk"), 10),
        ];
        await device.Apps.InstallAsync(files, null, None);
        Assert.Equal(
        [
            "cmd package install-create -r -S 130",
            "cmd package install-write -S 100 1234 0_base.apk -",
            "cmd package install-write -S 20 1234 1_split_config.arm64_v8a.apk -",
            "cmd package install-write -S 10 1234 2_split_config.xxhdpi.apk -",
            "cmd package install-commit 1234",
        ], fake.Execs.Select(e => e.Command));
    }

    [Fact]
    public async Task A_failed_commit_abandons_the_session()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg");
        fake.ExecHandler = (command, _) => command switch
        {
            _ when command.StartsWith("cmd package install-create", StringComparison.Ordinal) => "Success: created install session [77]\n",
            _ when command.StartsWith("cmd package install-commit", StringComparison.Ordinal) => "Failure [INSTALL_FAILED_INSUFFICIENT_STORAGE]\n",
            _ => "Success\n",
        };
        string folder = Path.Combine(Path.GetTempPath(), "dd-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.Apps.InstallAsync(
            [TempFile(Path.Combine(folder, "base.apk"), 10), TempFile(Path.Combine(folder, "split_config.en.apk"), 5)], null, None));
        Assert.Contains("enough free space", ex.Message, StringComparison.Ordinal);
        Assert.Equal("cmd package install-abandon 77", fake.Execs.Last().Command);
    }

    [Fact]
    public async Task Xapk_installs_its_splits_and_pushes_obb_files()
    {
        var (device, fake) = await ReadyAsync("android-14-google-pixel-7");
        fake.ExecHandler = (command, _) => command.StartsWith("cmd package install-create", StringComparison.Ordinal)
            ? "Success: created install session [5]\n"
            : "Success\n";
        string xapk = Path.Combine(Path.GetTempPath(), $"dd-{Guid.NewGuid():N}.xapk");
        using (var zip = ZipFile.Open(xapk, ZipArchiveMode.Create))
        {
            foreach (var (name, size) in new[] { ("com.game.apk", 50), ("config.arm64_v8a.apk", 20), ("config.armeabi_v7a.apk", 20), ("Android/obb/com.game/main.3.com.game.obb", 40) })
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(new byte[size]);
            }
            using var manifest = zip.CreateEntry("manifest.json").Open();
            manifest.Write("{}"u8);
        }
        await device.Apps.InstallAsync([xapk], null, None);
        Assert.Equal(
        [
            "cmd package install-create -r -S 70",
            "cmd package install-write -S 50 5 0_com.game.apk -",
            "cmd package install-write -S 20 5 1_config.arm64_v8a.apk -",
            "cmd package install-commit 5",
        ], fake.Execs.Select(e => e.Command));
        Assert.Equal(40, fake.Files["/sdcard/Android/obb/com.game/main.3.com.game.obb"].Length);
    }

    [Fact]
    public async Task Uninstall_and_export()
    {
        var (device, fake) = await ReadyAsync("android-12-xiaomi-2201117tg", f =>
        {
            f.On("pm uninstall com.whatsapp", "Success\n");
            f.On("pm uninstall com.android.settings", "Failure [DELETE_FAILED_INTERNAL_ERROR]\n");
            f.On("pm path com.whatsapp", "package:/data/app/x/base.apk\npackage:/data/app/x/split_config.arm64_v8a.apk\n");
        });
        await device.Apps.UninstallAsync("com.whatsapp", None);
        Assert.Equal(DeviceErrorCodes.InstallFailed, (await Assert.ThrowsAsync<DeviceException>(() => device.Apps.UninstallAsync("com.android.settings", None))).Code);
        await Assert.ThrowsAsync<DeviceException>(() => device.Apps.UninstallAsync("com.x; reboot", None));
        Assert.DoesNotContain(fake.Commands, c => c.Contains("reboot", StringComparison.Ordinal));

        fake.Files["/data/app/x/base.apk"] = [1];
        fake.Files["/data/app/x/split_config.arm64_v8a.apk"] = [2, 2];
        string folder = Path.Combine(Path.GetTempPath(), "dd-export-" + Guid.NewGuid().ToString("N"));
        var written = await device.Apps.ExportAsync("com.whatsapp", folder, None);
        Assert.Equal([Path.Combine(folder, "com.whatsapp.apk"), Path.Combine(folder, "com.whatsapp.split_config.arm64_v8a.apk")], written);
        Assert.Equal([2, 2], File.ReadAllBytes(written[1]));
    }

    [Fact]
    public async Task Actions_reboot_shutdown_and_screenshot()
    {
        byte[] png = TestApks.Png(4, SKColors.White);
        var (device, fake) = await ReadyAsync("android-14-google-pixel-7", f =>
        {
            f.On(c => c == "screencap -p", _ => new FakeReply(0, png, ""));
            f.On(c => c == "reboot -p", _ => throw new DeviceException(DeviceErrorCodes.Disconnected, "gone"));
        });
        Assert.Equal(png, await device.Actions.TakeScreenshotAsync(None));
        await device.Actions.ShutdownAsync(None); // the connection dropping is success
        await device.Actions.RebootAsync(RebootTarget.Recovery, None);
        await device.Actions.RebootAsync(RebootTarget.System, None);
        Assert.Equal(["recovery", ""], fake.Reboots);

        fake.On(c => c == "screencap -p", _ => FakeReply.Text("", 1, "screencap: Capturing failed."));
        Assert.Equal(DeviceErrorCodes.Protocol, (await Assert.ThrowsAsync<DeviceException>(() => device.Actions.TakeScreenshotAsync(None))).Code);
    }

    private static string TempFile(string nameOrPath, int size)
    {
        string path = Path.IsPathRooted(nameOrPath) ? nameOrPath : Path.Combine(Path.GetTempPath(), $"dd-{Guid.NewGuid():N}-{nameOrPath}");
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
