using System.Globalization;

namespace DeviceDeck.Ios.PhotoDelete;

/// <summary>
/// One line per photo deleted from an iPhone (and per refusal), in photo-deletes-yyyyMMdd.log beside DeviceDeck's other
/// logs: when, which phone, the DCIM path and its size. Never the photo's content.
/// </summary>
public sealed class PhotoDeleteLog(string? folder)
{
    private static readonly Lock Gate = new();

    /// <summary>%LocalAppData%\DeviceDeck\logs, or DEVICEDECK_DATA\logs when that is set.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("DEVICEDECK_DATA") is { Length: > 0 } data
            ? data
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeviceDeck"), "logs");

    public string? Folder { get; } = folder;

    /// <summary>The lines written in this process, newest last (the probe prints them).</summary>
    public List<string> Written { get; } = [];

    public void Write(string udid, string message)
    {
        var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{Short(udid)}] {message}";
        lock (Gate)
        {
            Written.Add(line);
            if (Folder is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, $"photo-deletes-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that can't be written must not stop the delete; the line is still in Written.
            }
        }
    }

    private static string Short(string udid) => udid.Length > 8 ? "…" + udid[^8..] : udid;
}
