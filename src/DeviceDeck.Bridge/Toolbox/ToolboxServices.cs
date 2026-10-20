using DeviceDeck.Core.Toolbox;

namespace DeviceDeck.Bridge.Toolbox;

/// <summary>Opens the mirror window for a phone (the Shell); a second call brings the open one to the front.</summary>
public interface IMirrorWindows
{
    void Open(string deviceId);
}

/// <summary>
/// What the toolbox methods need beyond <see cref="Methods.BridgeServices"/>: Windows-backed converters from the Shell
/// (each optional: without one the method answers not_supported) and the default folders for saved files.
/// </summary>
public sealed class ToolboxServices
{
    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public IHeicConverter? Heic { get; init; }

    /// <summary>For formats the managed trimmer doesn't cut (m4a, aac, flac, wma, …).</summary>
    public IAudioTrimmer? AudioTrimmer { get; init; }

    public IPdfPrinter? Pdf { get; init; }

    public IMirrorWindows? MirrorWindows { get; init; }

    /// <summary>Logs, crash reports and device reports go here when the page names no folder.</summary>
    public string OutputFolder { get; init; } = Path.Combine(Documents, "DeviceDeck");

    /// <summary>Mirror screenshots.</summary>
    public string PicturesFolder { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DeviceDeck");

    /// <summary>Trimmed ringtones before they go to the phone.</summary>
    public string TempFolder { get; init; } = Path.Combine(Path.GetTempPath(), "DeviceDeck", "toolbox");

    /// <summary>How often buffered log lines go to the page.</summary>
    public TimeSpan LogFlushInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Lines kept per log session for export.</summary>
    public int LogCapacity { get; init; } = 100_000;

    /// <summary>How long QR pairing waits for a phone to scan the code.</summary>
    public TimeSpan QrTimeout { get; init; } = TimeSpan.FromMinutes(3);

    internal LogSessions Logs { get; } = new();

    internal ToolboxJobs Jobs { get; } = new();

    internal Dictionary<string, CancellationTokenSource> QrSessions { get; } = new(StringComparer.Ordinal);
}
