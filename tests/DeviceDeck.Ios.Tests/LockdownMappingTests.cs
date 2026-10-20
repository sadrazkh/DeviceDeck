using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Tests;

public class LockdownMappingTests
{
    private static PlistDict Load(string name) => PlistSerializer.ParseDict(Fixture.Bytes("Plist", name));

    [Fact]
    public void Summary_from_GetValue_all()
    {
        var s = LockdownMapping.Summary("00008140-000A1B2C3D4E801E", Load("lockdown_getvalue_all.bplist"), ConnectionKind.Usb, DeviceState.Ready);

        Assert.Equal("00008140-000A1B2C3D4E801E", s.Id);
        Assert.Equal("Test iPhone", s.Name);
        Assert.Equal(DevicePlatform.Ios, s.Platform);
        Assert.Equal("iPhone17,3", s.Model);
        Assert.Equal("iPhone 16", s.MarketingName);
        Assert.Equal("18.0", s.OsVersion);
        Assert.Equal("#e3e4df", s.ColorHex);
        Assert.Equal(DeviceMode.Full, s.Mode);
        Assert.True(s.Capabilities.HasFlag(DeviceCapability.PhotosRead | DeviceCapability.AppDocuments | DeviceCapability.Reboot));
        Assert.False(s.Capabilities.HasFlag(DeviceCapability.PhotosDelete));
        Assert.False(s.Capabilities.HasFlag(DeviceCapability.PhotosImport));
        Assert.False(s.Capabilities.HasFlag(DeviceCapability.Screenshot));
    }

    [Theory]
    [InlineData(DeviceState.Connecting, DeviceCapability.None)]
    [InlineData(DeviceState.AwaitingTrust, DeviceCapability.None)]
    [InlineData(DeviceState.Locked, DeviceCapability.Info)]
    public void Capabilities_follow_the_state(DeviceState state, DeviceCapability expected) =>
        Assert.Equal(expected, LockdownMapping.CapabilitiesFor(state));

    [Fact]
    public void Summary_before_pairing_uses_what_is_there()
    {
        var s = LockdownMapping.Summary("X", new PlistDict { ["ProductType"] = "iPhone15,2" }, ConnectionKind.Wifi, DeviceState.AwaitingTrust);
        Assert.Equal("iPhone 14 Pro", s.Name);
        Assert.Equal("", s.OsVersion);
        Assert.Null(s.ColorHex);
        Assert.Equal(ConnectionKind.Wifi, s.Connection);
    }

    [Fact]
    public void Storage_by_category()
    {
        var info = LockdownMapping.Storage(Load("disk_usage.xml"));
        var volume = Assert.Single(info.Volumes);

        Assert.Equal(256_000_000_000, volume.TotalBytes);
        Assert.Equal(98_000_000_000, volume.FreeBytes);
        var c = volume.Categories.ToDictionary(x => x.Key, x => x.Bytes);
        Assert.Equal(44_000_000_000, c["photos"]);   // camera roll + photo library
        Assert.Equal(62_000_000_000, c["apps"]);
        Assert.Equal(1_500_000_000, c["media"]);
        Assert.Equal(10_000_000_000, c["system"]);   // system capacity − system available
        Assert.Equal(256_000_000_000 - 98_000_000_000 - 44_000_000_000 - 62_000_000_000 - 1_500_000_000 - 10_000_000_000, c["other"]);
        Assert.Equal(volume.TotalBytes - volume.FreeBytes, volume.Categories.Sum(x => x.Bytes));
        Assert.All(volume.Categories, x => Assert.Contains(x.Key, new[] { "photos", "videos", "apps", "media", "system", "other" }));
    }

    [Fact]
    public void Storage_on_ios27_counts_the_camera_roll_once_and_takes_apps_and_videos_from_outside()
    {
        var usage = Load("disk_usage_ios27.xml");

        var merged = LockdownMapping.Storage(usage).Volumes[0].Categories.ToDictionary(x => x.Key, x => x.Bytes);
        Assert.Equal(26_000_000_000, merged["photos"]); // not 52 GB: CameraUsage == PhotoUsage
        Assert.False(merged.ContainsKey("apps"));

        var v = LockdownMapping.Storage(usage, appBytes: 36_800_000_000, videoBytes: 16_500_000_000).Volumes[0];
        var c = v.Categories.ToDictionary(x => x.Key, x => x.Bytes);
        Assert.Equal(4_300_000_000, v.FreeBytes); // AmountDataAvailable, not TotalDataAvailable
        Assert.Equal(9_500_000_000, c["photos"]);
        Assert.Equal(16_500_000_000, c["videos"]);
        Assert.Equal(36_800_000_000, c["apps"]);
        Assert.Equal(8_800_000_000, c["system"]);
        Assert.Equal(v.TotalBytes - v.FreeBytes, v.Categories.Sum(x => x.Bytes));
        Assert.Equal(["photos", "videos", "apps", "media", "system", "other"], v.Categories.Select(x => x.Key));
    }

    [Fact]
    public void Storage_never_reports_more_video_than_camera_roll()
    {
        var v = LockdownMapping.Storage(Load("disk_usage_ios27.xml"), videoBytes: 99_000_000_000).Volumes[0];
        var c = v.Categories.ToDictionary(x => x.Key, x => x.Bytes);
        Assert.Equal(26_000_000_000, c["videos"]);
        Assert.False(c.ContainsKey("photos"));
    }

