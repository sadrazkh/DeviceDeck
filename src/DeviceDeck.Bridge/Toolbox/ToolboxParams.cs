using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Bridge.Toolbox;

// Params, results and events of the toolbox methods (docs/bridge-contract.md, "Toolbox").

public sealed record ToolboxFeatures(bool Log, bool CrashReports, bool Mirror, bool Ringtone, bool WifiSync, bool Report, bool WirelessDebugging);

public sealed record SessionParams(string SessionId);

public sealed record SessionResult(string SessionId);

public sealed record LogExportParams(string SessionId, DeviceLogLevel MinLevel = DeviceLogLevel.Verbose, string? Search = null, string? Destination = null);

public sealed record SavedFileResult(string Path, int Count);

/// <summary>One log line on the wire; <c>Seq</c> counts from 1 per session.</summary>
public sealed record LogLine(long Seq, DateTimeOffset? Time, DeviceLogLevel Level, string Process, int? Pid, string? Tag, string Message);

public sealed record LogLinesEvent(string SessionId, IReadOnlyList<LogLine> Lines, int Dropped);

public sealed record ToolboxError(string Code, string Message);

public sealed record LogEndedEvent(string SessionId, ToolboxError? Error);

public sealed record CrashParams(string DeviceId, string Id);

public sealed record CrashIdsParams(string DeviceId, IReadOnlyList<string> Ids, string? Destination = null);

public sealed record CrashTextResult(string Text, bool Truncated, long Size);

public sealed record CrashExportResult(string Folder, IReadOnlyList<string> Files);

public sealed record CrashDeleteResult(int Deleted, long FreedBytes);

public enum ReportFormat { Html, Pdf }

public sealed record ReportParams(string DeviceId, string Language = "en", bool RevealSerials = false, ReportFormat Format = ReportFormat.Html, string? Destination = null);

public sealed record HtmlResult(string Html);

public sealed record HeicStatusResult(bool Available);

public sealed record HeicConvertParams(IReadOnlyList<string> Files, int Quality = 90, string? Destination = null);

public sealed record JobStartedResult(string JobId, int Total);

public sealed record ToolboxJobParams(string JobId);

public enum ToolboxJobState { Running, Done, Failed, Cancelled }

/// <summary>A PC-side job (HEIC conversion); <c>Outputs</c> holds the first files written, for "Show in Explorer".</summary>
public sealed record ToolboxJob(
    string Id,
    string Kind,
    ToolboxJobState State,
    int Total,
    int Done,
    int Failed,
    string? Current,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Errors);

public sealed record WifiStatusResult(bool WirelessDebugging);

public sealed record WifiPairParams(string Host, int Port, string Code);

public sealed record WifiConnectParams(string Host, int Port);

public sealed record WifiQrResult(string SessionId, string Name, string Password, string Payload);

public sealed record WifiQrEvent(string SessionId, string Stage, string? Serial, string? Message, ToolboxError? Error);

public sealed record WifiSyncResult(bool? Enabled);

public sealed record WifiSyncParams(string DeviceId, bool Enabled);

public sealed record RingtoneParams(string DeviceId, IReadOnlyList<string> Files, double StartSeconds, double EndSeconds, string? Name = null, bool SetAsDefault = false);

public sealed record RingtoneResultDto(string DevicePath, bool IsDefault, long Bytes);

// ---- mirror window ----

public sealed record MirrorStartParams(string DeviceId, int MaxSize = 1280, int BitRate = 8_000_000, int MaxFps = 60);

public sealed record MirrorStartResult(string SessionId, string DeviceName, string Codec);

/// <summary>
/// One input from the mirror window. <c>Kind</c>: touch (action, pointerId, x, y, width, height, pressure),
/// key (down, keyCode), text (text), scroll (x, y, width, height, hScroll, vScroll) or command (command).
/// </summary>
public sealed record MirrorInputParams(
    string SessionId,
    string Kind,
    MirrorTouchAction Action = MirrorTouchAction.Down,
    long PointerId = MirrorTouch.MouseFinger,
    int X = 0,
    int Y = 0,
    int Width = 0,
    int Height = 0,
    float Pressure = 1,
    bool Down = true,
    int KeyCode = 0,
    string? Text = null,
    float HScroll = 0,
    float VScroll = 0,
    MirrorCommandKind Command = MirrorCommandKind.BackOrScreenOn);

/// <summary>A video packet, its bytes as base64.</summary>
public sealed record MirrorVideoEvent(string SessionId, MirrorPacketKind Kind, long Pts, bool Key, int Width, int Height, string Data);

public sealed record MirrorEndedEvent(string SessionId, ToolboxError? Error);

public sealed record MirrorShotResult(string Path);
