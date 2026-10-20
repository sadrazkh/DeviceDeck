using System.Globalization;

namespace IosProbe;

/// <summary>Plain-text output helpers.</summary>
internal static class Out
{
    public static void Table(string[] headers, IEnumerable<string[]> rows)
    {
        var all = rows.ToList();
        if (all.Count == 0)
        {
            Console.WriteLine("  (none)");
            return;
        }
        var widths = headers.Select((h, i) => Math.Max(h.Length, all.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();
        Console.WriteLine("  " + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        Console.WriteLine("  " + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var r in all)
            Console.WriteLine("  " + string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
    }

    public static void Pairs(IEnumerable<(string Label, string Value)> pairs)
    {
        var list = pairs.ToList();
        var width = list.Count == 0 ? 0 : list.Max(p => p.Label.Length);
        foreach (var (label, value) in list)
            Console.WriteLine($"  {label.PadRight(width)} : {value}");
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var u = 0;
        while (v >= 1000 && u < units.Length - 1)
        {
            v /= 1000;
            u++;
        }
        return u == 0 ? $"{bytes} B" : v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }

    public static string Rate(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0 ? "-" : Bytes((long)(bytes / elapsed.TotalSeconds)) + "/s";

    /// <summary>"356000000000001" → "35•••••••••••01".</summary>
    public static string Mask(string value) =>
        value.Length <= 4 ? new string('•', value.Length) : value[..2] + new string('•', value.Length - 4) + value[^2..];

    /// <summary>A one-line progress meter on stderr; total may be unknown.</summary>
    public static IProgress<long> Progress(long? total)
    {
        var last = DateTime.MinValue;
        return new InlineProgress<long>(done =>
        {
            if ((DateTime.UtcNow - last).TotalMilliseconds < 200 && done != total)
                return;
            last = DateTime.UtcNow;
            Console.Error.Write(total is > 0
                ? $"\r  {Bytes(done)} of {Bytes(total.Value)} ({100.0 * done / total.Value:0}%)   "
                : $"\r  {Bytes(done)}   ");
        });
    }

    public static void EndProgress() => Console.Error.WriteLine();
}
