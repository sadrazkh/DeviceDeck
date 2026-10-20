using DeviceDeck.Core.Toolbox;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace DeviceDeck.Shell.Toolbox;

/// <summary>
/// Cuts audio Windows can decode (m4a, aac, flac, wma, mp3, wav, …) with Media Foundation's transcoder and writes
/// AAC in an .m4a, which every Android version plays as a ringtone.
/// </summary>
internal sealed class MediaAudioTrimmer : IAudioTrimmer
{
    private static readonly string[] Extensions = [".m4a", ".aac", ".mp4", ".flac", ".wma", ".mp3", ".wav", ".3gp", ".adts"];

    public bool CanTrim(string extension) => Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public async Task<string> TrimAsync(string source, TimeSpan start, TimeSpan end, string destinationWithoutExtension, CancellationToken ct)
    {
        var input = await StorageFile.GetFileFromPathAsync(source).AsTask(ct).ConfigureAwait(false);
        var duration = (await input.Properties.GetMusicPropertiesAsync().AsTask(ct).ConfigureAwait(false)).Duration;
        var path = destinationWithoutExtension + ".m4a";
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(path)).AsTask(ct).ConfigureAwait(false);
        var output = await folder.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting).AsTask(ct).ConfigureAwait(false);

        var transcoder = new MediaTranscoder { TrimStartTime = start };
        if (duration > TimeSpan.Zero && end < duration)
        {
            // TrimStopTime is how much to cut off the end, not where to stop.
            transcoder.TrimStopTime = duration - end;
        }

        var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High)).AsTask(ct).ConfigureAwait(false);
        if (!prepared.CanTranscode)
        {
            throw new InvalidDataException($"Windows can't convert this audio file ({prepared.FailureReason}).");
        }

        await prepared.TranscodeAsync().AsTask(ct).ConfigureAwait(false);
        return path;
    }
}
