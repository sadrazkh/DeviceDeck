namespace DeviceDeck.Core.Abstractions;

// Toolbox: ringtones. Android only: an iPhone takes ringtones only through an iTunes / Finder sync.

/// <param name="DevicePath">Where the file went on the phone (Ringtones folder).</param>
/// <param name="IsDefault">True when it is now the phone's ringtone too.</param>
public sealed record RingtoneResult(string DevicePath, bool IsDefault);

public interface IRingtoneTarget
{
    /// <summary>File types the phone plays as ringtones (".mp3", ".m4a", …).</summary>
    IReadOnlyCollection<string> RingtoneExtensions { get; }

    /// <summary>
    /// Copies an audio file into the phone's Ringtones folder and adds it to the ringtone list (MediaStore).
    /// An existing ringtone of that name is replaced. <paramref name="setAsDefault"/> also makes it the ringtone.
    /// </summary>
    Task<RingtoneResult> AddRingtoneAsync(string fileName, Stream source, long length, bool setAsDefault, CancellationToken ct);
}
