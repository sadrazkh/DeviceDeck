using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Imaging;

namespace DeviceDeck.Core.Photos;

/// <summary>Date folders ("yyyy/MM" → 2026\10) and file names for a backup, keeping a Live Photo's two files under one name.</summary>
public static partial class PhotoBackupNaming
{
    public const string DefaultPattern = "yyyy/MM";

    /// <summary>Photos without any date (rare: the phone keeps one for nearly everything).</summary>
    public const string UndatedFolder = "Undated";

    /// <summary>The choices the UI offers; any mix of yyyy, MM and dd joined by / - _ . or a space is accepted.</summary>
    public static readonly IReadOnlyList<string> Presets = ["yyyy/MM", "yyyy/MM/dd", "yyyy-MM", "yyyy", ""];

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".3gp", ".mkv", ".webm", ".avi",
    };

    [GeneratedRegex(@"^(yyyy|MM|dd)([/\-_. ](yyyy|MM|dd))*$")]
    private static partial Regex PatternShape();

    /// <summary>"" (one folder) or tokens yyyy, MM, dd with separators; '/' starts a sub-folder.</summary>
    public static bool IsValidPattern(string? pattern) => pattern is not null && (pattern.Length == 0 || PatternShape().IsMatch(pattern));

    public static bool IsVideoName(string fileName) => VideoExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>The folder for a photo taken at <paramref name="takenAt"/> (shown in this PC's time zone).</summary>
    public static string Folder(string root, string pattern, DateTimeOffset? takenAt)
    {
        if (pattern.Length == 0)
        {
            return root;
        }

        if (takenAt is not { } when)
        {
            return Path.Combine(root, UndatedFolder);
        }

        var local = when.ToLocalTime();
        var text = new StringBuilder(pattern)
            .Replace("yyyy", local.Year.ToString("D4", CultureInfo.InvariantCulture))
            .Replace("MM", local.Month.ToString("D2", CultureInfo.InvariantCulture))
            .Replace("dd", local.Day.ToString("D2", CultureInfo.InvariantCulture))
            .ToString();
        return Path.Combine([root, .. text.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
    }

    /// <summary>The name the still gets on the PC: the phone's name made safe, ".jpg" when HEIC is converted.</summary>
    public static string StillName(string fileName, bool convertHeic)
    {
        var name = SafeName(fileName);
        return convertHeic && ImageFormats.IsHeic(name) ? Path.ChangeExtension(name, ".jpg") : name;
    }

    /// <summary>The motion half sits next to the still under the same stem: IMG_0001.HEIC + IMG_0001.MOV.</summary>
    public static string MotionPath(string stillPath, string motionFileName) =>
        Path.ChangeExtension(stillPath, Path.GetExtension(motionFileName) is { Length: > 0 } ext ? ext : ".MOV");

    /// <summary>
    /// Destinations for a batch, before anything is written: two photos with one name in one folder (IMG_0001 comes
    /// round again) get "IMG_0001 (1)", and both files of a Live Photo take the same stem. With <paramref name="hiddenRoot"/>,
    /// photos in the iPhone's Hidden album go under it instead, in the same date folders ("Hidden\2026\10", "Hidden\Undated").
    /// </summary>
    public static IReadOnlyList<(string Still, string? Motion)> Plan(IReadOnlyList<PhotoBackupAsset> assets, string root, string pattern, bool convertHeic,
        string? hiddenRoot = null)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<(string, string?)>(assets.Count);
        foreach (var asset in assets)
        {
            var folder = Folder(asset.Hidden && hiddenRoot is not null ? hiddenRoot : root, pattern, asset.TakenAt);
            var name = StillName(asset.FileName, convertHeic);
            var (still, motion) = FreeStem(folder, name, asset.Motion?.FileName, path => !taken.Contains(path));
            taken.Add(still);
            if (motion is not null)
            {
                taken.Add(motion);
            }

            plans.Add((still, motion));
        }

        return plans;
    }

    /// <summary>"name", "name (1)", … the first stem for which the still (and the motion half) are both free.</summary>
    public static (string Still, string? Motion) FreeStem(string folder, string stillName, string? motionName, Func<string, bool> isFree)
    {
        var stem = Path.GetFileNameWithoutExtension(stillName);
        var ext = Path.GetExtension(stillName);
        for (var n = 0; ; n++)
        {
            var still = Path.Combine(folder, n == 0 ? stillName : $"{stem} ({n}){ext}");
            var motion = motionName is null ? null : MotionPath(still, motionName);
            if (isFree(still) && (motion is null || isFree(motion)))
            {
                return (still, motion);
            }
        }
    }

    /// <summary>A phone file name made safe for Windows: no reserved characters or names, no trailing dot or space.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (safe.Length == 0)
        {
            safe = "_";
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        return stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" || ReservedDevice().IsMatch(stem) ? "_" + safe : safe;
    }

    [GeneratedRegex(@"^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDevice();
}
