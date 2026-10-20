using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

/// <summary>Checks every fixture folder (synthetic and, once added, captured from real phones) parses into sane values.</summary>
public class FixtureInvariantTests
{
    public static TheoryData<string> Sets => FixtureSet.AllNames();

    [Theory]
    [MemberData(nameof(Sets))]
    public void Getprop_gives_a_profile(string name)
    {
        var set = FixtureSet.ByRelativePath(name);
        var props = GetPropParser.Parse(set.Read("getprop.txt"));
        var profile = new DeviceProfile(props, null, TimeSpan.Zero);
        Assert.InRange(profile.Sdk, 29, 40);
        Assert.False(string.IsNullOrWhiteSpace(profile.Model));
        Assert.False(string.IsNullOrWhiteSpace(profile.AndroidVersion));
        Assert.NotEmpty(profile.SupportedAbis);
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Storage_adds_up(string name)
    {
        var set = FixtureSet.ByRelativePath(name);
        var volumes = StorageBuilder.ParseVolumes(set.TryRead("ls-storage.txt"), set.TryRead("sm-list-volumes.txt"));
        var storage = StorageBuilder.Build(DfParser.Parse(set.Read("df.txt")), DiskStatsParser.Parse(set.Read("dumpsys-diskstats.txt")), volumes);
        var internalVolume = storage.Volumes[0];
        Assert.Equal("internal", internalVolume.Key);
        Assert.InRange(internalVolume.FreeBytes, 1, internalVolume.TotalBytes);
        long used = internalVolume.TotalBytes - internalVolume.FreeBytes;
        Assert.InRange(internalVolume.Categories.Sum(c => c.Bytes), 0, used);
        if (internalVolume.Categories.Count > 0)
        {
            Assert.Equal(used, internalVolume.Categories.Sum(c => c.Bytes));
        }
        Assert.Equal(volumes.Count + 1, storage.Volumes.Count);
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Battery_is_in_range(string name)
    {
        var set = FixtureSet.ByRelativePath(name);
        var battery = BatteryParser.Parse(set.Read("dumpsys-battery.txt"), set.TryRead("battery-sysfs.txt"), set.TryRead("battery-sticky.txt"));
        Assert.InRange(battery.LevelPercent!.Value, 0, 100);
        Assert.InRange(battery.TemperatureCelsius!.Value, -20, 80);
        if (battery.HealthPercent is { } health)
        {
            Assert.InRange(health, 1, 100);
        }
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Package_listing_parses_every_line(string name)
    {
        var set = FixtureSet.ByRelativePath(name);
        string text = set.Read("pm-list-packages.txt");
        var entries = PackageListParser.Parse(text);
        Assert.Equal(text.Split('\n').Count(l => l.StartsWith("package:", StringComparison.Ordinal)), entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Matches(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$", e.PackageName);
            Assert.EndsWith(".apk", e.SourceDir);
            Assert.NotNull(e.VersionCode);
        });
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Ls_listing_has_entries_without_dot_dirs(string name)
    {
        var set = FixtureSet.ByRelativePath(name);
        var files = FileListingParser.ParseLs(set.Read("ls-la-sdcard.txt"), TimeSpan.Zero);
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => f.Name is "." or "..");
        Assert.All(files, f => Assert.NotNull(f.Modified));
        Assert.Contains(files, f => f.IsDirectory && f.Name == "DCIM");
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Media_query_parses_every_row(string name)
    {
        var set = FixtureSet.ByRelativePath(name);
        var profile = new DeviceProfile(GetPropParser.Parse(set.Read("getprop.txt")), null, TimeSpan.Zero);
        string text = set.Read("content-query-media.txt");
        var items = MediaQuery.Parse(text, profile.Sdk);
        Assert.Equal(text.Split('\n').Count(l => l.StartsWith("Row: ", StringComparison.Ordinal)), items.Count);
        Assert.All(items, i =>
        {
            Assert.StartsWith("/storage/", i.Path);
            Assert.NotNull(i.TakenAt);
            Assert.True(i.Size > 0);
        });
        Assert.Equal(items.OrderByDescending(i => i.TakenAt).Select(i => i.Id), items.Select(i => i.Id));
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Devices_line_parses(string name)
    {
        var set = FixtureSet.ByRelativePath(name);
        var entry = Assert.Single(TrackDevicesParser.Parse(set.Read("devices-l.txt")));
        Assert.Equal("device", entry.State);
        Assert.NotNull(entry.TransportId);
    }
}

/// <summary>Exact values for the synthetic phones (written by hand, so the expected values are known).</summary>
public class SyntheticFixtureTests
{
    private static DeviceProfile Profile(string set, string? deviceName = null) =>
        new(GetPropParser.Parse(FixtureSet.Synthetic(set).Read("getprop.txt")), deviceName, TimeSpan.FromHours(3.5));

    [Fact]
    public void Xiaomi_marketing_name_and_chipset()
    {
        var profile = Profile("android-12-xiaomi-2201117tg");
        Assert.Equal("2201117TG", profile.Model);
        Assert.Equal("Redmi Note 11", profile.MarketingName);
        Assert.Equal(31, profile.Sdk);
        Assert.Equal("QTI SM6225", profile.Chipset);
        Assert.Equal(["arm64-v8a", "armeabi-v7a", "armeabi"], profile.SupportedAbis);
        Assert.Equal("MPSS.HI.3.1.c3-00186-SDM680_GEN_PACK-1.443420.1.452424.1", profile.Baseband);
    }

    [Fact]
    public void Oneplus_name_comes_from_the_oplus_property()
    {
        Assert.Equal("OnePlus 12", Profile("android-15-oneplus-cph2581").MarketingName);
    }

    [Fact]
    public void Samsung_has_no_marketing_property_so_the_device_name_is_shown()
    {
        var profile = Profile("android-13-samsung-sm-a536b", "Galaxy A53 5G");
        Assert.Null(profile.MarketingName);
        Assert.Equal("Galaxy A53 5G", profile.DisplayName);
        Assert.Equal("SM-A536B", profile.Model);
    }

    [Fact]
    public void Pixel_color_and_name()
    {
        var profile = Profile("android-14-google-pixel-7");
        Assert.Equal("Pixel 7", profile.DisplayName);
        Assert.Null(profile.MarketingName);
        Assert.Equal("#202124", profile.ColorHex);
        Assert.Equal("16", Profile("android-16-google-pixel-9").AndroidVersion);
    }

    [Fact]
    public void Xiaomi_storage_matches_the_settings_app()
    {
        var set = FixtureSet.Synthetic("android-12-xiaomi-2201117tg");
        var storage = StorageBuilder.Build(DfParser.Parse(set.Read("df.txt")), DiskStatsParser.Parse(set.Read("dumpsys-diskstats.txt")), []);
        var volume = Assert.Single(storage.Volumes);
        Assert.Equal(128_000_000_000, volume.TotalBytes);
        Assert.Equal(49085988L * 1024, volume.FreeBytes);
        var categories = volume.Categories.ToDictionary(c => c.Key, c => c.Bytes);
        Assert.Equal(["photos", "videos", "apps", "media", "system", "other"], volume.Categories.Select(c => c.Key));
        Assert.Equal(12884901888, categories["photos"]);
        Assert.Equal(8589934592, categories["videos"]);
        Assert.Equal(9663676416 + 4831838208 + 1073741824, categories["apps"]);
        Assert.Equal(536870912 + 1073741824, categories["media"]);
        Assert.Equal(128_000_000_000 - 110450884L * 1024, categories["system"]);
    }

    [Fact]
    public void Samsung_sd_card_is_a_second_volume_and_categories_are_empty_without_a_cache()
    {
        var set = FixtureSet.Synthetic("android-13-samsung-sm-a536b");
        var volumes = StorageBuilder.ParseVolumes(set.Read("ls-storage.txt"), set.Read("sm-list-volumes.txt"));
        Assert.Equal([new RemovableVolume("3A1F-12C9", IsUsb: false)], volumes);
        var storage = StorageBuilder.Build(DfParser.Parse(set.Read("df.txt")), DiskStatsParser.Parse(set.Read("dumpsys-diskstats.txt")), volumes);
        Assert.Empty(storage.Volumes[0].Categories);
        var sd = storage.Volumes[1];
        Assert.Equal("sd:3A1F-12C9", sd.Key);
        Assert.Equal("SD card", sd.Label);
        Assert.Equal(62513152L * 1024, sd.TotalBytes);
        Assert.Equal(41541632L * 1024, sd.FreeBytes);
    }

    [Fact]
    public void Battery_per_phone()
    {
        BatteryInfo Read(string name)
        {
            var set = FixtureSet.Synthetic(name);
            return BatteryParser.Parse(set.Read("dumpsys-battery.txt"), set.TryRead("battery-sysfs.txt"), set.TryRead("battery-sticky.txt"));
        }

        var xiaomi = Read("android-12-xiaomi-2201117tg");
        Assert.Equal(new BatteryInfo(78, true, 94, 187, 31.2, 5000, 4683), xiaomi);

        var samsung = Read("android-13-samsung-sm-a536b");
        Assert.Equal(57, samsung.LevelPercent);
        Assert.False(samsung.IsCharging);
        Assert.Null(samsung.CycleCount);
        Assert.Null(samsung.HealthPercent);

        Assert.Equal(412, Read("android-14-google-pixel-7").CycleCount);

        var oneplus = Read("android-15-oneplus-cph2581");
        Assert.True(oneplus.IsCharging); // full and plugged in
        Assert.Null(oneplus.CycleCount);   // HAL reports 0

        var pixel9 = Read("android-16-google-pixel-9");
        Assert.Equal(153, pixel9.CycleCount);
        Assert.Equal(97, pixel9.HealthPercent);
        Assert.Equal(64, pixel9.LevelPercent);
    }

    [Fact]
    public void Details_rows()
    {
        var set = FixtureSet.Synthetic("android-12-xiaomi-2201117tg");
        var details = DetailsBuilder.Build(Profile("android-12-xiaomi-2201117tg"), "a1b2c3d4", ShellScript.Split(set.Read("details-script.txt")));
        var rows = details.Rows.ToDictionary(r => r.Key);
        Assert.Equal("Redmi Note 11 (2201117TG)", rows["model"].Value);
        Assert.Equal("12 (API 31)", rows["androidVersion"].Value);
        Assert.Equal("a1b2c3d4", rows["serial"].Value);
        Assert.True(rows["serial"].Sensitive);
        Assert.False(rows.ContainsKey("imei")); // SecurityException parcel: left out, not shown empty
        Assert.Equal("9a:3c:51:aa:07:12", rows["wifiMac"].Value);
        Assert.Equal("4 GB", rows["ram"].Value);
        Assert.Equal("SKQ1.211103.001 release-keys", rows["build"].Value);
        Assert.Equal("4.19.157-perf-gb2c1e2f", rows["kernel"].Value);
    }

    [Fact]
    public void Samsung_mac_skips_the_placeholder_address()
    {
        var set = FixtureSet.Synthetic("android-13-samsung-sm-a536b");
        Assert.Equal("4e:91:0d:66:2c:b3", DetailsBuilder.ParseMac(ShellScript.Split(set.Read("details-script.txt"))["mac"]));
    }

    [Fact]
    public void Imei_row_appears_when_the_phone_answers()
    {
        var sections = new Dictionary<string, string> { ["imei"] = ParcelParserTests.ParcelFor("490154203237518") };
        var details = DetailsBuilder.Build(Profile("android-14-google-pixel-7"), "x", sections);
        var imei = details.Rows.Single(r => r.Key == "imei");
        Assert.Equal("490154203237518", imei.Value);
        Assert.True(imei.Sensitive);
    }

    [Fact]
    public void Wifi_serial_without_ro_serialno_has_no_serial_row()
    {
        var profile = new DeviceProfile(GetPropParser.Parse("[ro.product.model]: [X]\n"), null, TimeSpan.Zero);
        var details = DetailsBuilder.Build(profile, "192.168.1.42:5555", new Dictionary<string, string>());
        Assert.DoesNotContain(details.Rows, r => r.Key == "serial");
    }

    [Fact]
    public void Xiaomi_media_items()
    {
        var items = MediaQuery.Parse(FixtureSet.Synthetic("android-12-xiaomi-2201117tg").Read("content-query-media.txt"), 31);
        Assert.Equal([1003, 1002, 1001, 1004, 1005], items.Select(i => i.Id));
        var photo = items.Single(i => i.Id == 1001);
        Assert.Equal((3000, 4000), (photo.Width, photo.Height)); // orientation 90 swaps
        Assert.True(photo.IsFavorite);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1714546500000), photo.TakenAt);
        var video = items.Single(i => i.Id == 1002);
        Assert.True(video.IsVideo);
        Assert.Equal(15.433, video.DurationSeconds!.Value, 3);
        Assert.Equal(PhotoKind.Screenshot, items.Single(i => i.Id == 1003).ToAsset().Kind);
        var trip = items.Single(i => i.Id == 1004);
        Assert.Equal("Trip, Day 1, sunset.jpg", trip.FileName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1714000000), trip.TakenAt); // datetaken 0 falls back to date_modified
        Assert.Equal("عکس خانوادگی.heic", items.Single(i => i.Id == 1005).FileName);
    }

    [Fact]
    public void Samsung_albums_include_the_sd_card_camera_and_a_multi_line_name()
    {
        var items = MediaQuery.Parse(FixtureSet.Synthetic("android-13-samsung-sm-a536b").Read("content-query-media.txt"), 33);
        Assert.Equal(5, items.Count);
        Assert.Equal("note with\nnewline.png", items.Single(i => i.Id == 20005).FileName);
        var albums = MediaQuery.Albums(items);
        Assert.Equal("all", albums[0].Id);
        Assert.Equal(5, albums[0].Count);
        Assert.Contains(albums, a => a is { Id: "videos", Count: 1 });
        Assert.Contains(albums, a => a is { Id: "favorites", Count: 1 });
        Assert.Equal(2, albums.Count(a => a.Kind == AlbumKind.Camera)); // phone and SD card each have DCIM/Camera
        Assert.Contains(albums, a => a is { Kind: AlbumKind.Screenshots, Name: "Screenshots" });
        var video = items.Single(i => i.Id == 20002);
        Assert.Equal((1080, 1920), (video.Width, video.Height));
    }

    [Fact]
    public void Android_10_projection_has_no_favourites_column()
    {
        Assert.DoesNotContain("is_favorite", MediaQuery.Projection(29));
        Assert.Contains("is_favorite", MediaQuery.Projection(30));
    }

    [Fact]
    public void Package_details_per_android_version()
    {
        var a12 = DumpsysPackageParser.Parse(FixtureSet.Synthetic("android-12-xiaomi-2201117tg").Read("dumpsys-package.txt"));
        Assert.Equal(new DateTime(2021, 9, 2, 14, 5, 33), a12["com.whatsapp"].FirstInstallTime);
        Assert.Equal("114.0.5735.196", a12["com.android.chrome"].VersionName); // not the hidden factory copy
        Assert.Null(a12["ir.mservices.market"].Installer);

        var a13 = DumpsysPackageParser.Parse(FixtureSet.Synthetic("android-13-samsung-sm-a536b").Read("dumpsys-package.txt"));
        Assert.Equal(new DateTime(2022, 11, 20, 19, 45, 10), a13["com.whatsapp"].FirstInstallTime); // user 0, not user 95
        Assert.True(a13["com.samsung.android.app.notes"].IsSystem);
        Assert.False(a13["com.snapp.passenger"].IsSystem);

        var a14 = DumpsysPackageParser.Parse(FixtureSet.Synthetic("android-14-google-pixel-7").Read("dumpsys-package.txt"));
        Assert.Null(a14["com.example.sideloaded"].VersionName);
        Assert.Equal(2, a14.Count);
    }
}

public class InfoBuilderTests
{
    [Theory]
    [InlineData(110_000_000_000L, 128_000_000_000L)]
    [InlineData(117_000_000_000L, 128_000_000_000L)]
    [InlineData(128_000_000_000L, 128_000_000_000L)]
    [InlineData(250_000_000_000L, 256_000_000_000L)]
    [InlineData(493_000_000_000L, 512_000_000_000L)]
    [InlineData(1_000_000_000_000L, 1_000_000_000_000L)]
    [InlineData(52_000_000_000L, 64_000_000_000L)]
    public void Round_storage_size_like_android(long size, long expected)
    {
        Assert.Equal(expected, StorageBuilder.RoundStorageSize(size));
    }

    [Fact]
    public void Stale_categories_never_exceed_used_space()
    {
        var df = DfParser.Parse("Filesystem 1K-blocks Used Available Use% Mounted on\n/dev/x 100000000 10000000 90000000 10% /data\n");
        var stats = DiskStatsParser.Parse("Photos Size: 900000000000\nApp Size: 1\n");
        var volume = StorageBuilder.Build(df, stats, []).Volumes[0];
        long used = volume.TotalBytes - volume.FreeBytes;
        Assert.InRange(volume.Categories.Sum(c => c.Bytes), used - 1000, used);
    }

    [Fact]
    public void Usb_drives_are_told_apart_from_sd_cards()
    {
        var volumes = StorageBuilder.ParseVolumes("1234-ABCD\nemulated\nself\nB0B0-0001\n", "public:179,1 mounted 1234-ABCD\npublic:8,1 mounted B0B0-0001\n");
        Assert.Equal([new RemovableVolume("1234-ABCD", false), new RemovableVolume("B0B0-0001", true)], volumes);
        Assert.Equal("USB drive", volumes[1].Label);
    }

    [Fact]
    public void Missing_storage_size_is_an_error_not_zero()
    {
        var ex = Assert.Throws<DeviceException>(() => StorageBuilder.Build([], null, []));
        Assert.Equal(DeviceErrorCodes.Protocol, ex.Code);
    }

    [Theory]
    [InlineData("Obsidian", "#202124")]
    [InlineData("#a1b2c3", "#A1B2C3")]
    [InlineData("phantom_black", "#1C1C1E")]
    [InlineData("BLK00", "#1C1C1E")]
    [InlineData("Mint Green", "#B6E3C8")]
    [InlineData("xyz", null)]
    [InlineData("", null)]
    public void Colour_values(string value, string? hex)
    {
        Assert.Equal(hex, DeviceColor.FromValue(value));
    }

    [Theory]
    [InlineData("MemTotal:        3735424 kB", "4 GB")]
    [InlineData("MemTotal:        7697304 kB", "8 GB")]
    [InlineData("MemTotal:        5583308 kB", "6 GB")]
    [InlineData("MemTotal:        11756328 kB", "12 GB")]
    [InlineData("nothing", null)]
    public void Ram_is_rounded_to_retail_sizes(string meminfo, string? expected)
    {
        Assert.Equal(expected, DetailsBuilder.FormatRam(meminfo));
    }

    [Fact]
    public void Summary_states_from_adb()
    {
        var unauthorized = SummaryBuilder.FromEntry(new AdbDeviceEntry("R5CT", "unauthorized", TransportId: 3));
        Assert.Equal(DeviceState.Unauthorized, unauthorized.State);
        Assert.Equal(DeviceCapability.None, unauthorized.Capabilities);
        Assert.Equal(ConnectionKind.Usb, unauthorized.Connection);

        var wifi = SummaryBuilder.FromEntry(new AdbDeviceEntry("192.168.1.42:5555", "device", Model: "Pixel_7"));
        Assert.Equal(ConnectionKind.Wifi, wifi.Connection);
        Assert.Equal(DeviceState.Connecting, wifi.State);
        Assert.Equal("Pixel 7", wifi.Model);

        Assert.Equal(DeviceState.Connecting, SummaryBuilder.FromEntry(new AdbDeviceEntry("x", "offline")).State);
        Assert.True(SummaryBuilder.FromEntry(new AdbDeviceEntry("x", "recovery")).Capabilities.HasFlag(DeviceCapability.Reboot));

        var profile = new DeviceProfile(GetPropParser.Parse(FixtureSet.Synthetic("android-15-oneplus-cph2581").Read("getprop.txt")), null, TimeSpan.Zero);
        var ready = SummaryBuilder.Ready(new AdbDeviceEntry("192.168.1.42:5555", "device"), profile);
        Assert.Equal(DeviceState.Ready, ready.State);
        Assert.Equal("OnePlus 12", ready.Name);
        Assert.Equal("CPH2581", ready.Model);
        Assert.Equal("15", ready.OsVersion);
        Assert.Equal(DevicePlatform.Android, ready.Platform);
        Assert.Equal(DeviceMode.Full, ready.Mode);
        Assert.True(ready.Capabilities.HasFlag(DeviceCapability.AppDocuments)); // app folders: Android/media, Android/data, Documents, Download
        Assert.True(ready.Capabilities.HasFlag(DeviceCapability.PhotosImport));
    }
}
