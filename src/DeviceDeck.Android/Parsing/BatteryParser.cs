using System.Globalization;
using System.Text.RegularExpressions;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Parsing;

/// <summary>
/// Builds <see cref="BatteryInfo"/> from three sources:
/// `dumpsys battery` (BatteryService: level, status, plugged, temperature in tenths of a degree),
/// the sticky ACTION_BATTERY_CHANGED extras (android.os.extra.CYCLE_COUNT, Android 14+), and
/// power_supply sysfs files when the phone lets the shell read them (cycle_count, charge_full, charge_full_design in µAh).
/// </summary>
internal static partial class BatteryParser
{
    // BatteryManager.BATTERY_STATUS_*
    private const int StatusCharging = 2;
    private const int StatusFull = 5;

    /// <summary>One shell line that prints "dir/file=value" for the sysfs files that exist and are readable.</summary>
    public const string SysfsCommand =
        "for d in battery bms; do for f in cycle_count battery_cycle charge_full charge_full_design; do " +
        "v=$(cat /sys/class/power_supply/$d/$f 2>/dev/null) && echo \"$d/$f=$v\"; done; done";

    /// <summary>Pulls the cycle count out of the sticky BATTERY_CHANGED intent (Android 14+).</summary>
    public const string StickyCycleCountCommand =
        "dumpsys activity broadcasts | grep -o 'android.os.extra.CYCLE_COUNT=[0-9]*' | head -n 1";

    public static BatteryInfo Parse(string dumpsysBattery, string? sysfs = null, string? sticky = null)
    {
        var values = ParseKeyValues(dumpsysBattery);

        int? level = Int(values, "level");
        int? scale = Int(values, "scale");
        if (level != null && scale is > 0 and not 100)
        {
            level = (int)Math.Round(level.Value * 100.0 / scale.Value);
        }

        int? status = Int(values, "status");
        bool plugged = Bool(values, "AC powered") || Bool(values, "USB powered") || Bool(values, "Wireless powered") || Bool(values, "Dock powered");
        bool charging = status == StatusCharging || (status == StatusFull && plugged);

        double? temperature = Int(values, "temperature") is { } tenths ? tenths / 10.0 : null;

        var files = ParseSysfs(sysfs);
        int? cycles = StickyCycleCount(sticky)
            ?? PositiveInt(files, "battery/cycle_count") ?? PositiveInt(files, "battery/battery_cycle")
            ?? PositiveInt(files, "bms/cycle_count");

        // charge_full / charge_full_design are µAh (some kernels report mAh; values under 100000 are taken as mAh).
        int? fullMah = Mah(PositiveLong(files, "battery/charge_full") ?? PositiveLong(files, "bms/charge_full"));
        int? designMah = Mah(PositiveLong(files, "battery/charge_full_design") ?? PositiveLong(files, "bms/charge_full_design"));
        int? health = null;
        if (fullMah != null && designMah is > 0)
        {
            int percent = (int)Math.Round(fullMah.Value * 100.0 / designMah.Value);
            health = percent is >= 1 and <= 130 ? Math.Min(percent, 100) : null;
        }

        return new BatteryInfo(
            level is >= 0 and <= 100 ? level : null,
            charging,
            health,
            cycles,
            temperature,
            designMah,
            fullMah);
    }

    /// <summary>"  key: value" lines (indented) from dumpsys.</summary>
    internal static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string key = line[..colon].Trim();
            // The first occurrence wins; Samsung appends a second block with other meanings for some keys.
            values.TryAdd(key, line[(colon + 1)..].Trim());
        }
        return values;
    }

    internal static Dictionary<string, string> ParseSysfs(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text == null)
        {
            return values;
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int equals = line.IndexOf('=');
            if (equals > 0)
            {
                values.TryAdd(line[..equals], line[(equals + 1)..].Trim());
            }
        }
        return values;
    }

    [GeneratedRegex(@"android\.os\.extra\.CYCLE_COUNT=(?<n>\d+)")]
    private static partial Regex StickyCycle();

    private static int? StickyCycleCount(string? sticky)
    {
        if (sticky == null)
        {
            return null;
        }
        var match = StickyCycle().Match(sticky);
        // -1 / 0 mean "unknown" on phones whose HAL doesn't report it.
        return match.Success && int.TryParse(match.Groups["n"].Value, out int n) && n > 0 ? n : null;
    }

    private static int? Mah(long? value) => value switch
    {
        null => null,
        < 100_000 => (int)value.Value,
        _ => (int)(value.Value / 1000),
    };

    private static int? Int(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;

    private static int? PositiveInt(Dictionary<string, string> values, string key) =>
        Int(values, key) is > 0 and var n ? n : null;

    private static long? PositiveLong(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n > 0 ? n : null;

    private static bool Bool(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
}
