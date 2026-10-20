using DeviceDeck.Android.Parsing;

namespace DeviceDeck.Android.Tests;

public class GetPropParserTests
{
    [Fact]
    public void Parses_simple_lines_and_ignores_noise()
    {
        var props = GetPropParser.Parse("[ro.product.model]: [Pixel 7]\r\n[ro.build.version.sdk]: [34]\nnot a prop line\n[empty]: []\n");
        Assert.Equal("Pixel 7", props["ro.product.model"]);
        Assert.Equal("34", props["ro.build.version.sdk"]);
        Assert.Equal("", props["empty"]);
        Assert.Equal(3, props.Count);
    }

    [Fact]
    public void Joins_a_value_that_spans_lines()
    {
        var props = GetPropParser.Parse("[a]: [1]\n[multi]: [first line\nsecond line\nthird]\n[b]: [2]\n");
        Assert.Equal("first line\nsecond line\nthird", props["multi"]);
        Assert.Equal("2", props["b"]);
    }

    [Fact]
    public void Keeps_brackets_and_colons_inside_values()
    {
        var props = GetPropParser.Parse("[ro.weird]: [a]: [b]\n[ro.url]: [http://x:80/]\n");
        Assert.Equal("a]: [b", props["ro.weird"]);
        Assert.Equal("http://x:80/", props["ro.url"]);
    }

    [Fact]
    public void First_returns_the_first_non_empty_key()
    {
        var props = GetPropParser.Parse("[a]: []\n[b]: [  x  ]\n");
        Assert.Equal("x", props.First("missing", "a", "b"));
        Assert.Null(props.First("missing"));
    }
}

public class DfParserTests
{
    [Fact]
    public void Parses_toybox_kilobyte_output()
    {
        var entries = DfParser.Parse("""
            Filesystem       1K-blocks     Used Available Use% Mounted on
            /dev/block/dm-8  110450884 61364896  49085988  56% /data
            /dev/fuse        110450884 61364896  49085988  56% /storage/emulated
            """);
        Assert.Equal(2, entries.Count);
        Assert.Equal("/data", entries[0].MountedOn);
        Assert.Equal(110450884L * 1024, entries[0].TotalBytes);
        Assert.Equal(49085988L * 1024, entries[0].AvailableBytes);
    }

    [Fact]
    public void Joins_a_wrapped_filesystem_name()
    {
        var entries = DfParser.Parse("""
            Filesystem 1K-blocks Used Available Use% Mounted on
            /dev/block/platform/soc/1d84000.ufshc/by-name/userdata
                       1000 400 600 40% /data
            """);
        var entry = Assert.Single(entries);
        Assert.Equal("/dev/block/platform/soc/1d84000.ufshc/by-name/userdata", entry.Filesystem);
        Assert.Equal(600 * 1024, entry.AvailableBytes);
    }

    [Fact]
    public void Understands_human_readable_sizes()
    {
        var entry = Assert.Single(DfParser.Parse("Filesystem Size Used Avail Use% Mounted on\n/dev/fuse 110G 58G 52G 53% /storage/emulated\n"));
        Assert.Equal(110L << 30, entry.TotalBytes);
        Assert.Equal(52L << 30, entry.AvailableBytes);
    }

    [Fact]
    public void Understands_old_toolbox_layout()
    {
        var entry = Assert.Single(DfParser.Parse("Filesystem               Size     Used     Free   Blksize\n/data                   25.0G    10.5G    14.5G   4096\n"));
        Assert.Equal("/data", entry.MountedOn);
        Assert.Equal((long)(14.5 * (1L << 30)), entry.AvailableBytes);
    }

    [Fact]
    public void Skips_error_lines()
    {
        Assert.Empty(DfParser.Parse("df: /storage/ABCD-1234: Permission denied\n"));
    }
}

public class DiskStatsParserTests
{
    [Fact]
    public void Reads_free_space_categories_and_packages()
    {
        var stats = DiskStatsParser.Parse("""
            Latency: 2ms [512B Data Write]
            Data-Free: 100K / 1000K total = 10% free
            Cache-Free: 5K / 9K total = 55% free
            App Size: 10
            Photos Size: 20
            Package Names: ["a.b","c.d"]
            App Sizes: [1,2]
            App Data Sizes: [3,4]
            Cache Sizes: [5,6]
            """);
        Assert.Equal(100 * 1024, stats.DataFreeBytes);
        Assert.Equal(1000 * 1024, stats.DataTotalBytes);
        Assert.Equal(10, stats.AppSize);
        Assert.Equal(20, stats.PhotosSize);
        Assert.True(stats.HasCategories);
        Assert.Equal(new PackageSize(2, 4, 6), stats.Packages["c.d"]);
    }

