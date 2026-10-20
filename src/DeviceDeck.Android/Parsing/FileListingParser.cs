using System.Globalization;
using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Parsing;

/// <param name="Name">Name only, as listed (spaces and Unicode kept).</param>
/// <param name="LinkTarget">For symlinks listed by `ls -la`: what the link points at.</param>
internal sealed record RemoteFile(
    string Name,
    bool IsDirectory,
    bool IsSymlink,
    long Size,
    DateTimeOffset? Modified,
    bool ReadOnly,
    string? LinkTarget = null);

/// <summary>
/// Directory listings from the phone's shell. Two formats:
/// stat: `stat -L -c '%f|%s|%Y|%n'` — raw mode in hex, size, mtime in epoch seconds, then the name (may hold '|');
/// ls:   toybox `ls -la` — "drwxrwx--x 4 root sdcard_rw 3488 2024-03-01 10:22 Name with spaces", symlinks as "a -> b".
/// </summary>
internal static partial class FileListingParser
{
    // stat(2) st_mode bits.
    private const int TypeMask = 0xF000;
    private const int TypeDirectory = 0x4000;
    private const int TypeSymlink = 0xA000;
    private const int AnyWrite = 0x92; // 0222

    /// <summary>The command <see cref="ParseStat"/> reads, for every entry of <paramref name="quotedDir"/> (which must end with '/').</summary>
    public static string StatListCommand(string quotedDir) =>
        $"find {quotedDir} -mindepth 1 -maxdepth 1 -exec stat -L -c '%f|%s|%Y|%n' {{}} +";

    public static string StatOneCommand(string quotedPath) => $"stat -L -c '%f|%s|%Y|%n' -- {quotedPath}";

    public static IReadOnlyList<RemoteFile> ParseStat(string text)
    {
        var files = new List<RemoteFile>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (TryParseStatLine(line) is { } file)
            {
                files.Add(file);
            }
        }
        return files;
    }

    public static RemoteFile? TryParseStatLine(string line)
    {
        string[] parts = line.Split('|', 4);
        if (parts.Length < 4
            || !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int mode)
            || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            return null;
        }
        string path = parts[3];
        string name = BaseName(path);
        if (name is "" or "." or "..")
        {
            return null;
        }
        bool directory = (mode & TypeMask) == TypeDirectory;
        return new RemoteFile(
            name,
            directory,
            (mode & TypeMask) == TypeSymlink,
            directory ? 0 : size,
            epoch > 0 ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null,
            (mode & AnyWrite) == 0);
    }

    [GeneratedRegex(
        @"^(?<mode>[-dlcbps][-rwxsStT]{9})[.+@]?\s+(?<links>\d+|\?)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+|\d+,\s*\d+|\?)\s+" +
        @"(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?)(?:\s+(?<zone>[+-]\d{4}))? (?<name>.+)$")]
    private static partial Regex LsLine();

    /// <summary>
    /// toybox `ls -la` (Android 6+). Dates are the phone's local time without zone, so <paramref name="phoneOffset"/>
    /// (from `date +%z`) turns them into instants; `--full-time` output carries its own zone.
    /// </summary>
    public static IReadOnlyList<RemoteFile> ParseLs(string text, TimeSpan phoneOffset)
    {
        var files = new List<RemoteFile>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            var match = LsLine().Match(line);
            if (!match.Success)
            {
                continue; // "total 52", "ls: x: Permission denied"
            }
            string mode = match.Groups["mode"].Value;
            string name = match.Groups["name"].Value;
            string? target = null;
            bool symlink = mode[0] == 'l';
            if (symlink)
            {
                int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    target = name[(arrow + 4)..];
                    name = name[..arrow];
                }
            }
            if (name is "." or "..")
            {
                continue;
            }

            long size = long.TryParse(match.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
            bool directory = mode[0] == 'd';
            files.Add(new RemoteFile(
                name,
                directory,
                symlink,
                directory || symlink ? 0 : size,
                ParseLsTime(match, phoneOffset),
                !mode.AsSpan(1).ContainsAny('w', 'W'),
                target));
        }
        return files;
    }

    private static DateTimeOffset? ParseLsTime(Match match, TimeSpan phoneOffset)
    {
        string stamp = match.Groups["date"].Value + " " + match.Groups["time"].Value;
        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF"];
        string time = match.Groups["time"].Value;
        if (time.Length > 16 && time.Contains('.'))
        {
            // Nanoseconds don't fit .NET's 7 digits.
            int dot = stamp.IndexOf('.');
            stamp = stamp[..Math.Min(stamp.Length, dot + 8)];
        }
        if (!DateTime.TryParseExact(stamp, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }
        TimeSpan offset = phoneOffset;
        if (match.Groups["zone"].Success && TryParseZone(match.Groups["zone"].Value, out var zone))
        {
            offset = zone;
        }
        return new DateTimeOffset(local, offset);
    }

    /// <summary>"+0330" / "-0500" (from `date +%z`).</summary>
    public static bool TryParseZone(string text, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        text = text.Trim();
        if (text.Length != 5 || (text[0] != '+' && text[0] != '-')
            || !int.TryParse(text.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
        {
            return false;
        }
        offset = new TimeSpan(hours, minutes, 0);
        if (text[0] == '-')
        {
            offset = -offset;
        }
        return true;
    }

    private static string BaseName(string path)
    {
        string trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
