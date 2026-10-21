using System.Text.RegularExpressions;

namespace DeviceDeck.Bridge;

// Round 4, updates: release notes for "What's new", version order, and the only web pages the update UI opens.
// See docs/bridge-contract.md, "Updates you can see (round 4)".

/// <param name="Url">The version's GitHub release page.</param>
/// <param name="Title">The release's name on GitHub ("DeviceDeck v0.4.0"), when known.</param>
/// <param name="Markdown">The notes as written on GitHub (English, then a Persian &lt;details&gt; block), or null when they couldn't be had.</param>
/// <param name="Source">"github" (fetched, or unchanged since the cached copy), "cache" (GitHub unreachable: the copy from last time),
/// "app" (came with this version or with the downloaded update) or "none".</param>
public sealed record ReleaseNotes(string Version, string Url, string? Title, string? Markdown, DateTimeOffset? Published, string Source);

/// <summary>app.releaseNotes and app.whatsNewShown.</summary>
public sealed record UpdateVersionParams(string Version);

/// <summary>app.openReleasePage: a version's page, or the latest release without one.</summary>
public sealed record ReleasePageParams(string? Version = null);

public static partial class ReleaseLinks
{
    public const string Repository = "https://github.com/sadrazkh/DeviceDeck";
    public const string Releases = Repository + "/releases";

    /// <summary>MAJOR.MINOR.PATCH with an optional -prerelease, as in the release tags (without their "v").</summary>
    public static bool IsVersion(string? version) => version is { Length: <= 64 } && VersionPattern().IsMatch(version);

    /// <summary>https://github.com/sadrazkh/DeviceDeck/releases/tag/v{version}, or …/releases/latest.</summary>
    public static string ReleasePage(string? version) =>
        version is null ? Releases + "/latest"
        : IsVersion(version) ? $"{Releases}/tag/v{version}"
        : throw new ArgumentException($"\"{version}\" is not a DeviceDeck version.", nameof(version));

    /// <summary>Semantic version order: 0.10.0 after 0.9.1, 0.4.0-beta.2 after 0.4.0-beta.1 and before 0.4.0.</summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < 3; i++)
        {
            var order = coreA[i].CompareTo(coreB[i]);
            if (order != 0)
            {
                return order;
            }
        }

        if (preA.Length == 0 || preB.Length == 0)
        {
            // A release comes after its prereleases.
            return preB.Length.CompareTo(preA.Length);
        }

        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numericA = long.TryParse(preA[i], out var na);
            var numericB = long.TryParse(preB[i], out var nb);
            var order = (numericA, numericB) switch
            {
                (true, true) => na.CompareTo(nb),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i]),
            };
            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return preA.Length.CompareTo(preB.Length);
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    private static (long[] Core, string[] Pre) Split(string version)
    {
        if (!IsVersion(version))
        {
            throw new ArgumentException($"\"{version}\" is not a DeviceDeck version.", nameof(version));
        }

        var dash = version.IndexOf('-');
        var core = (dash < 0 ? version : version[..dash]).Split('.').Select(long.Parse).ToArray();
        var pre = dash < 0 ? [] : version[(dash + 1)..].Split('.');
        return (core, pre);
    }

    [GeneratedRegex(@"^\d{1,9}\.\d{1,9}\.\d{1,9}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex VersionPattern();
}