    [Fact]
    public void Missing_cache_means_no_categories()
    {
        var stats = DiskStatsParser.Parse("Data-Free: 100K / 1000K total = 10% free\nFile-based Encryption: true\n");
        Assert.False(stats.HasCategories);
        Assert.Empty(stats.Packages);
    }

    [Fact]
    public void Mismatched_arrays_are_ignored()
    {
        var stats = DiskStatsParser.Parse("Package Names: [\"a\",\"b\"]\nApp Sizes: [1]\n");
        Assert.Empty(stats.Packages);
    }
}

public class BatteryParserTests
{
    private const string Charging = """
        Current Battery Service state:
          AC powered: false
          USB powered: true
          status: 2
          level: 78
          scale: 100
          temperature: 312
        """;

    [Fact]
    public void Reads_level_charging_and_temperature()
    {
        var battery = BatteryParser.Parse(Charging);
        Assert.Equal(78, battery.LevelPercent);
        Assert.True(battery.IsCharging);
        Assert.Equal(31.2, battery.TemperatureCelsius!.Value, 3);
        Assert.Null(battery.CycleCount);
        Assert.Null(battery.HealthPercent);
    }

    [Fact]
    public void Full_and_plugged_counts_as_charging_but_full_unplugged_does_not()
    {
        Assert.True(BatteryParser.Parse("  AC powered: true\n  status: 5\n  level: 100\n").IsCharging);
        Assert.False(BatteryParser.Parse("  AC powered: false\n  USB powered: false\n  status: 5\n  level: 100\n").IsCharging);
    }

    [Fact]
    public void Scales_a_non_100_scale()
    {
        Assert.Equal(50, BatteryParser.Parse("  level: 128\n  scale: 256\n").LevelPercent);
    }

    [Fact]
    public void Health_and_capacity_come_from_sysfs_in_microamp_hours()
    {
        var battery = BatteryParser.Parse(Charging, "battery/cycle_count=187\nbattery/charge_full=4683000\nbattery/charge_full_design=5000000\n");
        Assert.Equal(187, battery.CycleCount);
        Assert.Equal(4683, battery.FullChargeCapacityMah);
        Assert.Equal(5000, battery.DesignCapacityMah);
        Assert.Equal(94, battery.HealthPercent);
    }

    [Fact]
    public void Sticky_cycle_count_wins_and_zero_means_unknown()
    {
        Assert.Equal(412, BatteryParser.Parse(Charging, "battery/cycle_count=5\n", "android.os.extra.CYCLE_COUNT=412\n").CycleCount);
        Assert.Equal(5, BatteryParser.Parse(Charging, "battery/cycle_count=5\n", "android.os.extra.CYCLE_COUNT=0\n").CycleCount);
        Assert.Null(BatteryParser.Parse(Charging, null, "android.os.extra.CYCLE_COUNT=0").CycleCount);
    }

    [Fact]
    public void Milliamp_hour_kernels_and_bms_fallback()
    {
        var battery = BatteryParser.Parse(Charging, "bms/charge_full=4000\nbms/charge_full_design=4500\n");
        Assert.Equal(4000, battery.FullChargeCapacityMah);
        Assert.Equal(89, battery.HealthPercent);
    }

    [Fact]
    public void First_occurrence_of_a_key_wins()
    {
        Assert.Equal(57, BatteryParser.Parse("  level: 57\n  scale: 100\n  level: 0\n").LevelPercent);
    }
}

public class PackageListParserTests
{
    [Fact]
    public void Splits_path_name_version_installer_and_uid()
    {
        var entry = Assert.Single(PackageListParser.Parse(
            "package:/data/app/~~aB3==/com.x-Yz==/base.apk=com.x versionCode:42  installer=com.android.vending uid:10234\n"));
        Assert.Equal("com.x", entry.PackageName);
        Assert.Equal("/data/app/~~aB3==/com.x-Yz==/base.apk", entry.SourceDir);
        Assert.Equal(42, entry.VersionCode);
        Assert.Equal("com.android.vending", entry.Installer);
        Assert.Equal(10234, entry.Uid);
    }

