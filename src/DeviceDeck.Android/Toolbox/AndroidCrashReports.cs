using System.Globalization;
using System.Text.RegularExpressions;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Toolbox;

/// <summary>
/// Crash reports an Android phone hands to the shell user without root: the system drop box (`dumpsys dropbox`, which
/// keeps app crashes, ANRs, native crashes and tombstones), plus /data/tombstones and /data/anr when those folders are
/// readable (rooted or debug builds). Nothing here can be deleted from the PC.
/// </summary>
internal static partial class AndroidCrashReports
{
    public static string ListScript() => ShellScript.Build(
        ("dropbox", "dumpsys dropbox"),
        ("tombstones", "ls /data/tombstones 2>&1"),
        ("tombstat", "cd /data/tombstones && stat -c '%s %Y %n' *"),
        ("anr", "ls /data/anr 2>&1"),
        ("anrstat", "cd /data/anr && stat -c '%s %Y %n' *"));

    // "2026-10-19 21:14:03 data_app_crash (compressed text, 3121 bytes)"
    [GeneratedRegex(@"^(?<date>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) (?<tag>\S+) \((?<compressed>compressed )?(?<type>text|data), (?<size>\d+) bytes\)$")]
    private static partial Regex Entry();

    [GeneratedRegex(@"Process: (?<name>[^\s/]+)")]
    private static partial Regex ProcessLine();

    [GeneratedRegex(@">>> (?<name>\S+) <<<")]
    private static partial Regex TombstoneProcess();

    public static CrashReportList Parse(IReadOnlyDictionary<string, string> sections, TimeSpan phoneOffset)
    {
        var reports = new List<CrashReport>();
        reports.AddRange(ParseDropBox(sections.GetValueOrDefault("dropbox") ?? "", phoneOffset));

        var unreadable = new List<string>();
        foreach (var (folder, list, stat) in new[] { ("tombstones", "tombstones", "tombstat"), ("anr", "anr", "anrstat") })
        {
            var listing = sections.GetValueOrDefault(list) ?? "";
            if (listing.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            {
                unreadable.Add(folder);
                continue;
            }

            reports.AddRange(ParseStat(sections.GetValueOrDefault(stat) ?? "", folder));
        }

        return new CrashReportList(
            reports.OrderByDescending(r => r.Time ?? DateTimeOffset.MinValue).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(),
            CanDelete: false,
            unreadable);
    }

    /// <summary>Crash-like drop-box entries; the many routine ones (netstats, strict mode, …) are left out.</summary>
    public static List<CrashReport> ParseDropBox(string dump, TimeSpan phoneOffset)
    {
        var reports = new List<CrashReport>();
        var lines = dump.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Entry().Match(lines[i].TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }

            var tag = m.Groups["tag"].Value;
            if (KindOf(tag) is not { } kind)
            {
                continue;
            }

            // The next indented line is a 70-character preview of the text.
            var preview = i + 1 < lines.Length && lines[i + 1].StartsWith("    ", StringComparison.Ordinal) ? lines[i + 1].Trim() : "";
            var process = ProcessLine().Match(preview) is { Success: true } p ? p.Groups["name"].Value
                : TombstoneProcess().Match(preview) is { Success: true } t ? Path.GetFileName(t.Groups["name"].Value)
                : tag;
            var date = m.Groups["date"].Value;
            DateTimeOffset? time = DateTime.TryParseExact(date, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? new DateTimeOffset(parsed, phoneOffset)
                : null;
            var readable = m.Groups["type"].Value == "text";
            reports.Add(new CrashReport(
                $"dropbox/{tag}/{date}",
                $"{tag}-{date.Replace(":", "", StringComparison.Ordinal).Replace(' ', '-')}.txt",
                process,
                kind,
                time,
                long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
                readable));
        }

        return reports;
    }

    public static CrashReportKind? KindOf(string tag)
    {
        var lower = tag.ToLowerInvariant();
        if (lower.Contains("tombstone", StringComparison.Ordinal) || lower.Contains("native_crash", StringComparison.Ordinal))
        {
            return CrashReportKind.Tombstone;
        }

        if (lower is "system_server_crash" or "system_server_watchdog" or "system_restart" or "system_last_kmsg" or "system_server_wtf_crash"
            || lower.Contains("watchdog", StringComparison.Ordinal) || lower.Contains("kernel_panic", StringComparison.Ordinal))
        {
            return CrashReportKind.Panic;
        }

        if (lower.EndsWith("_crash", StringComparison.Ordinal))
        {
            return CrashReportKind.Crash;
        }

        if (lower.EndsWith("_anr", StringComparison.Ordinal))
        {
            return CrashReportKind.Hang;
        }

        return lower.Contains("lowmem", StringComparison.Ordinal) ? CrashReportKind.Memory : null;
    }

    /// <summary>"4096 1760896440 tombstone_00" lines from stat in /data/tombstones or /data/anr.</summary>
    public static List<CrashReport> ParseStat(string stat, string folder)
    {
        var reports = new List<CrashReport>();
        foreach (var raw in stat.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(' ', 3);
            if (parts.Length != 3 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var seconds) || parts[2] == "*")
            {
                continue;
            }

            var name = parts[2];
            reports.Add(new CrashReport(
                $"{folder}/{name}",
                name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? name : name + ".txt",
                name,
                folder == "anr" ? CrashReportKind.Hang : CrashReportKind.Tombstone,
                DateTimeOffset.FromUnixTimeSeconds(seconds),
                size,
                Readable: !name.EndsWith(".pb", StringComparison.OrdinalIgnoreCase)));
        }

        return reports;
    }

    /// <summary>The shell command that prints one report, or path_not_found for an id that isn't one of ours.</summary>
    public static string ReadCommand(string id)
    {
        var parts = id.Split('/', 3);
        if (parts is ["dropbox", var tag, var date] && TagOrDate().IsMatch(tag) && DateOnly().IsMatch(date))
        {
            return $"dumpsys dropbox --print {ShellQuote.Quote(date)} {ShellQuote.Quote(tag)}";
        }

        if (parts is [("tombstones" or "anr") and var folder, var name] && !name.Contains("..", StringComparison.Ordinal) && name.Length > 0)
        {
            return "cat " + ShellQuote.Quote($"/data/{folder}/{name}");
        }

        throw new DeviceException(DeviceErrorCodes.PathNotFound, "That crash report is no longer on the phone.");
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex TagOrDate();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d$")]
    private static partial Regex DateOnly();

    /// <summary>`dumpsys dropbox --print` output without its header: from the first "=====" line on.</summary>
    public static string? DropBoxBody(string output)
    {
        var start = output.IndexOf("========================================", StringComparison.Ordinal);
        return start < 0 ? null : output[start..];
    }
}
