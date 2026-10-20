using System.Globalization;

namespace DeviceDeck.Android.Parsing;

/// <param name="FirstInstallTime">Local phone time "yyyy-MM-dd HH:mm:ss" (package level up to Android 12, under "User 0:" from 13).</param>
/// <param name="IsSystem">flags=[ SYSTEM ... ] — preinstalled, including updated system apps.</param>
internal sealed record DumpsysPackage(
    string PackageName,
    string? VersionName,
    long? VersionCode,
    string? CodePath,
    DateTime? FirstInstallTime,
    DateTime? LastUpdateTime,
    string? Installer,
    bool IsSystem);

/// <summary>
/// Parses the "Packages:" section of `dumpsys package packages` (Settings.dumpPackageLPr):
/// "  Package [com.x] (1a2b3c):" then "    key=value" lines. Stops at the next top-level section
/// ("Hidden system packages:" repeats updated system apps with their factory versions).
/// </summary>
internal static class DumpsysPackageParser
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public static IReadOnlyDictionary<string, DumpsysPackage> Parse(string text)
    {
        var result = new Dictionary<string, DumpsysPackage>(StringComparer.Ordinal);
        bool inPackages = false;
        Builder? current = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            bool topLevel = !char.IsWhiteSpace(line[0]);
            if (topLevel)
            {
                Flush();
                inPackages = line.StartsWith("Packages:", StringComparison.Ordinal);
                continue;
            }
            if (!inPackages)
            {
                continue;
            }

            string trimmed = line.Trim();
            if (trimmed.StartsWith("Package [", StringComparison.Ordinal))
            {
                Flush();
                int close = trimmed.IndexOf(']');
                if (close > "Package [".Length)
                {
                    current = new Builder(trimmed["Package [".Length..close]);
                }
                continue;
            }
            current?.Read(trimmed);
        }
        Flush();
        return result;

        void Flush()
        {
            if (current != null)
            {
                result.TryAdd(current.Name, current.Build());
                current = null;
            }
        }
    }

    internal static DateTime? ParseTime(string value) =>
        DateTime.TryParseExact(value.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        && time.Year > 1971 ? time : null;

    private sealed class Builder(string name)
    {
        public string Name { get; } = name;
        private string? _versionName;
        private long? _versionCode;
        private string? _codePath;
        private DateTime? _firstInstall;
        private DateTime? _lastUpdate;
        private string? _installer;
        private bool _system;

        public void Read(string line)
        {
            if (line.StartsWith("versionCode=", StringComparison.Ordinal))
            {
                // "versionCode=1546038208 minSdk=26 targetSdk=34"
                string value = line["versionCode=".Length..].Split(' ')[0];
                _versionCode = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long code) ? code : null;
            }
            else if (TryValue(line, "versionName=", out string? versionName))
            {
                _versionName ??= versionName == "null" ? null : versionName;
            }
            else if (TryValue(line, "codePath=", out string? codePath))
            {
                _codePath ??= codePath;
            }
            else if (TryValue(line, "firstInstallTime=", out string? first))
            {
                // Several users each print one; keep the first (user 0 comes first).
                _firstInstall ??= ParseTime(first);
            }
            else if (TryValue(line, "lastUpdateTime=", out string? last))
            {
                _lastUpdate ??= ParseTime(last);
            }
            else if (TryValue(line, "installerPackageName=", out string? installer))
            {
                _installer ??= installer == "null" ? null : installer;
            }
            else if (line.StartsWith("flags=[", StringComparison.Ordinal) || line.StartsWith("pkgFlags=[", StringComparison.Ordinal))
            {
                _system |= line.Contains(" SYSTEM ", StringComparison.Ordinal) || line.EndsWith(" SYSTEM]", StringComparison.Ordinal);
            }
        }

        public DumpsysPackage Build() =>
            new(Name, _versionName, _versionCode, _codePath, _firstInstall, _lastUpdate, _installer, _system);

        private static bool TryValue(string line, string prefix, out string value)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = line[prefix.Length..].Trim();
                return true;
            }
            value = "";
            return false;
        }
    }
}
