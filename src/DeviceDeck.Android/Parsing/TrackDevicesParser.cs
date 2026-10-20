using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Parsing;

/// <summary>One line of `adb devices -l` / host:track-devices-l.</summary>
/// <param name="State">adb's word: device, unauthorized, authorizing, offline, connecting, recovery, sideload, bootloader, no permissions, ...</param>
internal sealed record AdbDeviceEntry(
    string Serial,
    string State,
    string? Product = null,
    string? Model = null,
    string? DeviceName = null,
    long? TransportId = null,
    string? Usb = null);

internal static partial class TrackDevicesParser
{
    // Properties adb appends after the state in the long format.
    [GeneratedRegex(@"^(usb|product|model|device|transport_id|features):\S*$")]
    private static partial Regex PropertyToken();

    public static IReadOnlyList<AdbDeviceEntry> Parse(string text)
    {
        var entries = new List<AdbDeviceEntry>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
            {
                continue;
            }
            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            // Properties sit at the end; the state ("no permissions (...)" has spaces) is what's between.
            int firstProperty = tokens.Length;
            while (firstProperty > 2 && PropertyToken().IsMatch(tokens[firstProperty - 1]))
            {
                firstProperty--;
            }
            string state = string.Join(' ', tokens[1..firstProperty]);
            if (state.StartsWith("no permissions", StringComparison.Ordinal))
            {
                state = "no permissions";
            }

            var properties = new Dictionary<string, string>();
            foreach (string token in tokens[firstProperty..])
            {
                int colon = token.IndexOf(':');
                properties[token[..colon]] = token[(colon + 1)..];
            }
            entries.Add(new AdbDeviceEntry(
                tokens[0],
                state,
                properties.GetValueOrDefault("product"),
                properties.GetValueOrDefault("model"),
                properties.GetValueOrDefault("device"),
                long.TryParse(properties.GetValueOrDefault("transport_id"), out long id) ? id : null,
                properties.GetValueOrDefault("usb")));
        }
        return entries;
    }

    [GeneratedRegex(@"^(\d{1,3}(\.\d{1,3}){3}|\[[0-9A-Fa-f:.%a-zA-Z]+\]|[A-Za-z0-9.-]+\.[A-Za-z]{2,}):\d{1,5}$")]
    private static partial Regex HostPortSerial();

    /// <summary>ip:port (adb connect / adb tcpip) and mDNS names (Wireless debugging on Android 11+) are Wi-Fi.</summary>
    public static bool IsWifiSerial(string serial) =>
        HostPortSerial().IsMatch(serial)
        || serial.Contains("._adb-tls-connect._tcp", StringComparison.Ordinal)
        || serial.Contains("._adb._tcp", StringComparison.Ordinal);
}