    [Fact]
    public void Storage_without_category_keys_is_all_other()
    {
        var info = LockdownMapping.Storage(new PlistDict { ["TotalDiskCapacity"] = 64_000_000_000L, ["AmountDataAvailable"] = 4_000_000_000L });
        var other = Assert.Single(info.Volumes[0].Categories);
        Assert.Equal(("other", 60_000_000_000L), (other.Key, other.Bytes));
    }

    [Fact]
    public void Battery_from_IORegistry_and_battery_domain()
    {
        var b = LockdownMapping.Battery(Load("ioreg_powersource.xml"), Load("battery_domain.xml"));

        Assert.Equal(76, b.LevelPercent);
        Assert.True(b.IsCharging);
        Assert.Equal(412, b.CycleCount);
        Assert.Equal(3561, b.DesignCapacityMah);
        Assert.Equal(3279, b.FullChargeCapacityMah);
        Assert.Equal(92, b.HealthPercent);          // 3279 / 3561
        Assert.Equal(30.5, b.TemperatureCelsius);
    }

    [Fact]
    public void Battery_on_ios27_reads_capacities_from_BatteryData_and_has_no_temperature()
    {
        var b = LockdownMapping.Battery(Load("ioreg_powersource_ios27.xml"), new PlistDict());
        Assert.Equal(88, b.LevelPercent);
        Assert.Equal(617, b.CycleCount);
        Assert.Equal(3521, b.DesignCapacityMah);
        Assert.NotNull(b.FullChargeCapacityMah);
        Assert.NotNull(b.HealthPercent);
        Assert.Null(b.TemperatureCelsius);
    }

    [Fact]
    public void Battery_without_IORegistry_still_has_level()
    {
        var b = LockdownMapping.Battery(null, Load("battery_domain.xml"));
        Assert.Equal(76, b.LevelPercent);
        Assert.Null(b.CycleCount);
        Assert.Null(b.HealthPercent);
    }

    [Fact]
    public void Battery_older_phone_reports_capacity_in_MaxCapacity()
    {
        var b = LockdownMapping.Battery(new PlistDict
        {
            ["CurrentCapacity"] = 1500L,
            ["MaxCapacity"] = 1800L,
            ["DesignCapacity"] = 1960L,
            ["CycleCount"] = 800L,
        }, new PlistDict());
        Assert.Equal(83, b.LevelPercent);
        Assert.Equal(1800, b.FullChargeCapacityMah);
        Assert.Equal(92, b.HealthPercent);
    }

    [Fact]
    public void Details_rows_mark_identifiers_sensitive()
    {
        var details = LockdownMapping.Details("00008140-000A1B2C3D4E801E", Load("lockdown_getvalue_all.xml"));
        var rows = details.Rows.ToDictionary(r => r.Key);

        Assert.Equal("F2LXK000N70F", rows["serial"].Value);
        Assert.True(rows["serial"].Sensitive);
        Assert.True(rows["imei"].Sensitive);
        Assert.Equal("356000000000001", rows["imei"].Value);
        Assert.True(rows["imei2"].Sensitive);
        Assert.False(rows["udid"].Sensitive);
        Assert.Equal("00008140-000A1B2C3D4E801E", rows["udid"].Value);
        Assert.Equal("a4:c3:61:00:11:23", rows["wifi"].Value);
        Assert.Equal("a4:c3:61:00:11:22", rows["bluetooth"].Value);
        Assert.Equal("Irancell (IR) · 432/35", rows["carrier"].Value);
        Assert.Equal("18.0 (22A3354)", rows["ios"].Value);
        Assert.Equal("iPhone 16 · iPhone17,3 · MYE73ZD/A", rows["model"].Value);
        Assert.True(rows["phone"].Sensitive);
    }

    [Fact]
    public void Carrier_unknown_bundle_uses_the_mcc_mnc_table_and_merges_identical_sims()
    {
        var values = Load("carrier_unknown_dual_sim.xml");
        Assert.Equal("MCI (IR) · 432/11 (2 SIMs)", LockdownMapping.Carrier(values));

        var mixed = new PlistDict
        {
            ["CarrierBundleInfoArray"] = new List<object>
            {
                new PlistDict { ["CFBundleIdentifier"] = "com.apple.Irancell_ir", ["MCC"] = "432", ["MNC"] = "35" },
                new PlistDict { ["CFBundleIdentifier"] = "com.apple.Unknown", ["MCC"] = "432", ["MNC"] = "11" },
                new PlistDict { ["CFBundleIdentifier"] = "com.apple.Unknown", ["MCC"] = "999", ["MNC"] = "99" },
            },
        };
        Assert.Equal("Irancell (IR) · 432/35, MCI (IR) · 432/11, 999/99", LockdownMapping.Carrier(mixed));
    }

    [Theory]
    [InlineData("iPhone10,3", "#E1E4E3", "#e1e4e3")]
    [InlineData("iPhone17,3", "#zzzzzz", null)]
    [InlineData("iPhone99,1", "18", null)]
    [InlineData(null, null, null)]
    public void Colour_from_enclosure_colour(string? productType, string? enclosure, string? expected) =>
        Assert.Equal(expected, DeviceColors.Hex(productType, enclosure));

    [Theory]
    [InlineData("iPhone15,2", "iPhone 14 Pro")]
    [InlineData("iPhone17,5", "iPhone 16e")]
    [InlineData("iPhone12,8", "iPhone SE (2nd generation)")]
    [InlineData("iPad13,1", "iPad")]
    [InlineData("iPhone99,9", null)]
    public void Marketing_names(string productType, string? expected) =>
        Assert.Equal(expected, DeviceModels.MarketingName(productType));
}
