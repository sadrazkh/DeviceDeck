using System.Globalization;

namespace DeviceDeck.Ios.Plist;

/// <summary>
/// A plist dictionary. Values are: string, long, ulong (only above long.MaxValue), double, bool,
/// DateTimeOffset, byte[], PlistDict, List&lt;object&gt;, PlistUid.
/// </summary>
public sealed class PlistDict : Dictionary<string, object>
{
    public PlistDict() : base(StringComparer.Ordinal) { }

    public PlistDict(IDictionary<string, object> source) : base(source, StringComparer.Ordinal) { }

    public string? GetString(string key) => TryGetValue(key, out var v) ? v as string : null;

    public long? GetLong(string key) => TryGetValue(key, out var v) ? PlistValue.AsLong(v) : null;

    public int? GetInt(string key)
    {
        var l = GetLong(key);
        return l is >= int.MinValue and <= int.MaxValue ? (int)l.Value : null;
    }

    public double? GetDouble(string key) => TryGetValue(key, out var v) ? PlistValue.AsDouble(v) : null;

    public bool? GetBool(string key) => TryGetValue(key, out var v) ? PlistValue.AsBool(v) : null;

    public byte[]? GetData(string key) => TryGetValue(key, out var v) ? v as byte[] : null;

    public PlistDict? GetDict(string key) => TryGetValue(key, out var v) ? v as PlistDict : null;

    public List<object>? GetArray(string key) => TryGetValue(key, out var v) ? v as List<object> : null;

    public DateTimeOffset? GetDate(string key) => TryGetValue(key, out var v) && v is DateTimeOffset d ? d : null;
}

/// <summary>A CF$UID reference (NSKeyedArchiver); only appears in binary plists.</summary>
public readonly record struct PlistUid(ulong Value);

public static class PlistValue
{
    public static long? AsLong(object? v) => v switch
    {
        long l => l,
        ulong u when u <= long.MaxValue => (long)u,
        int i => i,
        double d when d is >= long.MinValue and <= long.MaxValue && Math.Floor(d) == d => (long)d,
        bool b => b ? 1 : 0,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public static double? AsDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        ulong u => u,
        int i => i,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    public static bool? AsBool(object? v) => v switch
    {
        bool b => b,
        long l => l != 0,
        string s when bool.TryParse(s, out var p) => p,
        _ => null,
    };
}
