namespace DeviceDeck.Core.Abstractions;

// Toolbox: screen mirroring with control. A device that has DeviceCapability.ScreenMirror also implements this
// (Android through scrcpy-server; the iPhone is not in this round).

/// <param name="MaxSize">Longest side of the video in pixels; 0 = the phone's own resolution.</param>
public sealed record MirrorOptions(int MaxSize = 1280, int BitRate = 8_000_000, int MaxFps = 60, bool StayAwake = true);

public enum MirrorPacketKind
{
    /// <summary>A new capture session (start, rotation): <see cref="MirrorPacket.Width"/> and Height are set, no data.</summary>
    Session,
    /// <summary>Codec configuration (H.264 SPS/PPS in Annex B); keep it and send it with the next key frame.</summary>
    Config,
    Frame,
}

/// <param name="Pts">Presentation time in microseconds (frames only).</param>
public readonly record struct MirrorPacket(MirrorPacketKind Kind, long Pts, bool KeyFrame, int Width, int Height, ReadOnlyMemory<byte> Data);

public interface IScreenMirrorSource
{
    /// <summary>Starts mirroring; dispose the session to stop it (the phone side stops with it).</summary>
    Task<IScreenMirrorSession> StartMirrorAsync(MirrorOptions options, CancellationToken ct);
}

public interface IScreenMirrorSession : IAsyncDisposable
{
    /// <summary>The name the phone gave (for the window title).</summary>
    string DeviceName { get; }

    /// <summary>"h264".</summary>
    string Codec { get; }

    /// <summary>Video packets in order until the session ends; ends with device_disconnected when the phone goes away.</summary>
    IAsyncEnumerable<MirrorPacket> ReadPacketsAsync(CancellationToken ct);

    Task SendAsync(MirrorInput input, CancellationToken ct);
}

/// <summary>One input event or command for the phone. Coordinates are in the current video frame's pixels.</summary>
public abstract record MirrorInput;

public enum MirrorTouchAction { Down = 0, Up = 1, Move = 2, Cancel = 3 }

/// <param name="PointerId">-2 = the mouse as one finger; other values are touch pointers of a touch screen.</param>
/// <param name="Width">Width of the video frame the coordinates refer to; the phone drops events for an older size.</param>
public sealed record MirrorTouch(MirrorTouchAction Action, long PointerId, int X, int Y, int Width, int Height, float Pressure) : MirrorInput
{
    public const long MouseFinger = -2;
}

/// <param name="KeyCode">An Android KeyEvent code (3 home, 4 back, 24/25 volume, 26 power, 187 recent apps).</param>
public sealed record MirrorKey(bool Down, int KeyCode, int Repeat = 0, int MetaState = 0) : MirrorInput;

public sealed record MirrorText(string Text) : MirrorInput;

/// <param name="HScroll">Notches, -16..16; positive scrolls right / up like a mouse wheel.</param>
public sealed record MirrorScroll(int X, int Y, int Width, int Height, float HScroll, float VScroll) : MirrorInput;

public enum MirrorCommandKind { BackOrScreenOn, ExpandNotifications, ExpandSettings, CollapsePanels, Rotate, ResetVideo, ScreenOff, ScreenOn }

public sealed record MirrorCommand(MirrorCommandKind Kind) : MirrorInput;
