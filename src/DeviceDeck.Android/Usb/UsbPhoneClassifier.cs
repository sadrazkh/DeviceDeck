using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Usb;

/// <summary>One USB device node as Windows lists it (a whole device, or one interface of a composite device).</summary>
/// <param name="InstanceId">"USB\VID_04E8&amp;PID_6860\R58N1234" (a device) or "USB\VID_04E8&amp;PID_6860&amp;MI_01\6&amp;2c2f&amp;0&amp;0001" (an interface).</param>
/// <param name="ParentInstanceId">The node above it: for an interface, its composite device.</param>
/// <param name="Name">Windows' friendly name or description ("Galaxy S23", "SAMSUNG Android ADB Interface").</param>
/// <param name="CompatibleIds">"USB\Class_FF&amp;SubClass_42&amp;Prot_01", "USB\MS_COMP_MTP"…</param>
/// <param name="HasProblem">Windows marks the node with a problem (most often: no driver installed).</param>
internal sealed record UsbNode(string InstanceId, string? ParentInstanceId, string? Name, IReadOnlyList<string> CompatibleIds, bool HasProblem);

/// <summary>
/// Which Android phones Windows sees that adb can't use yet, from Windows' USB device list. Best effort: it names a phone
/// only when it is sure (Apple's devices and unknown makers' cameras are never counted).
/// </summary>
internal static class UsbPhoneClassifier
{
    /// <summary>adb's USB interface: class 0xFF, subclass 0x42, protocol 0x01 (AOSP adb, usb_windows.cpp).</summary>
    public const string AdbInterface = @"USB\Class_FF&SubClass_42&Prot_01";

    /// <summary>What a phone with USB debugging off still shows Windows: MTP (Microsoft OS descriptor) or PTP (still image).</summary>
    private static readonly string[] MediaInterfaces = [@"USB\MS_COMP_MTP", @"USB\Class_06&SubClass_01&Prot_01"];

    private const string AppleVendor = "05AC";

