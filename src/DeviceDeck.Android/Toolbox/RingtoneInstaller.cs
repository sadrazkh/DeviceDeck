using System.Globalization;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;

namespace DeviceDeck.Android.Toolbox;

/// <summary>
/// Ringtones: the file goes into /sdcard/Ringtones, MediaStore indexes it (files there are marked as ringtones by
/// MediaProvider itself), and "make it the ringtone" is the same setting Settings → Sound writes.
/// </summary>
internal static class RingtoneInstaller
{
    public const string AudioUri = "content://media/external/audio/media";

    /// <summary>Keeps letters (any script), digits, spaces and ._-() ; everything else becomes "_".</summary>
    public static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Trim());
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '.' or '_' or '-' or '(' or ')' ? ch : '_').ToArray();
        var cleaned = new string(chars).Trim();
        var extension = Path.GetExtension(cleaned);
        // No leading dot (a hidden file MediaStore skips) and never an empty name.
        var stem = Path.GetFileNameWithoutExtension(cleaned).Trim(' ', '.');
        if (stem.Length == 0)
        {
            stem = "Ringtone";
        }

        return (stem.Length + extension.Length > 120 ? stem[..(120 - extension.Length)] : stem) + extension;
    }

    public static string QueryCommand(string canonicalPath) =>
        $"content query --uri {AudioUri} --projection _id --where {ShellQuote.Quote("_data=" + ShellQuote.SqlString(canonicalPath))}";

    public static string SetDefaultCommand(long id) =>
        "settings put system ringtone " + ShellQuote.Quote($"{AudioUri}/{id.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>MediaStore's id for the file, waiting a little for the scan; null when it never shows up.</summary>
    public static async Task<long?> FindMediaIdAsync(IAdbChannel channel, string canonicalPath, CancellationToken ct, int attempts = 10)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var result = await channel.ShellAsync(QueryCommand(canonicalPath), ct).ConfigureAwait(false);
            var row = ContentQueryParser.Parse(result.Output, ["_id"]).FirstOrDefault();
            if (row?.GetValueOrDefault("_id") is { } text && long.TryParse(text, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        return null;
    }
}
