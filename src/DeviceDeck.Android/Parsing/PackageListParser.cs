using System.Globalization;

namespace DeviceDeck.Android.Parsing;

/// <param name="SourceDir">Path of base.apk; null when the listing had no -f.</param>
/// <param name="Installer">Package that installed it (com.android.vending, ...); null for "null" or no -i.</param>
internal sealed record PackageListEntry(string PackageName, string? SourceDir, long? VersionCode, string? Installer, int? Uid);

/// <summary>
/// Parses `pm list packages -f -U -i --show-versioncode` (PackageManagerShellCommand.runListPackages):
/// "package:/data/app/~~aB3==/com.x-Yz==/base.apk=com.x versionCode:42  installer=com.android.vending uid:10234".
/// The path may contain '=' (base64 folder names), so the name is what follows the last '=' before the first space.
/// </summary>
internal static class PackageListParser
{
    public static IReadOnlyList<PackageListEntry> Parse(string text)
    {
        var entries = new List<PackageListEntry>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal))
            {
                continue;
            }
            line = line["package:".Length..];

            int space = line.IndexOf(' ');
            string head = space < 0 ? line : line[..space];
            string tail = space < 0 ? "" : line[space..];

            int equals = head.LastIndexOf('=');
            string? sourceDir = equals >= 0 ? head[..equals] : null;
            string name = equals >= 0 ? head[(equals + 1)..] : head;
            if (name.Length == 0)
            {
                continue;
            }

            long? versionCode = null;
            string? installer = null;
            int? uid = null;
            foreach (string token in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("versionCode:", StringComparison.Ordinal)
                    && long.TryParse(token["versionCode:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long code))
                {
                    versionCode = code;
                }
                else if (token.StartsWith("installer=", StringComparison.Ordinal))
                {
                    string value = token["installer=".Length..];
                    installer = value is "" or "null" ? null : value;
                }
                else if (token.StartsWith("uid:", StringComparison.Ordinal))
                {
                    // Several users give "uid:10234,1010234"; the first is the owner's.
                    string first = token["uid:".Length..].Split(',')[0];
                    uid = int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int u) ? u : null;
                }
            }
            entries.Add(new PackageListEntry(name, sourceDir, versionCode, installer, uid));
        }
        return entries;
    }

    /// <summary>`pm path pkg`: "package:/data/app/.../base.apk" per line (base first, then splits).</summary>
    public static IReadOnlyList<string> ParsePaths(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => l["package:".Length..])
            .Where(p => p.Length > 0)
            .ToList();
}