    [Fact]
    public void Handles_null_installer_multi_user_uids_and_bare_names()
    {
        var entries = PackageListParser.Parse("""
            package:/system/app/A/A.apk=com.a versionCode:1  installer=null uid:10001,1010001
            package:com.b
            garbage line
            """);
        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].Installer);
        Assert.Equal(10001, entries[0].Uid);
        Assert.Equal("com.b", entries[1].PackageName);
        Assert.Null(entries[1].SourceDir);
    }

    [Fact]
    public void Parses_pm_path_output()
    {
        var paths = PackageListParser.ParsePaths("package:/data/app/x/base.apk\npackage:/data/app/x/split_config.arm64_v8a.apk\n\n");
        Assert.Equal(["/data/app/x/base.apk", "/data/app/x/split_config.arm64_v8a.apk"], paths);
    }
}

public class DumpsysPackageParserTests
{
    [Fact]
    public void Reads_packages_and_stops_at_hidden_system_packages()
    {
        var packages = DumpsysPackageParser.Parse("""
            Packages:
              Package [com.android.chrome] (5b31e0d):
                codePath=/data/app/~~x==/com.android.chrome-y==
                versionCode=572815333 minSdk=29 targetSdk=33
                versionName=114.0.5735.196
                flags=[ SYSTEM HAS_CODE UPDATED_SYSTEM_APP ]
                lastUpdateTime=2023-07-01 03:12:50
                installerPackageName=com.android.vending
                User 0: ceDataInode=8192 installed=true
                  firstInstallTime=2009-01-01 03:30:00

            Hidden system packages:
              Package [com.android.chrome] (7cd1209):
                versionName=96.0.4664.104
            """);
        var chrome = Assert.Single(packages).Value;
        Assert.Equal("114.0.5735.196", chrome.VersionName);
        Assert.Equal(572815333, chrome.VersionCode);
        Assert.True(chrome.IsSystem);
        Assert.Equal(new DateTime(2009, 1, 1, 3, 30, 0), chrome.FirstInstallTime);
        Assert.Equal("com.android.vending", chrome.Installer);
        Assert.Equal("/data/app/~~x==/com.android.chrome-y==", chrome.CodePath);
    }

    [Fact]
    public void Epoch_install_times_are_unknown()
    {
        Assert.Null(DumpsysPackageParser.ParseTime("1970-01-01 03:30:00"));
        Assert.Null(DumpsysPackageParser.ParseTime("garbage"));
    }
}

