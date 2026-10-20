using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace DeviceDeck.Core.AppFiles;

/// <summary>
/// The root keys of Android app folders, each a folder of the phone's shared storage opened as a root of its own:
/// "appmedia:&lt;package&gt;" = Android/media/&lt;package&gt;, "appdata:&lt;package&gt;" = Android/data/&lt;package&gt;,
/// "shared:Documents" and "shared:Download".
/// </summary>
public static partial class AppFilesRootKeys
{
    public const string MediaPrefix = "appmedia:";
    public const string DataPrefix = "appdata:";
    public const string SharedPrefix = "shared:";

    /// <summary>The shared folders apps read and write without asking, in the order the UI lists them.</summary>
    public static readonly IReadOnlyList<string> SharedFolders = ["Documents", "Download"];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageName();

    public static bool IsPackageName(string? name) => name is not null && name.Length <= 255 && PackageName().IsMatch(name);

    /// <summary>The folder a key stands for, as path segments under the storage root ("Android", "media", "com.whatsapp").</summary>
    public static bool TryParse(string rootKey, [NotNullWhen(true)] out string[]? segments)
    {
        segments = null;
        if (rootKey.StartsWith(MediaPrefix, StringComparison.Ordinal) && IsPackageName(rootKey[MediaPrefix.Length..]))
        {
            segments = ["Android", "media", rootKey[MediaPrefix.Length..]];
        }
        else if (rootKey.StartsWith(DataPrefix, StringComparison.Ordinal) && IsPackageName(rootKey[DataPrefix.Length..]))
        {
            segments = ["Android", "data", rootKey[DataPrefix.Length..]];
        }
        else if (rootKey.StartsWith(SharedPrefix, StringComparison.Ordinal) && SharedFolders.Contains(rootKey[SharedPrefix.Length..], StringComparer.Ordinal))
        {
            segments = [rootKey[SharedPrefix.Length..]];
        }

        return segments is not null;
    }
}
