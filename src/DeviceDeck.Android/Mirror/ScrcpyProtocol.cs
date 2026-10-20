using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Mirror;

/// <summary>
/// The parts of scrcpy's client/server protocol DeviceDeck speaks (scrcpy-server 5.0.1, Apache-2.0; see
/// doc/develop.md and app/src/control_msg.c upstream). Client and server versions must match exactly.
/// </summary>
internal static class ScrcpyProtocol
{
    public const string ServerVersion = "5.0.1";

    /// <summary>Our own file name, so a scrcpy install of another version on the same phone isn't disturbed.</summary>
    public const string DevicePath = "/data/local/tmp/devicedeck-scrcpy-server.jar";

    public const uint CodecH264 = 0x68323634; // "h264"

    public const int DeviceNameLength = 64;

    /// <summary>Video packets bigger than this are a framing error, not a frame.</summary>
    public const int MaxPacketBytes = 32 * 1024 * 1024;

    public static string SocketName(int scid) => "scrcpy_" + scid.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>
    /// app_process command line: forward tunnel (we connect to the phone's abstract socket through adb, so nothing
    /// listens on the PC), video only, control on, H.264, cleaned up when the connection closes.
    /// </summary>
    public static string ServerCommand(int scid, MirrorOptions options) => string.Join(' ',
        $"CLASSPATH={DevicePath}",
        "app_process / com.genymobile.scrcpy.Server",
        ServerVersion,
        "scid=" + scid.ToString("x8", CultureInfo.InvariantCulture),
        "log_level=info",
        "tunnel_forward=true",
        "audio=false",
        "control=true",
        "video_codec=h264",
        FormattableString.Invariant($"max_size={Math.Max(0, options.MaxSize)}"),
        FormattableString.Invariant($"video_bit_rate={Math.Max(500_000, options.BitRate)}"),
        FormattableString.Invariant($"max_fps={Math.Clamp(options.MaxFps, 1, 120)}"),
        "stay_awake=" + (options.StayAwake ? "true" : "false"),
        "power_off_on_close=false",
        "clipboard_autosync=false",
        "cleanup=true");

    // ---- video socket ----

    private const long FlagSession = 1L << 63;
    private const long FlagConfig = 1L << 62;
    private const long FlagKeyFrame = 1L << 61;

    /// <summary>A 12-byte header: a session packet (width, height) or a media packet header (flags, pts, size).</summary>
    public static (MirrorPacketKind Kind, long Pts, bool KeyFrame, int WidthOrSize, int Height) ReadHeader(ReadOnlySpan<byte> header)
    {
        var first = BinaryPrimitives.ReadInt64BigEndian(header);
        if ((first & FlagSession) != 0)
        {
            return (MirrorPacketKind.Session, 0, false, BinaryPrimitives.ReadInt32BigEndian(header[4..]), BinaryPrimitives.ReadInt32BigEndian(header[8..]));
        }

        var config = (first & FlagConfig) != 0;
        var size = BinaryPrimitives.ReadInt32BigEndian(header[8..]);
        return (config ? MirrorPacketKind.Config : MirrorPacketKind.Frame, config ? 0 : first & (FlagKeyFrame - 1), (first & FlagKeyFrame) != 0, size, 0);
    }

    public static string DeviceName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    // ---- control socket (client → device) ----

    private const byte TypeInjectKeycode = 0;
    private const byte TypeInjectText = 1;
    private const byte TypeInjectTouch = 2;
    private const byte TypeInjectScroll = 3;
    private const byte TypeBackOrScreenOn = 4;
    private const byte TypeExpandNotifications = 5;
    private const byte TypeExpandSettings = 6;
    private const byte TypeCollapsePanels = 7;
    private const byte TypeSetDisplayPower = 10;
    private const byte TypeRotate = 11;
    private const byte TypeResetVideo = 17;

    public const int MaxTextBytes = 300;