public class FileListingParserTests
{
    [Fact]
    public void Stat_lines_keep_spaces_unicode_and_pipes_in_names()
    {
        var files = FileListingParser.ParseStat("""
            45f8|3452|1673328600|/sdcard//Telegram Files
            81b0|20480|1711823460|/sdcard//گزارش مالی ۱۴۰۳.pdf
            81b0|512|1715525400|/sdcard//a|b.txt
            nonsense
            """);
        Assert.Equal(3, files.Count);
        Assert.True(files[0].IsDirectory);
        Assert.Equal("Telegram Files", files[0].Name);
        Assert.Equal(0, files[0].Size);
        Assert.Equal("گزارش مالی ۱۴۰۳.pdf", files[1].Name);
        Assert.Equal(20480, files[1].Size);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1711823460), files[1].Modified);
        Assert.Equal("a|b.txt", files[2].Name);
    }

    [Fact]
    public void Stat_mode_without_write_bits_is_read_only()
    {
        var file = FileListingParser.TryParseStatLine("8124|10|1|/system/build.prop")!;
        Assert.True(file.ReadOnly);
        Assert.False(FileListingParser.TryParseStatLine("81b0|10|1|/sdcard/x")!.ReadOnly);
    }

    [Fact]
    public void Ls_handles_symlinks_spaces_unicode_and_skips_dot_entries()
    {
        var files = FileListingParser.ParseLs("""
            total 104
            drwxrws--- 14 u0_a181 media_rw 3452 2024-05-12 18:20 .
            drwx--x--x  4 root    root     3452 2023-01-10 09:00 ..
            drwxrws---  2 u0_a181 media_rw 3452 2023-11-30 12:00 Telegram Files
            -rw-rw----  1 u0_a181 media_rw 1048576 2024-04-02 07:15 My Notes (copy).txt
            -rw-rw----  1 u0_a197 media_rw     4096 2023-12-12 12:12 file with  two  spaces.txt
            -rw-rw----  1 u0_a197 media_rw      512 2023-12-12 12:12 trailing-arrow -> not a link.txt
            lrwxrwxrwx  1 root    root          23 2023-01-10 09:00 music link -> /sdcard/Music/Favorites
            crw-rw-rw-  1 root    root      1,   3 2023-01-10 09:00 null
            ls: ./secret: Permission denied
            """, TimeSpan.FromHours(3.5));
        Assert.Equal(6, files.Count);
        Assert.Equal("Telegram Files", files[0].Name);
        Assert.True(files[0].IsDirectory);
        Assert.Equal(1048576, files[1].Size);
        Assert.Equal(new DateTimeOffset(2024, 4, 2, 7, 15, 0, TimeSpan.FromHours(3.5)), files[1].Modified);
        Assert.Equal("file with  two  spaces.txt", files[2].Name);
        Assert.Equal("trailing-arrow -> not a link.txt", files[3].Name);
        Assert.Null(files[3].LinkTarget);
        Assert.Equal("music link", files[4].Name);
        Assert.True(files[4].IsSymlink);
        Assert.Equal("/sdcard/Music/Favorites", files[4].LinkTarget);
        Assert.Equal("null", files[5].Name);
    }

    [Fact]
    public void Ls_full_time_output_carries_its_own_zone()
    {
        var file = Assert.Single(FileListingParser.ParseLs(
            "-rw-rw---- 1 u0_a1 media_rw 10 2024-01-02 03:04:05.123456789 +0100 a.txt\n", TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2024, 1, 2, 3, 4, 5, 123, TimeSpan.FromHours(1)).AddTicks(4567), file.Modified);
    }

    [Theory]
    [InlineData("+0330", 3, 30)]
    [InlineData("-0500", -5, 0)]
    [InlineData("+0000", 0, 0)]
    public void Parses_date_zone(string text, int hours, int minutes)
    {
        Assert.True(FileListingParser.TryParseZone(text, out var offset));
        Assert.Equal(new TimeSpan(hours, hours < 0 ? -minutes : minutes, 0), offset);
    }
}

public class ContentQueryParserTests
{
    private static readonly string[] Projection = ["_id", "_display_name", "_data", "width"];

    [Fact]
    public void Values_may_contain_comma_space()
    {
        var row = Assert.Single(ContentQueryParser.Parse(
            "Row: 0 _id=1004, _display_name=Trip, Day 1, sunset.jpg, _data=/sdcard/Trip, Day 1, sunset.jpg, width=3000\n", Projection));
        Assert.Equal("Trip, Day 1, sunset.jpg", row["_display_name"]);
        Assert.Equal("/sdcard/Trip, Day 1, sunset.jpg", row["_data"]);
        Assert.Equal("3000", row["width"]);
    }

    [Fact]
    public void Null_and_newlines_in_values()
    {
        var rows = ContentQueryParser.Parse("Row: 0 _id=1, _display_name=two\nlines.png, _data=/a/two\nlines.png, width=NULL\nRow: 1 _id=2, _display_name=b, _data=/b, width=5\n", Projection);
        Assert.Equal(2, rows.Count);
        Assert.Equal("two\nlines.png", rows[0]["_display_name"]);
        Assert.Null(rows[0]["width"]);
        Assert.Equal("5", rows[1]["width"]);
    }

    [Fact]
    public void Other_layouts_and_no_results_give_nothing()
    {
        Assert.Empty(ContentQueryParser.Parse("No result found.\n", Projection));
        Assert.Empty(ContentQueryParser.Parse("Row: 0 other=1, _id=2\n", Projection));
    }
}

public class ParcelParserTests
{
    /// <summary>Builds `service call` output for a String16 the way Parcel's dump prints it.</summary>
    internal static string ParcelFor(string value)
    {
        var words = new List<uint> { 0, (uint)value.Length };
        string padded = value + "\0";
        if (padded.Length % 2 == 1)
        {
            padded += "\0";
        }
        for (int i = 0; i < padded.Length; i += 2)
        {
            words.Add(padded[i] | ((uint)padded[i + 1] << 16));
        }
        var lines = new List<string>();
        for (int i = 0; i < words.Count; i += 4)
        {
            lines.Add($"  0x{i * 4:x8}: " + string.Join(' ', words.Skip(i).Take(4).Select(w => w.ToString("x8"))) + " '....'");
        }
        return "Result: Parcel(\n" + string.Join('\n', lines) + ")\n";
    }

