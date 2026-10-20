using System.Globalization;
using System.Text.RegularExpressions;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Info;

internal static partial class DetailsBuilder
{
    /// <summary>
    /// One round trip for the rows getprop doesn't have. The IMEI call needs READ_PRIVILEGED_PHONE_STATE on
    /// Android 10+, which the shell lacks on stock builds: it answers with a SecurityException parcel and the row is left out.
    /// </summary>
    public static string Script() => ShellScript.Build(
        ("meminfo", "grep MemTotal /proc/meminfo"),
        ("kernel", "uname -r"),
        ("mac", "cat /sys/class/net/wlan0/address; ip link show wlan0"),
        ("imei", "service call iphonesubinfo 1 s16 com.android.shell"));

    public static DeviceDetails Build(DeviceProfile profile, string adbSerial, IReadOnlyDictionary<string, string> sections)
    {
        var rows = new List<DetailRow>();
        void Add(string key, string label, string? value, bool sensitive = false)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new DetailRow(key, label, value.Trim(), sensitive));
            }
        }

        Add("model", "Model", profile.MarketingName is { } marketing ? $"{marketing} ({profile.Model})" : profile.Model);
        Add("manufacturer", "Manufacturer", profile.Manufacturer);
        Add("androidVersion", "Android version", profile.Sdk > 0 ? $"{profile.AndroidVersion} (API {profile.Sdk})" : profile.AndroidVersion);
        Add("securityPatch", "Security patch", profile.SecurityPatch);
        Add("build", "Build number", profile.BuildId);
        Add("serial", "Serial number", profile.Serial ?? (TrackDevicesParser.IsWifiSerial(adbSerial) ? null : adbSerial), sensitive: true);

        string? imei = ParcelParser.ReadString(sections.GetValueOrDefault("imei") ?? "");
        if (ParcelParser.IsValidImei(imei))
        {
            Add("imei", "IMEI", imei, sensitive: true);
        }
        Add("wifiMac", "Wi-Fi MAC address", ParseMac(sections.GetValueOrDefault("mac")), sensitive: true);
        Add("chipset", "Chipset", profile.Chipset);
        Add("ram", "RAM", FormatRam(sections.GetValueOrDefault("meminfo")));
        Add("cpuAbi", "CPU architecture", profile.CpuAbi);
        Add("baseband", "Baseband", profile.Baseband);
        Add("kernel", "Kernel", sections.GetValueOrDefault("kernel")?.Trim());
        Add("fingerprint", "Build fingerprint", profile.Fingerprint);
        return new DeviceDetails(rows);
    }

    [GeneratedRegex(@"\b(?<mac>[0-9a-fA-F]{2}(?::[0-9a-fA-F]{2}){5})\b")]
    private static partial Regex Mac();

    /// <summary>
    /// First real MAC in the sysfs/ip output. Android 10+ reports 02:00:00:00:00:00 to callers without
    /// LOCAL_MAC_ADDRESS; with MAC randomisation on, this is the address used on the current network.
    /// </summary>
    public static string? ParseMac(string? text)
    {
        if (text == null)
        {
            return null;
        }
        foreach (Match match in Mac().Matches(text))
        {
            string mac = match.Groups["mac"].Value.ToLowerInvariant();
            if (mac is "02:00:00:00:00:00" or "00:00:00:00:00:00" or "ff:ff:ff:ff:ff:ff")
            {
                continue;
            }
            return mac;
        }
        return null;
    }

    /// <summary>MemTotal (kB, what the kernel can use) rounded up to the sizes phones are sold with.</summary>
    public static string? FormatRam(string? meminfo)
    {
        var match = Regex.Match(meminfo ?? "", @"MemTotal:\s*(\d+)\s*kB");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb))
        {
            return null;
        }
        double gib = kb / 1024.0 / 1024.0;
        double[] sizes = [1, 1.5, 2, 3, 4, 6, 8, 10, 12, 16, 18, 20, 24, 32, 64];
        double rounded = sizes.FirstOrDefault(s => s >= gib, Math.Ceiling(gib));
        return rounded.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }
}
