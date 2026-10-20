using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Bridge.Toolbox;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Toolbox;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The toolbox's pure parts: device report, WAV/MP3 trimming, HEIC planning and log sessions.</summary>
public sealed class ToolboxCoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-toolbox-core-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- device report ----

    private static DeviceReportData Data(bool withStorage = true)
    {
        var spec = FakeSampleData.Android();
        return new DeviceReportData(spec.Summary, spec.Details, withStorage ? spec.Storage : null, spec.Battery);
    }

    [Fact]
    public void Report_masks_sensitive_rows_and_lists_storage_and_battery()
    {
        var at = new DateTimeOffset(2026, 10, 20, 16, 44, 0, TimeSpan.FromHours(3.5));
        var html = DeviceReport.ToHtml(Data(), "en", revealSensitive: false, at, "0.2.0");
        Assert.StartsWith("<!doctype html><html lang=\"en\" dir=\"ltr\">", html, StringComparison.Ordinal);
        Assert.Contains("<h1>Galaxy S25 Ultra</h1>", html, StringComparison.Ordinal);
        Assert.Contains("Android version", html, StringComparison.Ordinal);
        Assert.Contains("512 GB", html, StringComparison.Ordinal);
        Assert.Contains("Charge cycles", html, StringComparison.Ordinal);
        Assert.Contains("class=\"c-photos\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSampleData.AndroidId, html, StringComparison.Ordinal);
        Assert.DoesNotContain("35 881210", html, StringComparison.Ordinal);
        Assert.Contains("•• •••••• •••••• •", html, StringComparison.Ordinal);
        Assert.Contains("Serial numbers and similar values are hidden", html, StringComparison.Ordinal);
        Assert.Contains("20 October 2026, 16:44", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        var open = DeviceReport.ToHtml(Data(), "en", revealSensitive: true, at, "0.2.0");
        Assert.Contains(FakeSampleData.AndroidId, open, StringComparison.Ordinal);
        Assert.DoesNotContain("are hidden", open, StringComparison.Ordinal);
    }

    [Fact]
    public void Persian_report_is_right_to_left_with_persian_digits_and_dates()
    {
        var at = new DateTimeOffset(2026, 10, 20, 16, 44, 0, TimeSpan.FromHours(3.5));
        var html = DeviceReport.ToHtml(Data(withStorage: false), "fa", revealSensitive: false, at, "0.2.0");
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("۲۸ مهر ۱۴۰۵", html, StringComparison.Ordinal);
        Assert.Contains("DeviceDeck ۰.۲.۰", html, StringComparison.Ordinal);
        Assert.Contains("در دسترس نیست", html, StringComparison.Ordinal); // no storage
        Assert.Contains("۶۴٪", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_mask_and_file_name()
    {
        Assert.Equal("•••• ••••:•• ••-••", DeviceReport.Mask("F2LZ K9X1:Q7 AB-CD"));
        var summary = FakeSampleData.Iphone().Summary with { Name = "Nima's \"phone\"/2" };
        Assert.Equal("DeviceDeck report – Nima's _phone__2 – 2026-10-20 1644.pdf",
            DeviceReport.FileName(summary, new DateTimeOffset(2026, 10, 20, 16, 44, 0, TimeSpan.Zero), ".pdf"));
    }

    [Fact]
    public async Task Gathering_a_report_skips_parts_the_phone_cannot_give()
    {
        await using var provider = new FakeDeviceProvider(FakeOptions.ForTests(FakeSampleData.Iphone()));
        await provider.StartAsync(CancellationToken.None);
        var data = await DeviceReport.GatherAsync(provider.Devices[0], CancellationToken.None);
        Assert.NotNull(data.Storage);
        Assert.NotNull(data.Battery);
        Assert.NotNull(data.Details);
    }

    // ---- audio trimming ----

    private static byte[] Wav(int rate, short channels, int seconds)
    {
        var align = channels * 2;
        var data = rate * align * seconds;
        var wav = new byte[44 + 10 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), (uint)(rate * align));
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), (ushort)align);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        // A LIST chunk before data, as many encoders write one.
        "LIST"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), 2);
        "data"u8.CopyTo(wav.AsSpan(46));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(50), (uint)data);
        for (var i = 0; i < data; i++)
        {
            wav[54 + i] = (byte)(i / align);
        }

        return wav;
    }

    [Fact]
    public void Wav_trim_keeps_whole_sample_frames_and_the_format()
    {
        var wav = Wav(44_100, 2, 4);
        Assert.Equal(4.0, ManagedAudioTrimmer.DurationSeconds(wav, ".wav"));
        var cut = ManagedAudioTrimmer.TrimWav(wav, TimeSpan.FromSeconds(1.00001), TimeSpan.FromSeconds(2.5));
        Assert.Equal(1.5, ManagedAudioTrimmer.DurationSeconds(cut, ".wav")!.Value, 3);
        var dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(cut.AsSpan(40));
        Assert.Equal(0u, dataBytes % 4);
        Assert.Equal(cut.Length - 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(cut.AsSpan(4)));
        Assert.Equal(wav.AsSpan(20, 16).ToArray(), cut.AsSpan(20, 16).ToArray());
        Assert.Equal(unchecked((byte)44_100), cut[44]); // first kept frame is frame 44,100

        Assert.Throws<InvalidDataException>(() => ManagedAudioTrimmer.TrimWav("not a wav file"u8, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Throws<InvalidDataException>(() => ManagedAudioTrimmer.TrimWav(wav, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10)));
    }

    /// <summary>MPEG-1 layer III, 128 kbit/s, 44.1 kHz: 417-byte frames of 1152 samples (26.1 ms).</summary>
    private static byte[] Mp3(int frames, bool id3 = true, bool xing = true, bool tag = true)
    {
        using var m = new MemoryStream();
        if (id3)
        {
            m.Write("ID3"u8);
            m.Write([4, 0, 0, 0, 0, 0, 20]); // v2.4, no flags, size 20 (syncsafe)
            m.Write(new byte[20]);
        }

        void Frame(byte fill, bool isXing)
        {
            var frame = new byte[417];
            frame[0] = 0xFF;
            frame[1] = 0xFB;
            frame[2] = 0x90;
            frame[3] = 0x00;
            for (var i = 4; i < frame.Length; i++)
            {
                frame[i] = fill;
            }

            if (isXing)
            {
                "Xing"u8.CopyTo(frame.AsSpan(36));
            }

            m.Write(frame);
        }

        if (xing)
        {
            Frame(0, true);
        }

        for (var i = 0; i < frames; i++)
        {
            Frame((byte)(i % 200 + 1), false);
        }

        if (tag)
        {
            m.Write("TAG"u8);
            m.Write(new byte[125]);
        }

        return m.ToArray();
    }

    [Fact]
    public void Mp3_frames_are_found_past_id3_and_without_the_xing_frame()
    {
        var mp3 = Mp3(100);
        var frames = ManagedAudioTrimmer.ReadFrames(mp3);
        Assert.Equal(100, frames.Count);
        Assert.All(frames, f => Assert.Equal((417, 44_100, 1152), (f.Length, f.SampleRate, f.Samples)));
        Assert.Equal(100 * 1152 / 44_100.0, ManagedAudioTrimmer.DurationSeconds(mp3, ".mp3")!.Value, 6);

        var cut = ManagedAudioTrimmer.TrimMp3(mp3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.Equal(0, cut.Length % 417);
        Assert.InRange(cut.Length / 417, 37, 39);
        Assert.Equal(0xFF, cut[0]);
        Assert.Equal(ManagedAudioTrimmer.ReadFrames(cut).Count, cut.Length / 417);
        Assert.Throws<InvalidDataException>(() => ManagedAudioTrimmer.TrimMp3("no frames here at all"u8, TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Trimmer_writes_next_to_the_name_it_was_given()
    {
        var source = Path.Combine(_dir, "song.mp3");
        await File.WriteAllBytesAsync(source, Mp3(200, id3: false, xing: false, tag: false));
        var trimmer = new ManagedAudioTrimmer();
        Assert.True(trimmer.CanTrim(".MP3"));
        Assert.False(trimmer.CanTrim(".m4a"));
        var written = await trimmer.TrimAsync(source, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), Path.Combine(_dir, "cut"), CancellationToken.None);
        Assert.Equal(Path.Combine(_dir, "cut.mp3"), written);
        Assert.InRange(new FileInfo(written).Length / 417, 37, 39);
        await Assert.ThrowsAsync<ArgumentException>(() => trimmer.TrimAsync(source, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), Path.Combine(_dir, "x"), CancellationToken.None));
    }

    // ---- HEIC planning ----

    [Fact]
    public void Heic_plan_finds_photos_in_folders_and_never_reuses_a_name()
    {
        var trip = Directory.CreateDirectory(Path.Combine(_dir, "Trip")).FullName;
        Directory.CreateDirectory(Path.Combine(trip, "Day 2"));
        File.WriteAllBytes(Path.Combine(trip, "IMG_1.HEIC"), [1]);
        File.WriteAllBytes(Path.Combine(trip, "IMG_1.heif"), [2]); // same stem, other extension
        File.WriteAllBytes(Path.Combine(trip, "IMG_1.jpg"), [3]);  // taken on disk
        File.WriteAllBytes(Path.Combine(trip, "Day 2", "IMG_2.hif"), [4]);
        File.WriteAllBytes(Path.Combine(trip, "notes.txt"), [5]);

        var nextTo = HeicBatch.Plan([trip, Path.Combine(trip, "IMG_1.HEIC")], null);
        Assert.Equal(3, nextTo.Count); // the file given twice counts once
        Assert.Equal(
            [Path.Combine(trip, "Day 2", "IMG_2.jpg"), Path.Combine(trip, "IMG_1 (2).jpg"), Path.Combine(trip, "IMG_1 (3).jpg")],
            nextTo.Select(i => i.Destination).Order(StringComparer.OrdinalIgnoreCase));

        var target = Path.Combine(_dir, "Out");
        var into = HeicBatch.Plan([trip], target);
        Assert.Contains(into, i => i.Destination == Path.Combine(target, "Trip", "Day 2", "IMG_2.jpg"));
        Assert.Contains(into, i => i.Destination == Path.Combine(target, "Trip", "IMG_1.jpg"));
        Assert.Contains(into, i => i.Destination == Path.Combine(target, "Trip", "IMG_1 (2).jpg"));
        Assert.Empty(HeicBatch.Plan([Path.Combine(trip, "notes.txt"), Path.Combine(_dir, "missing")], null));
    }

    // ---- log sessions ----

    private sealed class ScriptedLog(int count, DeviceException? end = null) : IDeviceLogSource
    {
        public async IAsyncEnumerable<DeviceLogEntry> StreamLogAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            for (var i = 1; i <= count; i++)
            {
                yield return new DeviceLogEntry(null, i % 10 == 0 ? DeviceLogLevel.Error : DeviceLogLevel.Info, "proc", i, null, $"line {i}");
            }

            if (end is not null)
            {
                throw end;
            }
        }
    }

    [Fact]
    public async Task Log_session_batches_in_order_drops_a_backlog_for_the_page_and_keeps_lines_for_export()
    {
        var events = new List<(string Method, object Params)>();
        var options = new ToolboxServices { LogFlushInterval = TimeSpan.FromHours(1), LogCapacity = 7000 };
        var sessions = new LogSessions();
        var session = sessions.Start("phone", new ScriptedLog(6000, new DeviceException(DeviceErrorCodes.Disconnected, "gone")),
            (m, p) =>
            {
                lock (events)
                {
                    events.Add((m, p));
                }
            }, options, CancellationToken.None);

        await Wait.UntilAsync(() =>
        {
            lock (events)
            {
                return events.Any(e => e.Method == "log.ended");
            }
        }, "log.ended");

        List<LogLinesEvent> batches;
        lock (events)
        {
            batches = events.Where(e => e.Method == "log.lines").Select(e => (LogLinesEvent)e.Params).ToList();
            Assert.Equal("device_disconnected", ((LogEndedEvent)events.Last().Params).Error!.Code);
        }

        Assert.All(batches, b => Assert.InRange(b.Lines.Count, 1, LogSession.MaxBatch));
        Assert.Equal(1000, batches.Sum(b => b.Dropped)); // 6000 lines, 5000 kept for the page
        var seqs = batches.SelectMany(b => b.Lines).Select(l => l.Seq).ToList();
        Assert.Equal(Enumerable.Range(1001, 5000).Select(i => (long)i), seqs);

        var all = Path.Combine(_dir, "all.txt");
        Assert.Equal(6000, await session.ExportAsync(all, DeviceLogLevel.Verbose, null, CancellationToken.None));
        var errors = Path.Combine(_dir, "errors.txt");
        Assert.Equal(600, await session.ExportAsync(errors, DeviceLogLevel.Error, null, CancellationToken.None));
        Assert.Equal(1, await session.ExportAsync(Path.Combine(_dir, "one.txt"), DeviceLogLevel.Verbose, "line 4321", CancellationToken.None));
        Assert.StartsWith("- I proc[1]: line 1", (await File.ReadAllLinesAsync(all))[0], StringComparison.Ordinal);
        Assert.True(session.Ended);
        session.Clear();
        Assert.Empty(session.Snapshot());
        await sessions.StopAsync(session.Id);
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public async Task Log_ring_keeps_only_the_newest_lines()
    {
        var sessions = new LogSessions();
        var session = sessions.Start("phone", new ScriptedLog(500), (_, _) => { }, new ToolboxServices { LogCapacity = 100, LogFlushInterval = TimeSpan.FromMilliseconds(10) }, CancellationToken.None);
        await Wait.UntilAsync(() => session.Ended, "the scripted log to end");
        var kept = session.Snapshot();
        Assert.Equal(Enumerable.Range(401, 100).Select(i => (long)i), kept.Select(l => l.Seq));
        await sessions.StopAsync(session.Id);
    }

    [Fact]
    public async Task A_reloaded_page_stops_its_log_sessions()
    {
        using var page = new CancellationTokenSource();
        var sessions = new LogSessions();
        var session = sessions.Start("phone", new EndlessLog(), (_, _) => { }, new ToolboxServices(), page.Token);
        await page.CancelAsync();
        await Wait.UntilAsync(() => session.Ended && sessions.Count == 0, "the session to end with the page");
    }

    private sealed class EndlessLog : IDeviceLogSource
    {
        public async IAsyncEnumerable<DeviceLogEntry> StreamLogAsync([EnumeratorCancellation] CancellationToken ct)
        {
            while (true)
            {
                await Task.Delay(5, ct);
                yield return new DeviceLogEntry(null, DeviceLogLevel.Info, "p", 1, null, "tick");
            }
        }
    }
}