    [Fact]
    public void Reads_a_string16_reply()
    {
        Assert.Equal("490154203237518", ParcelParser.ReadString(ParcelFor("490154203237518")));
    }

    [Fact]
    public void A_security_exception_reply_gives_null()
    {
        Assert.Null(ParcelParser.ReadString("Result: Parcel(\n  0x00000000: ffffffff 0000004a 00650052 00750071 '....J...R.e.q.u.')\n"));
        Assert.Null(ParcelParser.ReadString("service: Service iphonesubinfo does not exist\n"));
        Assert.Null(ParcelParser.ReadString("Result: Parcel(00000000 ffffffff   '........')\n"));
    }

    [Theory]
    [InlineData("490154203237518", true)]
    [InlineData("490154203237519", false)]
    [InlineData("000000000000000", false)]
    [InlineData("49015420323751", false)]
    [InlineData("4901542032375a8", false)]
    public void Imei_check_digit(string value, bool valid)
    {
        Assert.Equal(valid, ParcelParser.IsValidImei(value));
    }
}

public class TrackDevicesParserTests
{
    [Fact]
    public void Parses_long_format_with_all_states()
    {
        var entries = TrackDevicesParser.Parse("""
            List of devices attached
            a1b2c3d4               device usb:1-1 product:spes_global model:2201117TG device:spes transport_id:1
            R5CT20ABCDE            unauthorized usb:2-1 transport_id:3
            192.168.1.42:5555      offline transport_id:4
            0123456789ABCDEF       no permissions (missing udev rules? user is in the plugdev group); see [http://developer.android.com/tools/device.html] usb:1-2 transport_id:9
            emulator-5554	device
            """);
        Assert.Equal(5, entries.Count);
        Assert.Equal(new AdbDeviceEntry("a1b2c3d4", "device", "spes_global", "2201117TG", "spes", 1, "1-1"), entries[0]);
        Assert.Equal("unauthorized", entries[1].State);
        Assert.Equal(3, entries[1].TransportId);
        Assert.Equal("offline", entries[2].State);
        Assert.Equal("no permissions", entries[3].State);
        Assert.Equal(9, entries[3].TransportId);
        Assert.Equal("emulator-5554", entries[4].Serial);
        Assert.Null(entries[4].TransportId);
    }

    [Theory]
    [InlineData("192.168.1.42:5555", true)]
    [InlineData("[fe80::1%wlan0]:5555", true)]
    [InlineData("adb-28031FDH200ABC-xYz1Ab._adb-tls-connect._tcp", true)]
    [InlineData("phone.local:5555", true)]
    [InlineData("a1b2c3d4", false)]
    [InlineData("emulator-5554", false)]
    [InlineData("R5CT20ABCDE", false)]
    public void Wifi_serials(string serial, bool wifi)
    {
        Assert.Equal(wifi, TrackDevicesParser.IsWifiSerial(serial));
    }
}

public class ShellTextTests
{
    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$HOME `x` \"q\" ; rm -rf /", "'$HOME `x` \"q\" ; rm -rf /'")]
    [InlineData("عکس.jpg", "'عکس.jpg'")]
    public void Quote_single_quotes_everything(string value, string expected)
    {
        Assert.Equal(expected, ShellQuote.Quote(value));
    }

    [Fact]
    public void Quote_rejects_nul()
    {
        Assert.Throws<ArgumentException>(() => ShellQuote.Quote("a\0b"));
    }

    [Fact]
    public void Sql_strings_double_quotes()
    {
        Assert.Equal("'it''s'", ShellQuote.SqlString("it's"));
    }

    [Fact]
    public void Script_round_trips_sections()
    {
        string script = ShellScript.Build(("a", "echo 1"), ("b", "false"));
        Assert.Contains("echo '@@dd:a@@'; { echo 1; } 2>/dev/null; ", script, StringComparison.Ordinal);
        Assert.EndsWith("true", script, StringComparison.Ordinal);

        var sections = ShellScript.Split("junk\n@@dd:a@@\n1\n2\n@@dd:b@@\n");
        Assert.Equal("1\n2\n", sections["a"]);
        Assert.Equal("", sections["b"]);
        Assert.False(sections.ContainsKey("junk"));
    }
}
