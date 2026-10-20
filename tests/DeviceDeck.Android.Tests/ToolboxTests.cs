using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Mirror;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Android.Toolbox;
using DeviceDeck.Android.Wifi;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

/// <summary>The Android toolbox: logcat, crash reports, ringtones, scrcpy mirroring and wireless debugging.</summary>
public class ToolboxTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Tehran = TimeSpan.FromHours(3.5);

    // ---- logcat ----

    [Fact]
    public void Logcat_lines_parse_with_and_without_the_year()
    {
        var now = new DateTimeOffset(2026, 10, 20, 17, 0, 0, Tehran);
        var processes = new Dictionary<int, string> { [1290] = "system_server" };
        var e = LogcatParser.Parse("2026-10-20 16:44:01.123  1290  1312 I ActivityManager: Start proc 4242:com.example/u0a201", Tehran, now, processes)!;
        Assert.Equal((DeviceLogLevel.Info, "system_server", 1290, "ActivityManager", "Start proc 4242:com.example/u0a201"), (e.Level, e.Process, e.Pid, e.Tag, e.Message));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 16, 44, 1, 123, Tehran), e.Time);

        var old = LogcatParser.Parse("10-20 16:44:01.500   640   650 E SurfaceFlinger  : Frame missed: 2", Tehran, now, null)!;
        Assert.Equal((DeviceLogLevel.Error, "", "SurfaceFlinger", "Frame missed: 2"), (old.Level, old.Process, old.Tag, old.Message));
        Assert.Equal(2026, old.Time!.Value.Year);

        var empty = LogcatParser.Parse("2026-10-20 16:44:01.123  1  1 F init    :", Tehran, now, null)!;
        Assert.Equal((DeviceLogLevel.Fault, "init", ""), (empty.Level, empty.Tag, empty.Message));

        Assert.Null(LogcatParser.Parse("--------- beginning of main", Tehran, now, null));
        Assert.Null(LogcatParser.Parse("\r", Tehran, now, null));
        Assert.Equal("garbage", LogcatParser.Parse("garbage", Tehran, now, null)!.Message);
    }

    [Theory]
    [InlineData('V', DeviceLogLevel.Verbose)]
    [InlineData('D', DeviceLogLevel.Debug)]
    [InlineData('I', DeviceLogLevel.Info)]
    [InlineData('W', DeviceLogLevel.Warning)]
    [InlineData('E', DeviceLogLevel.Error)]
    [InlineData('F', DeviceLogLevel.Fault)]
    [InlineData('A', DeviceLogLevel.Fault)]
    public void Logcat_levels_map(char letter, DeviceLogLevel level) => Assert.Equal(level, LogcatParser.LevelOf(letter));

    [Fact]
    public void Process_lists_from_toybox_and_toolbox_ps()
    {
        var toybox = LogcatParser.ParseProcesses("  PID NAME\n    1 init\n 1290 system_server\n 5120 com.example.notes\n");
        Assert.Equal("system_server", toybox[1290]);
        Assert.Equal(3, toybox.Count);
        var toolbox = LogcatParser.ParseProcesses("USER     PID   PPID  VSIZE  RSS     WCHAN    PC         NAME\nroot      1     0     1000   200   ffffffff 00000000 S /init\nu0_a12    3388  300   900000 50000 ffffffff 00000000 S com.google.android.gms\n");
        Assert.Equal("com.google.android.gms", toolbox[3388]);
        Assert.Empty(LogcatParser.ParseProcesses(""));
    }

    [Fact]
    public void Line_sink_hands_over_whole_lines_across_writes()
    {
        var lines = new List<string>();
        using var sink = new LineSink(lines.Add);
        sink.Write("first\nsec"u8);
        sink.Write("ond\nعکس "u8);
        sink.Write(Encoding.UTF8.GetBytes("تازه"));
        Assert.Equal(["first", "second"], lines);
        sink.Flush();
        Assert.Equal(["first", "second", "عکس تازه"], lines);
    }

    [Fact]
    public async Task Log_stream_names_processes_and_ends_when_logcat_stops()
    {
        var (device, fake) = await DeviceTests.ReadyAsync("android-14-google-pixel-7", f =>
        {
            f.On(LogcatParser.ProcessListCommand, "  PID NAME\n 1290 system_server\n");
            f.On(LogcatParser.Command,
                "--------- beginning of main\n2026-10-20 16:44:01.123  1290  1312 I ActivityManager: one\n2026-10-20 16:44:01.124  5120  5120 E AndroidRuntime: two\n");
        });
        var entries = new List<DeviceLogEntry>();
        var error = await Assert.ThrowsAsync<DeviceException>(async () =>
        {
            await foreach (var entry in device.StreamLogAsync(None))
            {
                entries.Add(entry);
            }
        });
        Assert.Equal(DeviceErrorCodes.Disconnected, error.Code);
        Assert.Equal([("system_server", "one"), ("", "two")], entries.Select(e => (e.Process, e.Message)));
        // The unknown pid 5120 asked ps once more, at most.
        Assert.InRange(fake.Commands.Count(c => c == LogcatParser.ProcessListCommand), 1, 2);
    }

    // ---- crash reports ----

    private const string DropBox = """
        Drop box contents: 6 entries
        Max entries: 1000

        ========================================
        2026-10-19 21:14:03 data_app_crash (text, 3121 bytes)
            Process: com.example.notes/Flags: 0x38c8be46/Package: com.example.notes v42
        ========================================
        2026-10-19 21:20:00 netstats_dump (compressed data, 4000 bytes)
        ========================================
        2026-10-18 23:20:00 data_app_anr (compressed text, 900 bytes)
            Process: com.whatsapp/Subject: Input dispatching timed out
        ========================================
        2026-10-17 14:00:00 SYSTEM_TOMBSTONE_PROTO (compressed data, 5000 bytes)
        ========================================
        2026-10-17 14:00:00 SYSTEM_TOMBSTONE (compressed text, 7000 bytes)
            *** *** ***/pid: 640, tid: 650, name: surfaceflinger  >>> /system/bin/surfaceflinger <<<
        ========================================
        2026-10-15 06:50:00 system_server_crash (text, 100 bytes) (contents lost)
        """;

    [Fact]
    public void Drop_box_lists_crash_like_entries_only()
    {
        var reports = AndroidCrashReports.ParseDropBox(DropBox.Replace("\r\n", "\n"), Tehran);
        Assert.Equal(
            [
                ("dropbox/data_app_crash/2026-10-19 21:14:03", "com.example.notes", CrashReportKind.Crash, true),
                ("dropbox/data_app_anr/2026-10-18 23:20:00", "com.whatsapp", CrashReportKind.Hang, true),
                ("dropbox/SYSTEM_TOMBSTONE_PROTO/2026-10-17 14:00:00", "SYSTEM_TOMBSTONE_PROTO", CrashReportKind.Tombstone, false),
                ("dropbox/SYSTEM_TOMBSTONE/2026-10-17 14:00:00", "surfaceflinger", CrashReportKind.Tombstone, true),
            ],
            reports.Select(r => (r.Id, r.Process, r.Kind, r.Readable)));
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 21, 14, 3, Tehran), reports[0].Time);
        Assert.Equal("data_app_crash-2026-10-19-211403.txt", reports[0].FileName);
        Assert.Equal(3121, reports[0].Size);
    }

    [Theory]
    [InlineData("data_app_native_crash", CrashReportKind.Tombstone)]
    [InlineData("system_app_crash", CrashReportKind.Crash)]
    [InlineData("system_server_crash", CrashReportKind.Panic)]
    [InlineData("system_server_watchdog", CrashReportKind.Panic)]
    [InlineData("SYSTEM_RESTART", CrashReportKind.Panic)]
    [InlineData("system_app_anr", CrashReportKind.Hang)]
    [InlineData("lowmemorykiller", CrashReportKind.Memory)]
    [InlineData("netstats_dump", null)]
    [InlineData("system_app_strictmode", null)]
    public void Drop_box_tags_map_to_kinds(string tag, CrashReportKind? kind) => Assert.Equal(kind, AndroidCrashReports.KindOf(tag));

    [Fact]
    public async Task Crash_list_reports_root_only_folders_and_reads_one_entry()
    {
        var (device, fake) = await DeviceTests.ReadyAsync("android-14-google-pixel-7", f =>
        {
            f.On("dumpsys dropbox", DropBox.Replace("\r\n", "\n") + "\n");
            f.On("ls /data/tombstones 2>&1", "ls: /data/tombstones: Permission denied\n");
            f.On("ls /data/anr 2>&1", "anr_2026-10-18-23-20-00-123\n");
            f.On("cd /data/anr && stat -c '%s %Y %n' *", "2048 1760816400 anr_2026-10-18-23-20-00-123\n");
            f.On("dumpsys dropbox --print '2026-10-19 21:14:03' 'data_app_crash'",
                "Drop box contents: 6 entries\n\n========================================\n2026-10-19 21:14:03 data_app_crash (text, 3121 bytes)\nProcess: com.example.notes\njava.lang.NullPointerException\n");
            f.On("cat '/data/anr/anr_2026-10-18-23-20-00-123'", "----- pid 7781 -----\n");
        });

        var list = await device.ListCrashReportsAsync(None);
        Assert.False(list.CanDelete);
        Assert.Equal(["tombstones"], list.Unreadable);
        Assert.Contains(list.Reports, r => r.Id == "anr/anr_2026-10-18-23-20-00-123" && r.Kind == CrashReportKind.Hang && r.Size == 2048);
        Assert.Equal(5, list.Reports.Count);
        Assert.Single(fake.Commands, c => c.Contains("dumpsys dropbox", StringComparison.Ordinal)); // one round trip

        using var text = new MemoryStream();
        await device.ReadCrashReportAsync("dropbox/data_app_crash/2026-10-19 21:14:03", text, None);
        var body = Encoding.UTF8.GetString(text.ToArray());
        Assert.StartsWith("========", body, StringComparison.Ordinal);
        Assert.Contains("NullPointerException", body, StringComparison.Ordinal);

        using var anr = new MemoryStream();
        await device.ReadCrashReportAsync("anr/anr_2026-10-18-23-20-00-123", anr, None);
        Assert.Equal("----- pid 7781 -----\n", Encoding.UTF8.GetString(anr.ToArray()));

        Assert.Equal(DeviceErrorCodes.PathNotFound, (await Assert.ThrowsAsync<DeviceException>(() =>
            device.ReadCrashReportAsync("dropbox/x; rm -rf /sdcard/2026-10-19 21:14:03", Stream.Null, None))).Code);
        Assert.Equal(DeviceErrorCodes.PathNotFound, (await Assert.ThrowsAsync<DeviceException>(() =>
            device.ReadCrashReportAsync("anr/../../sdcard/x", Stream.Null, None))).Code);
        Assert.Equal(DeviceErrorCodes.PermissionDenied, (await Assert.ThrowsAsync<DeviceException>(() =>
            device.DeleteCrashReportsAsync(["anr/anr_2026-10-18-23-20-00-123"], None))).Code);
    }

    // ---- ringtones ----

    [Theory]
    [InlineData("My tone.mp3", "My tone.mp3")]
    [InlineData("a/b\\c'd\";e.m4a", "c_d__e.m4a")]
    [InlineData("زنگ صبح.mp3", "زنگ صبح.mp3")]
    [InlineData(".mp3", "Ringtone.mp3")]
    public void Ringtone_names_are_safe_on_the_phone(string name, string expected) => Assert.Equal(expected, RingtoneInstaller.SafeFileName(name));

    [Fact]
    public async Task Ringtone_goes_to_the_Ringtones_folder_and_can_become_the_ringtone()
    {
        var path = "/storage/emulated/0/Ringtones/Morning.mp3";
        var (device, fake) = await DeviceTests.ReadyAsync("android-14-google-pixel-7", f =>
        {
            f.On("mkdir -p '/sdcard/Ringtones'", "");
            f.On(RingtoneInstaller.QueryCommand(path), "Row: 0 _id=57\n");
            f.On(RingtoneInstaller.SetDefaultCommand(57), "");
        });
        var data = new byte[1234];
        var result = await device.AddRingtoneAsync("Morning.mp3", new MemoryStream(data), data.Length, setAsDefault: true, None);
        Assert.Equal(("/sdcard/Ringtones/Morning.mp3", true), (result.DevicePath, result.IsDefault));
        Assert.Equal(1234, fake.Files["/sdcard/Ringtones/Morning.mp3"].Length);
        Assert.Contains(fake.Commands, c => c.Contains("MEDIA_SCANNER_SCAN_FILE", StringComparison.Ordinal) && c.Contains("Ringtones/Morning.mp3", StringComparison.Ordinal));
        Assert.Contains("settings put system ringtone 'content://media/external/audio/media/57'", fake.Commands);

        await Assert.ThrowsAsync<DeviceException>(() => device.AddRingtoneAsync("song.exe", Stream.Null, 0, false, None));
    }

    // ---- scrcpy protocol ----

    [Fact]
    public void Touch_key_text_scroll_and_commands_serialize_like_scrcpy()
    {
        var touch = Assert.Single(ScrcpyProtocol.Serialize(new MirrorTouch(MirrorTouchAction.Down, MirrorTouch.MouseFinger, 540, 1170, 1080, 2340, 1f)));
        Assert.Equal(32, touch.Length);
        Assert.Equal(2, touch[0]);
        Assert.Equal(0, touch[1]);
        Assert.Equal(-2L, BinaryPrimitives.ReadInt64BigEndian(touch.AsSpan(2)));
        Assert.Equal((540, 1170), (BinaryPrimitives.ReadInt32BigEndian(touch.AsSpan(10)), BinaryPrimitives.ReadInt32BigEndian(touch.AsSpan(14))));
        Assert.Equal((1080, 2340), ((int)BinaryPrimitives.ReadUInt16BigEndian(touch.AsSpan(18)), (int)BinaryPrimitives.ReadUInt16BigEndian(touch.AsSpan(20))));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(touch.AsSpan(22)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(Assert.Single(ScrcpyProtocol.Serialize(new MirrorTouch(MirrorTouchAction.Up, 7, 1, 1, 10, 10, 1f))).AsSpan(22)));

        var key = Assert.Single(ScrcpyProtocol.Serialize(new MirrorKey(false, 3)));
        Assert.Equal([0, 1, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0], key);

        var text = Assert.Single(ScrcpyProtocol.Serialize(new MirrorText("سلام")));
        Assert.Equal(1, text[0]);
        Assert.Equal(8, BinaryPrimitives.ReadInt32BigEndian(text.AsSpan(1)));
        Assert.Equal("سلام", Encoding.UTF8.GetString(text.AsSpan(5)));
        // Long text is cut at 300 bytes, on a character boundary (each Persian letter is 2 bytes; "a" makes it odd).
        var cut = Assert.Single(ScrcpyProtocol.Serialize(new MirrorText("a" + new string('س', 200))));
        Assert.Equal(299, BinaryPrimitives.ReadInt32BigEndian(cut.AsSpan(1)));

        var scroll = Assert.Single(ScrcpyProtocol.Serialize(new MirrorScroll(10, 20, 1080, 2340, 0, -1)));
        Assert.Equal(21, scroll.Length);
        Assert.Equal(3, scroll[0]);
        Assert.Equal(-2048, BinaryPrimitives.ReadInt16BigEndian(scroll.AsSpan(15)));

        Assert.Equal([[4, 0], [4, 1]], ScrcpyProtocol.Serialize(new MirrorCommand(MirrorCommandKind.BackOrScreenOn)));
        Assert.Equal([[11]], ScrcpyProtocol.Serialize(new MirrorCommand(MirrorCommandKind.Rotate)));
        Assert.Equal([[17]], ScrcpyProtocol.Serialize(new MirrorCommand(MirrorCommandKind.ResetVideo)));
        Assert.Equal([[10, 0]], ScrcpyProtocol.Serialize(new MirrorCommand(MirrorCommandKind.ScreenOff)));
    }

    [Fact]
    public void Video_headers_tell_sessions_config_and_key_frames_apart()
    {
        var session = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(session, 0x80000000);
        BinaryPrimitives.WriteInt32BigEndian(session.AsSpan(4), 1080);
        BinaryPrimitives.WriteInt32BigEndian(session.AsSpan(8), 2340);
        Assert.Equal((MirrorPacketKind.Session, 1080, 2340), Pick(ScrcpyProtocol.ReadHeader(session)));

        var key = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(key, (1L << 61) | 123_456);
        BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(8), 999);
        var (kind, pts, isKey, size, _) = ScrcpyProtocol.ReadHeader(key);
        Assert.Equal((MirrorPacketKind.Frame, 123_456L, true, 999), (kind, pts, isKey, size));

        var config = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(config, 1L << 62);
        BinaryPrimitives.WriteInt32BigEndian(config.AsSpan(8), 30);
        Assert.Equal(MirrorPacketKind.Config, ScrcpyProtocol.ReadHeader(config).Kind);

        static (MirrorPacketKind, int, int) Pick((MirrorPacketKind Kind, long Pts, bool KeyFrame, int WidthOrSize, int Height) h) => (h.Kind, h.WidthOrSize, h.Height);
    }

    [Fact]
    public void Server_command_names_the_version_and_options()
    {
        var command = ScrcpyProtocol.ServerCommand(0x1234abcd, new MirrorOptions(MaxSize: 1024, BitRate: 4_000_000, MaxFps: 30));
        Assert.StartsWith("CLASSPATH=/data/local/tmp/devicedeck-scrcpy-server.jar app_process / com.genymobile.scrcpy.Server 5.0.1 ", command, StringComparison.Ordinal);
        foreach (var part in new[] { "scid=1234abcd", "tunnel_forward=true", "audio=false", "control=true", "video_codec=h264", "max_size=1024", "video_bit_rate=4000000", "max_fps=30", "cleanup=true" })
        {
            Assert.Contains(" " + part, command, StringComparison.Ordinal);
        }
        Assert.Equal("scrcpy_0000002a", ScrcpyProtocol.SocketName(42));
    }

    /// <summary>A scrcpy-server on the fake adb server: the shell runs until closed, the abstract socket refuses twice, then streams.</summary>
    private sealed class FakeScrcpy
    {
        public readonly ConcurrentQueue<byte[]> Control = new();
        public readonly TaskCompletionSource ShellClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Refusals = 2;
        public int VideoConnections;
        public string? Socket;
        public bool StreamThenHangUp;

        public async Task<bool> HandleAsync(string request, NetworkStream stream, CancellationToken ct)
        {
            if (request.StartsWith("shell,v2,raw:CLASSPATH=", StringComparison.Ordinal))
            {
                Socket = "localabstract:scrcpy_" + request.Split("scid=")[1][..8];
                await stream.WriteAsync("OKAY"u8.ToArray(), ct);
                await stream.ReadExactlyAsync(new byte[5], ct); // close-stdin packet
                await WritePacket(stream, 1, "[server] INFO: Device: [Google] Pixel 7 (Android 14)\n"u8.ToArray(), ct);
                var buffer = new byte[16];
                try
                {
                    while (await stream.ReadAsync(buffer, ct) > 0)
                    {
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }
                ShellClosed.TrySetResult();
                return true;
            }

            if (Socket is null || request != Socket)
            {
                return false;
            }

            if (Interlocked.Decrement(ref Refusals) >= 0)
            {
                await stream.WriteAsync("FAIL"u8.ToArray(), ct);
                await stream.WriteAsync("0006closed"u8.ToArray(), ct);
                return true;
            }

            await stream.WriteAsync("OKAY"u8.ToArray(), ct);
            if (Interlocked.Increment(ref VideoConnections) == 1)
            {
                var name = new byte[64];
                "Pixel 7"u8.CopyTo(name);
                await stream.WriteAsync(new byte[] { 0 }, ct); // dummy byte
                await stream.WriteAsync(name, ct);
                await stream.WriteAsync(new byte[] { 0x68, 0x32, 0x36, 0x34 }, ct); // "h264"
                await stream.WriteAsync(Header(0x80000000L << 32 | 1080, 2340), ct);
                await Media(stream, 1L << 62, [0, 0, 0, 1, 0x67, 0x64, 0, 0x28], ct);
                await Media(stream, (1L << 61) | 1000, [0, 0, 0, 1, 0x65, 1, 2, 3], ct);
                await Media(stream, 2000, [0, 0, 0, 1, 0x41, 4, 5], ct);
                if (StreamThenHangUp)
                {
                    return true;
                }

                await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
                return true;
            }

            // The control socket: record what the client sends.
            var read = new byte[256];
            int n;
            while ((n = await stream.ReadAsync(read, ct)) > 0)
            {
                Control.Enqueue(read[..n]);
            }
            return true;
        }

        private static byte[] Header(long first, int last)
        {
            var header = new byte[12];
            BinaryPrimitives.WriteInt64BigEndian(header, first);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), last);
            return header;
        }

        private static async Task Media(NetworkStream stream, long flagsAndPts, byte[] data, CancellationToken ct)
        {
            await stream.WriteAsync(Header(flagsAndPts, data.Length), ct);
            await stream.WriteAsync(data, ct);
        }

        private static async Task WritePacket(NetworkStream stream, byte id, byte[] data, CancellationToken ct)
        {
            var packet = new byte[5 + data.Length];
            packet[0] = id;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), (uint)data.Length);
            data.CopyTo(packet, 5);
            await stream.WriteAsync(packet, ct);
        }
    }

    private static string TempJar()
    {
        var jar = Path.Combine(Path.GetTempPath(), "devicedeck-test-scrcpy-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(jar, "PK fake scrcpy-server"u8.ToArray());
        return jar;
    }

    [Fact]
    public async Task Mirror_session_pushes_the_server_connects_streams_and_sends_input()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        var scrcpy = new FakeScrcpy();
        server.Extra = scrcpy.HandleAsync;
        var channel = new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3));
        var jar = TempJar();
        try
        {
            await using var session = await ScrcpySession.StartAsync(channel, channel, jar, new MirrorOptions(), None);
            Assert.Equal("Pixel 7", session.DeviceName);
            Assert.Equal("PK fake scrcpy-server", Encoding.ASCII.GetString(server.Files[ScrcpyProtocol.DevicePath]));
            Assert.Equal(-2, scrcpy.Refusals); // two refusals retried, then the video and control sockets

            var packets = new List<MirrorPacket>();
            using var read = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var packet in session.ReadPacketsAsync(read.Token))
            {
                packets.Add(packet);
                if (packets.Count == 4)
                {
                    break;
                }
            }

            Assert.Equal([MirrorPacketKind.Session, MirrorPacketKind.Config, MirrorPacketKind.Frame, MirrorPacketKind.Frame], packets.Select(p => p.Kind));
            Assert.Equal((1080, 2340), (packets[0].Width, packets[0].Height));
            Assert.Equal((1000L, true, 1080), (packets[2].Pts, packets[2].KeyFrame, packets[2].Width));
            Assert.Equal((2000L, false), (packets[3].Pts, packets[3].KeyFrame));
            Assert.Equal(new byte[] { 0, 0, 0, 1, 0x41, 4, 5 }, packets[3].Data.ToArray());

            await session.SendAsync(new MirrorKey(true, 3), None);
            await session.SendAsync(new MirrorCommand(MirrorCommandKind.Rotate), None);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (scrcpy.Control.Sum(c => c.Length) < 15 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            var sent = scrcpy.Control.SelectMany(c => c).ToArray();
            Assert.Equal(15, sent.Length);
            Assert.Equal(0, sent[0]);
            Assert.Equal(11, sent[14]);
        }
        finally
        {
            File.Delete(jar);
        }

        // Disposing closes the server's shell.
        await scrcpy.ShellClosed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Mirror_stream_that_ends_just_finishes()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        var scrcpy = new FakeScrcpy { Refusals = 0, StreamThenHangUp = true };
        server.Extra = scrcpy.HandleAsync;
        var channel = new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3));
        var jar = TempJar();
        try
        {
            await using var session = await ScrcpySession.StartAsync(channel, channel, jar, new MirrorOptions(), None);
            var count = 0;
            using var read = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var _ in session.ReadPacketsAsync(read.Token))
            {
                count++;
            }
            Assert.Equal(4, count);
        }
        finally
        {
            File.Delete(jar);
        }
    }

    [Fact]
    public async Task Mirroring_without_scrcpy_server_says_what_to_fetch()
    {
        var (device, _) = await DeviceTests.ReadyAsync("android-14-google-pixel-7");
        // The fake channel has no raw sockets: not supported, before looking for the server file.
        Assert.Equal(DeviceErrorCodes.NotSupported, (await Assert.ThrowsAsync<DeviceException>(() => device.StartMirrorAsync(new MirrorOptions(), None))).Code);

        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        var entry = new AdbDeviceEntry("R5CT", "device", TransportId: 3);
        var options = new AndroidProviderOptions { CacheDirectory = null, ScrcpyServerPath = Path.Combine(Path.GetTempPath(), "no-such-scrcpy-server") };
        var real = new AndroidDevice(new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3)), entry, new Apps.AppMetadataCache(null), options);
        server.Shell = command => command switch
        {
            "getprop" => FakeShellReply.Text("[ro.product.model]: [Pixel 7]\n[ro.build.version.sdk]: [34]\n[ro.build.version.release]: [14]\n"),
            _ => FakeShellReply.Text(""),
        };
        var profile = await real.LoadProfileAsync(None);
        real.SetSummary(Info.SummaryBuilder.Ready(entry, profile));
        var missing = await Assert.ThrowsAsync<DeviceException>(() => real.StartMirrorAsync(new MirrorOptions(), None));
        Assert.Equal(DeviceErrorCodes.DriverMissing, missing.Code);
        Assert.Contains("fetch-scrcpy-server.ps1", missing.Message, StringComparison.Ordinal);
    }

    // ---- wireless debugging ----

    [Theory]
    [InlineData("192.168.1.5", 37099, "192.168.1.5:37099")]
    [InlineData(" 10.0.0.2 ", 5555, "10.0.0.2:5555")]
    [InlineData("fe80::1", 40000, "[fe80::1]:40000")]
    [InlineData("pixel-7.local", 41000, "pixel-7.local:41000")]
    public void Addresses_are_formatted_for_adb(string host, int port, string expected) => Assert.Equal(expected, WirelessReplies.Address(host, port));

    [Theory]
    [InlineData("192.168.1.5;reboot", 1)]
    [InlineData("192.168.1.5", 0)]
    [InlineData("192.168.1.5", 70000)]
    public void Bad_addresses_are_refused(string host, int port) => Assert.Throws<ArgumentException>(() => WirelessReplies.Address(host, port));

    [Fact]
    public void Adb_answers_become_results_or_errors()
    {
        var paired = WirelessReplies.Pair("Successfully paired to 192.168.1.5:37099 [guid=adb-R5CT20ABCDE-a1B2c3]");
        Assert.Equal("adb-R5CT20ABCDE-a1B2c3", WirelessReplies.Guid(paired.Message));
        Assert.Equal(DeviceErrorCodes.Unauthorized, Assert.Throws<DeviceException>(() => WirelessReplies.Pair("Failed: Wrong password or connection was dropped.")).Code);
        Assert.Equal(DeviceErrorCodes.NotFound, Assert.Throws<DeviceException>(() => WirelessReplies.Pair("Failed: Unable to start pairing client.")).Code);
        Assert.Equal(DeviceErrorCodes.Protocol, Assert.Throws<DeviceException>(() => WirelessReplies.Pair("Failed: something new")).Code);

        Assert.Equal("192.168.1.5:41917", WirelessReplies.Connect("connected to 192.168.1.5:41917").Serial);
        Assert.Equal("192.168.1.5:41917", WirelessReplies.Connect("already connected to 192.168.1.5:41917").Serial);
        Assert.Equal(DeviceErrorCodes.Unauthorized, Assert.Throws<DeviceException>(() => WirelessReplies.Connect("failed to authenticate to 192.168.1.5:41917")).Code);
        Assert.Equal(DeviceErrorCodes.NotFound, Assert.Throws<DeviceException>(() => WirelessReplies.Connect("failed to connect to '192.168.1.5:41917': Connection refused")).Code);
    }

    private static AndroidProviderOptions Options(FakeAdbServer server) => new()
    {
        AdbPath = Path.Combine(Path.GetTempPath(), "no-such-adb.exe"),
        ServerPort = server.Port,
        CacheDirectory = null,
        StopServerOnDispose = false,
        ServerStarter = _ => Task.CompletedTask,
    };

    private static Func<string, NetworkStream, CancellationToken, Task<bool>> HostAnswers(Dictionary<string, string> answers) => async (request, stream, ct) =>
    {
        if (!answers.TryGetValue(request, out var answer))
        {
            return false;
        }
        var body = Encoding.UTF8.GetBytes(answer);
        await stream.WriteAsync("OKAY"u8.ToArray(), ct);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("x4")), ct);
        await stream.WriteAsync(body, ct);
        return true;
    };

    [Fact]
    public async Task Pair_connect_and_disconnect_go_through_the_adb_server()
    {
        await using var server = new FakeAdbServer();
        server.Extra = HostAnswers(new Dictionary<string, string>
        {
            ["host:pair:123456:192.168.1.5:37099"] = "Successfully paired to 192.168.1.5:37099 [guid=adb-R5CT-x]",
            ["host:pair:000000:192.168.1.5:37099"] = "Failed: Wrong password or connection was dropped.",
            ["host:connect:192.168.1.5:41917"] = "connected to 192.168.1.5:41917",
            ["host:disconnect:192.168.1.5:41917"] = "disconnected 192.168.1.5:41917",
        });
        await using var provider = new AndroidDeviceProvider(Options(server));

        Assert.Contains("Successfully paired", (await provider.PairAsync("192.168.1.5", 37099, "123456", None)).Message, StringComparison.Ordinal);
        Assert.Equal(DeviceErrorCodes.Unauthorized, (await Assert.ThrowsAsync<DeviceException>(() => provider.PairAsync("192.168.1.5", 37099, "000000", None))).Code);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.PairAsync("192.168.1.5", 37099, "12 34", None));
        Assert.Equal("192.168.1.5:41917", (await provider.ConnectAsync("192.168.1.5", 41917, None)).Serial);
        await provider.DisconnectAsync("192.168.1.5:41917", None);
        Assert.Equal(DeviceErrorCodes.NotSupported, (await Assert.ThrowsAsync<DeviceException>(() => provider.DisconnectAsync("R5CT20ABCDE", None))).Code);
    }

    /// <summary>A phone's mDNS responder on loopback: answers PTR queries for the services it was given, with SRV and A.</summary>
    private sealed class FakeResponder : IAsyncDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public FakeResponder() => _loop = Task.Run(LoopAsync);

        public ConcurrentDictionary<string, (string Instance, int Port)> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;

        public int Queries;

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await _udp.ReceiveAsync(_stop.Token);
                }
                catch (Exception)
                {
                    return;
                }
                Interlocked.Increment(ref Queries);
                var question = ReadQuestion(received.Buffer);
                if (Services.TryGetValue(question, out var service))
                {
                    var reply = Response(question, service.Instance, service.Port);
                    await _udp.SendAsync(reply, received.RemoteEndPoint);
                }
            }
        }

        private static string ReadQuestion(byte[] query)
        {
            var labels = new List<string>();
            var at = 12;
            while (query[at] != 0)
            {
                labels.Add(Encoding.UTF8.GetString(query, at + 1, query[at]));
                at += 1 + query[at];
            }
            return string.Join('.', labels);
        }

        private static byte[] Response(string service, string instance, int port)
        {
            using var m = new MemoryStream();
            m.Write([0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 2]); // response, 1 answer, 2 additional
            var instanceName = Name([instance, .. service.Split('.')]);
            var host = Name(["Pixel-7", "local"]);
            Record(m, Name(service.Split('.')), 12, instanceName);
            var srv = new byte[6 + host.Length];
            BinaryPrimitives.WriteUInt16BigEndian(srv.AsSpan(4), (ushort)port);
            host.CopyTo(srv, 6);
            Record(m, instanceName, 33, srv);
            Record(m, host, 1, [127, 0, 0, 1]);
            return m.ToArray();
        }

        private static byte[] Name(string[] labels)
        {
            using var m = new MemoryStream();
            foreach (var label in labels)
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                m.WriteByte((byte)bytes.Length);
                m.Write(bytes);
            }
            m.WriteByte(0);
            return m.ToArray();
        }

        private static void Record(MemoryStream m, byte[] owner, ushort type, byte[] data)
        {
            m.Write(owner);
            var fixedPart = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 0x8001);
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), 120);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)data.Length);
            m.Write(fixedPart);
            m.Write(data);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _udp.Dispose();
            try
            {
                await _loop;
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public void Queries_ask_for_a_unicast_answer()
    {
        var query = MdnsBrowser.BuildQuery("_adb-tls-pairing._tcp.local", 12);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)));
        Assert.Equal(0x8001, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 2)));
        Assert.Equal(12, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4)));
    }

    [Fact]
    public async Task Mdns_browse_finds_a_service_with_its_address_and_port()
    {
        await using var responder = new FakeResponder();
        responder.Services["_adb-tls-pairing._tcp.local"] = ("devicedeck-abc123", 37099);
        var found = await new MdnsBrowser(responder.EndPoint).BrowseAsync("_adb-tls-pairing._tcp", TimeSpan.FromMilliseconds(600), None);
        var service = Assert.Single(found);
        Assert.Equal(("devicedeck-abc123", IPAddress.Loopback, 37099), (service.Instance, service.Address, service.Port));
    }

    [Fact]
    public async Task Qr_pairing_finds_the_phone_pairs_then_connects()
    {
        await using var responder = new FakeResponder();
        responder.Services["_adb-tls-pairing._tcp.local"] = ("devicedeck-abc123", 37099);
        responder.Services["_adb-tls-connect._tcp.local"] = ("adb-R5CT-x", 41917);
        await using var server = new FakeAdbServer();
        server.Extra = HostAnswers(new Dictionary<string, string>
        {
            ["host:pair:Secret123:127.0.0.1:37099"] = "Successfully paired to 127.0.0.1:37099 [guid=adb-R5CT-x]",
            ["host:connect:127.0.0.1:41917"] = "connected to 127.0.0.1:41917",
        });
        await using var provider = new AndroidDeviceProvider(Options(server)) { Mdns = new MdnsBrowser(responder.EndPoint) };
        var stages = new ConcurrentQueue<string>();

        var result = await provider.PairWithQrAsync("devicedeck-abc123", "Secret123", new SyncProgress(stages.Enqueue), None);

        Assert.Equal("127.0.0.1:41917", result.Serial);
        Assert.Equal(["waiting", "pairing", "connecting"], stages);
    }

    [Fact]
    public async Task Qr_pairing_keeps_waiting_until_cancelled()
    {
        await using var responder = new FakeResponder();
        await using var server = new FakeAdbServer();
        await using var provider = new AndroidDeviceProvider(Options(server)) { Mdns = new MdnsBrowser(responder.EndPoint) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.PairWithQrAsync("devicedeck-nobody", "x", null, cts.Token));
        Assert.True(responder.Queries >= 2);
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
