using System.Text.RegularExpressions;

namespace DeviceDeck.Core.Photos;

/// <summary>
/// Which photos of an iPhone's Hidden album a backup takes: none of them (the default), them as well as the others, or
/// only them. A phone without a Hidden album (Android) always backs up as <see cref="Exclude"/>.
/// </summary>
public enum PhotoBackupHidden { Exclude, Include, Only }

/// <summary>
/// Where hidden photos go when a backup takes them: a folder of their own under the destination, dated inside the same
/// way (<c>Hidden\2026\10\IMG_1234.HEIC</c>, <c>Hidden\Undated\…</c>), or into the same date folders as the others.
/// </summary>
public enum PhotoBackupHiddenLayout { Separate, Together }

/// <summary>
/// The rules of the hidden-photo options. A photo's tree is decided once, when it is first copied: hiding or unhiding it
/// on the phone later, or changing these options, never copies it again and never moves a file on the PC.
/// </summary>
public static partial class PhotoBackupHiddenRules
{
    /// <summary>The hidden photos' folder unless the user names another ("پنهان" is the Persian suggestion).</summary>
    public const string DefaultFolder = "Hidden";

    public const int MaxFolderLength = 64;

    /// <summary>True when a photo (hidden on the phone or not) is one the options back up.</summary>
    public static bool Wants(PhotoBackupHidden mode, bool hidden) => mode switch
    {
        PhotoBackupHidden.Include => true,
        PhotoBackupHidden.Only => hidden,
        _ => !hidden,
    };

    /// <summary>The folder hidden photos go under (their date folders inside it), or null when they go with the others.</summary>
    public static string? HiddenRoot(string destination, PhotoBackupHidden mode, PhotoBackupHiddenLayout layout, string? folder) =>
        mode != PhotoBackupHidden.Exclude && layout == PhotoBackupHiddenLayout.Separate
            ? Path.Combine(destination, string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder)
            : null;

    /// <summary>The old on/off switch: on mixed hidden photos in with the others.</summary>
    public static (PhotoBackupHidden Mode, PhotoBackupHiddenLayout? Layout) FromIncludeHidden(bool includeHidden) =>
        includeHidden ? (PhotoBackupHidden.Include, PhotoBackupHiddenLayout.Together) : (PhotoBackupHidden.Exclude, null);

    /// <summary>
    /// The name trimmed, when it is one folder name Windows takes and no date folder could be called: not empty, at most
    /// <see cref="MaxFolderLength"/> characters, no \ / : * ? " &lt; &gt; | or control characters, no direction marks,
    /// not "." or "..", no trailing dot, not a reserved device name (CON, NUL, COM1…), not "Undated", not only digits
    /// and the pattern's separators (it would sit among the year folders). Throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string ValidateFolder(string? name)
    {
        if (Problem(name) is { } problem)
        {
            throw new ArgumentException($"hiddenFolder {problem}.");
        }

        return name!.Trim();
    }

    public static bool IsValidFolder(string? name) => Problem(name) is null;

    private static string? Problem(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return "must be a folder name";
        }

        if (trimmed.Length > MaxFolderLength)
        {
            return $"must be at most {MaxFolderLength} characters";
        }

        if (trimmed.IndexOfAny(['\\', '/']) >= 0)
        {
            return "must be one folder name, without \\ or /";
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Any(c => char.IsControl(c) || IsDirectionMark(c)))
        {
            return "must not contain : * ? \" < > | or control characters";
        }

        if (trimmed is "." or ".." || trimmed.EndsWith('.'))
        {
            return "must not end with a dot";
        }

        var stem = trimmed.Split('.')[0].TrimEnd();
        if (stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" || ReservedDevice().IsMatch(stem))
        {
            return "is a name Windows keeps for devices";
        }

        if (string.Equals(trimmed, PhotoBackupNaming.UndatedFolder, StringComparison.OrdinalIgnoreCase) || DateLike().IsMatch(trimmed))
        {
            return "must not look like a date folder";
        }

        return null;
    }

    /// <summary>Marks that reorder text (a name could look like another): LRM, RLM, the embeddings and isolates. ZWNJ stays allowed.</summary>
    private static bool IsDirectionMark(char c) => c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');

    [GeneratedRegex(@"^(COM|LPT)[0-9¹²³]$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedDevice();

    [GeneratedRegex(@"^[0-9][0-9 ._\-]*$")]
    private static partial Regex DateLike();
}
