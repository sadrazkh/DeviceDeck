using DeviceDeck.Android.Usb;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

/// <summary>Which phones Windows sees that adb can't use, from Windows' USB list (shapes as Windows reports them).</summary>
public sealed class UsbPhoneClassifierTests
{
    private const string Adb = UsbPhoneClassifier.AdbInterface;
    private const string Mtp = @"USB\MS_COMP_MTP";
    private const string Ptp = @"USB\Class_06&SubClass_01&Prot_01";

    private static UsbNode Device(string id, string? name = "USB Composite Device") => new(id, @"USB\ROOT_HUB30\4&1&0&0", name, [], false);

    private static UsbNode Interface(string id, string parent, string? name, string compatible, bool problem = false) =>
        new(id, parent, name, [compatible, @"USB\COMPOSITE"], problem);

    private static IReadOnlyList<DriverPhoneHint> Classify(IReadOnlyList<UsbNode> nodes, string[]? adbSerials = null, bool serverUp = true, bool settled = true) =>
        UsbPhoneClassifier.Classify(nodes, adbSerials ?? [], serverUp, _ => settled);

    [Fact]
    public void A_samsung_whose_adb_interface_has_no_driver_needs_the_makers_usb_driver()
    {
        const string phone = @"USB\VID_04E8&PID_6860\R58N12345";
        var hint = Assert.Single(Classify(
        [
            Device(phone),
            Interface(@"USB\VID_04E8&PID_6860&MI_00\6&2C2F1E6E&0&0000", phone, "Galaxy S23", Mtp),
            Interface(@"USB\VID_04E8&PID_6860&MI_01\6&2C2F1E6E&0&0001", phone, "SAMSUNG Android", Adb, problem: true),
        ]));
        Assert.Equal(new DriverPhoneHint("Galaxy S23", DriverPhoneProblems.DriverMissing, "samsung"), hint);
    }

    [Fact]
    public void A_pixel_showing_only_mtp_has_usb_debugging_off()
    {
        const string phone = @"USB\VID_18D1&PID_4EE1\2A281FDH2004YY";
        var hint = Assert.Single(Classify([Device(phone, "Pixel 7")with { CompatibleIds = [Mtp] }]));
        Assert.Equal(new DriverPhoneHint("Pixel 7", DriverPhoneProblems.UsbDebuggingOff, "google"), hint);
    }

    [Fact]
    public void Iphones_cameras_and_other_usb_devices_are_never_counted()
    {
        Assert.Empty(Classify(
        [
            Device(@"USB\VID_05AC&PID_12A8\00008140001601C02178801C") with { CompatibleIds = [Ptp] },
            Device(@"USB\VID_04A9&PID_32D1\CANONEOS") with { CompatibleIds = [Ptp] }, // a camera
            Device(@"USB\VID_046D&PID_C52B\5&1A2B&0&3", "Logitech USB Input Device"),
        ]));
    }

    [Fact]
    public void A_working_adb_interface_adb_doesnt_list_is_said_only_once_it_has_settled_and_the_server_is_up()
    {
        const string phone = @"USB\VID_2717&PID_FF48\a1b2c3d4";
        UsbNode[] nodes =
        [
            Device(phone),
            Interface(@"USB\VID_2717&PID_FF48&MI_00\7&1&0&0000", phone, "Redmi Note 11", Mtp),
            Interface(@"USB\VID_2717&PID_FF48&MI_01\7&1&0&0001", phone, "ADB Interface", Adb),
        ];
        Assert.Equal(DriverPhoneProblems.NotSeenByAdb, Assert.Single(Classify(nodes)).Problem);
        Assert.Empty(Classify(nodes, settled: false)); // adb takes a moment after Windows
        Assert.Empty(Classify(nodes, serverUp: false)); // the card says the server's state instead
        Assert.Empty(Classify(nodes, adbSerials: ["A1B2C3D4"])); // listed (serials compare without case)
        Assert.Empty(Classify(nodes, adbSerials: ["some-other-serial"])); // adb lists a phone: serials may not match, so no claim
    }

    [Fact]
    public void An_unknown_maker_with_a_broken_adb_interface_still_counts()
    {
        const string phone = @"USB\VID_ABCD&PID_0001\SER123";
        var hint = Assert.Single(Classify([Device(phone), Interface(@"USB\VID_ABCD&PID_0001&MI_01\8&1&0&0001", phone, null, Adb, problem: true)]));
        Assert.Equal(new DriverPhoneHint("Android phone", DriverPhoneProblems.DriverMissing, null), hint);
    }

    [Fact]
    public void Windows_11s_vendor_qualified_class_ids_count_and_lookalikes_do_not()
    {
        // Windows 11 lists "USB\COMPAT_VID_04e8&Class_ff&SubClass_42&Prot_01" (seen on this PC for other devices).
        const string phone = @"USB\VID_04E8&PID_6860\R58N12345";
        var hint = Assert.Single(Classify(
        [
            Device(phone),
            new UsbNode(@"USB\VID_04E8&PID_6860&MI_01\6&1&0&0001", phone, "SAMSUNG Android", [@"USB\COMPAT_VID_04e8&Class_ff&SubClass_42&Prot_01"], true),
        ]));
        Assert.Equal(DriverPhoneProblems.DriverMissing, hint.Problem);

        // "SubClass_42…" alone, or another protocol, isn't adb.
        Assert.Empty(Classify(
        [
            Device(phone),
            new UsbNode(@"USB\VID_04E8&PID_6860&MI_01\6&1&0&0001", phone, "Modem", [@"USB\COMPAT_VID_04e8&Class_ff&SubClass_42&Prot_02", @"USB\XClass_ff&SubClass_42&Prot_01"], true),
        ]));
    }

    [Theory]
    [InlineData(@"USB\VID_04E8&PID_6860\R58N12345", "R58N12345")]
    [InlineData(@"USB\VID_04E8&PID_6860\6&2C2F1E6E&0&3", null)]
    [InlineData(@"USB\VID_04E8&PID_6860&MI_01\6&2C2F1E6E&0&0001", null)]
    public void The_usb_serial_is_read_from_a_devices_instance_id(string id, string? serial) =>
        Assert.Equal(serial, UsbPhoneClassifier.SerialOf(id));
}