    /// <summary>USB vendor ids of Android phone makers (usb.ids), for "USB debugging is off" and the maker's driver link.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Makers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["18D1"] = "google",
        ["04E8"] = "samsung",
        ["2717"] = "xiaomi",
        ["12D1"] = "huawei",
        ["339B"] = "honor",
        ["2A70"] = "oneplus",
        ["22D9"] = "oppo",
        ["2D95"] = "vivo",
        ["22B8"] = "motorola",
        ["1004"] = "lg",
        ["0FCE"] = "sony",
        ["0BB4"] = "htc",
        ["17EF"] = "lenovo",
        ["19D2"] = "zte",
        ["0B05"] = "asus",
        ["2E04"] = "nokia",
        ["0E8D"] = "mediatek",
        ["1782"] = "unisoc",
    };

    // Names Windows gives that say nothing about which phone it is.
    private static readonly string[] GenericNames =
    [
        "USB Composite Device", "MTP USB Device", "MTP", "USB Device", "Android", "ADB Interface", "Android ADB Interface",
        "Android Composite ADB Interface", "Android Bootloader Interface", "Unknown USB Device",
    ];

    /// <param name="adbUsbSerials">Serials of the USB phones adb lists right now (any state).</param>
    /// <param name="adbServerUp">Whether DeviceDeck's adb server answers (otherwise "adb doesn't see it" means nothing).</param>
    /// <param name="settled">Whether a device (by its key) has been in Windows' list long enough to count as "not seen by adb".</param>
    public static IReadOnlyList<DriverPhoneHint> Classify(IReadOnlyList<UsbNode> nodes, IReadOnlyCollection<string> adbUsbSerials, bool adbServerUp,
        Func<string, bool> settled)
    {
        var hints = new List<DriverPhoneHint>();
        foreach (var device in nodes.GroupBy(DeviceKey, StringComparer.OrdinalIgnoreCase))
        {
            string key = device.Key;
            string? vendor = VendorOf(key);
            if (vendor is null || vendor.Equals(AppleVendor, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? maker = Makers.GetValueOrDefault(vendor);
            var adb = device.Where(n => Has(n, AdbInterface)).ToList();
            var media = device.Where(n => MediaInterfaces.Any(m => Has(n, m))).ToList();
            string? name = BestName(media.Concat(device));

            if (adb.Any(n => n.HasProblem))
            {
                hints.Add(new DriverPhoneHint(name ?? "Android phone", DriverPhoneProblems.DriverMissing, maker));
            }
            else if (adb.Count > 0)
            {
                // Working adb interface: adb should list it. Only say it doesn't when adb lists no USB phone at all (serials
                // don't always match Windows' instance ids) and the phone has been there a while (adb takes a moment).
                string? serial = SerialOf(key);
                bool listed = serial is not null && adbUsbSerials.Contains(serial, StringComparer.OrdinalIgnoreCase);
                if (adbServerUp && !listed && adbUsbSerials.Count == 0 && settled(key))
                {
                    hints.Add(new DriverPhoneHint(name ?? "Android phone", DriverPhoneProblems.NotSeenByAdb, maker));
                }
            }
            else if (media.Count > 0 && maker is not null)
            {
                hints.Add(new DriverPhoneHint(name ?? "Android phone", DriverPhoneProblems.UsbDebuggingOff, maker));
            }
        }

        return hints;
    }

    /// <summary>The whole device a node belongs to: an interface's composite parent, else the node itself.</summary>
    internal static string DeviceKey(UsbNode node) =>
        IsInterface(node.InstanceId) && node.ParentInstanceId is { } parent && parent.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
            ? parent
            : node.InstanceId;

    /// <summary>The ids of the devices in <paramref name="nodes"/> that offer a working adb interface.</summary>
    public static IEnumerable<string> AdbDeviceKeys(IReadOnlyList<UsbNode> nodes) =>
        nodes.Where(n => Has(n, AdbInterface) && !n.HasProblem).Select(DeviceKey).Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsInterface(string instanceId) =>
        instanceId.Split('\\') is { Length: >= 2 } parts && parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase);

    /// <summary>"04E8" from "USB\VID_04E8&amp;PID_6860…".</summary>
    internal static string? VendorOf(string instanceId)
    {
        int at = instanceId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && at + 8 <= instanceId.Length ? instanceId.Substring(at + 4, 4) : null;
    }

    /// <summary>The USB serial number Windows put in a device's instance id; null when Windows made one up (it has '&amp;').</summary>
    internal static string? SerialOf(string deviceInstanceId)
    {
        var parts = deviceInstanceId.Split('\\');
        return parts.Length == 3 && parts[2].Length > 0 && !parts[2].Contains('&', StringComparison.Ordinal) ? parts[2] : null;
    }

    /// <summary>
    /// Whether a node lists a compatible id. A class id also matches Windows 11's vendor-qualified form:
    /// "USB\Class_FF&amp;SubClass_42&amp;Prot_01" is found in "USB\COMPAT_VID_04e8&amp;Class_ff&amp;SubClass_42&amp;Prot_01" too.
    /// </summary>
    private static bool Has(UsbNode node, string compatibleId) =>
        node.CompatibleIds.Any(id => id.Equals(compatibleId, StringComparison.OrdinalIgnoreCase) || SameClass(id, compatibleId));

    private static bool SameClass(string id, string classId)
    {
        const string prefix = @"USB\";
        if (!classId.StartsWith(prefix + "Class_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string triple = classId[prefix.Length..];
        return id.Length > triple.Length
            && id.EndsWith(triple, StringComparison.OrdinalIgnoreCase)
            && id[id.Length - triple.Length - 1] is '&' or '\\';
    }

    private static string? BestName(IEnumerable<UsbNode> nodes) =>
        nodes.Select(n => n.Name?.Trim())
            .FirstOrDefault(name => !string.IsNullOrEmpty(name)
                && !GenericNames.Any(g => name.Equals(g, StringComparison.OrdinalIgnoreCase))
                && !name.Contains("ADB Interface", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Composite", StringComparison.OrdinalIgnoreCase));
}