    /// <summary>One input as the bytes scrcpy-server reads; some commands are two messages (key down and up).</summary>
    public static IReadOnlyList<byte[]> Serialize(MirrorInput input) => input switch
    {
        MirrorKey key => [Key(key)],
        MirrorText text => [Text(text.Text)],
        MirrorTouch touch => [Touch(touch)],
        MirrorScroll scroll => [Scroll(scroll)],
        MirrorCommand { Kind: MirrorCommandKind.BackOrScreenOn } => [[TypeBackOrScreenOn, 0], [TypeBackOrScreenOn, 1]],
        MirrorCommand { Kind: MirrorCommandKind.ExpandNotifications } => [[TypeExpandNotifications]],
        MirrorCommand { Kind: MirrorCommandKind.ExpandSettings } => [[TypeExpandSettings]],
        MirrorCommand { Kind: MirrorCommandKind.CollapsePanels } => [[TypeCollapsePanels]],
        MirrorCommand { Kind: MirrorCommandKind.Rotate } => [[TypeRotate]],
        MirrorCommand { Kind: MirrorCommandKind.ResetVideo } => [[TypeResetVideo]],
        MirrorCommand { Kind: MirrorCommandKind.ScreenOff } => [[TypeSetDisplayPower, 0]],
        MirrorCommand { Kind: MirrorCommandKind.ScreenOn } => [[TypeSetDisplayPower, 1]],
        _ => throw new ArgumentException($"Unknown mirror input {input.GetType().Name}."),
    };

    private static byte[] Key(MirrorKey key)
    {
        var buffer = new byte[14];
        buffer[0] = TypeInjectKeycode;
        buffer[1] = key.Down ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(2), key.KeyCode);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(6), key.Repeat);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(10), key.MetaState);
        return buffer;
    }

    private static byte[] Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = bytes.Length;
        if (length > MaxTextBytes)
        {
            // Cut on a character boundary: back up over UTF-8 continuation bytes.
            length = MaxTextBytes;
            while (length > 0 && (bytes[length] & 0xC0) == 0x80)
            {
                length--;
            }
        }

        var buffer = new byte[5 + length];
        buffer[0] = TypeInjectText;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), length);
        bytes.AsSpan(0, length).CopyTo(buffer.AsSpan(5));
        return buffer;
    }

    private static byte[] Touch(MirrorTouch touch)
    {
        var buffer = new byte[32];
        buffer[0] = TypeInjectTouch;
        buffer[1] = (byte)touch.Action;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(2), touch.PointerId);
        WritePosition(buffer.AsSpan(10), touch.X, touch.Y, touch.Width, touch.Height);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(22), ToU16FixedPoint(touch.Action == MirrorTouchAction.Up ? 0 : touch.Pressure));
        // action_button and buttons stay 0: a finger, not a mouse.
        return buffer;
    }

    private static byte[] Scroll(MirrorScroll scroll)
    {
        var buffer = new byte[21];
        buffer[0] = TypeInjectScroll;
        WritePosition(buffer.AsSpan(1), scroll.X, scroll.Y, scroll.Width, scroll.Height);
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(13), ToI16FixedPoint(Math.Clamp(scroll.HScroll / 16, -1, 1)));
        BinaryPrimitives.WriteInt16BigEndian(buffer.AsSpan(15), ToI16FixedPoint(Math.Clamp(scroll.VScroll / 16, -1, 1)));
        return buffer;
    }

    private static void WritePosition(Span<byte> at, int x, int y, int width, int height)
    {
        BinaryPrimitives.WriteInt32BigEndian(at, x);
        BinaryPrimitives.WriteInt32BigEndian(at[4..], y);
        BinaryPrimitives.WriteUInt16BigEndian(at[8..], (ushort)Math.Clamp(width, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(at[10..], (ushort)Math.Clamp(height, 0, ushort.MaxValue));
    }

    /// <summary>scrcpy's sc_float_to_u16fp: 0..1 → 0..0xFFFF.</summary>
    public static ushort ToU16FixedPoint(float value)
    {
        var u = (uint)(Math.Clamp(value, 0f, 1f) * 65536f);
        return (ushort)Math.Min(u, 0xFFFF);
    }

    /// <summary>scrcpy's sc_float_to_i16fp: -1..1 → -0x8000..0x7FFF.</summary>
    public static short ToI16FixedPoint(float value)
    {
        var i = (int)(Math.Clamp(value, -1f, 1f) * 32768f);
        return (short)Math.Clamp(i, short.MinValue, short.MaxValue);
    }
}
