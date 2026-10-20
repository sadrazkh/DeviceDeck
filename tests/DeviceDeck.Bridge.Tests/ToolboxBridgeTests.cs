using System.Buffers.Binary;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Fake;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The toolbox methods end to end on the sample phones: features, live log, crash reports, report, HEIC, Wi-Fi, ringtones.</summary>
public sealed class ToolboxBridgeTests
{
    private const string Iphone = FakeSampleData.IphoneId;
    private const string Android = FakeSampleData.AndroidId;

    private static Task<TestHost> StartAsync() => TestHost.StartAsync(FakeSampleData.Iphone(), FakeSampleData.Android());

    private static string? Code(JsonObject response) => (string?)response["error"]?["data"]?["code"];

    [Fact]
    public async Task Features_follow_platform_and_capabilities()
    {
        await using var host = await StartAsync();
        var iphone = (await host.ResultAsync("toolbox.features", new { deviceId = Iphone }))!.AsObject();
        Assert.Equal([true, true, false, false, true, true, false],
            new[] { "log", "crashReports", "mirror", "ringtone", "wifiSync", "report", "wirelessDebugging" }.Select(k => (bool)iphone[k]!));
        var android = (await host.ResultAsync("toolbox.features", new { deviceId = Android }))!.AsObject();
        Assert.Equal([true, true, true, true, false], new[] { "log", "crashReports", "mirror", "ringtone", "wifiSync" }.Select(k => (bool)android[k]!));
    }

    [Fact]
    public async Task Live_log_comes_in_order_saves_with_the_filter_and_stops()
    {
        await using var host = await StartAsync();
        var session = (string)(await host.ResultAsync("log.start", new { deviceId = Android }))!["sessionId"]!;
        List<JsonObject> Lines() => host.Transport.EventsNamed("log.lines")
            .Where(e => (string?)e["params"]!["sessionId"] == session)
            .SelectMany(e => e["params"]!["lines"]!.AsArray().Select(l => l!.AsObject())).ToList();
        await Wait.UntilAsync(() => Lines().Count >= 20, "20 log lines");

        var seqs = Lines().Select(l => (long)l["seq"]!).ToList();
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);
        Assert.Contains(Lines(), l => (string?)l["level"] is "info" or "debug" or "verbose");

        var saved = (await host.ResultAsync("log.export", new { sessionId = session, minLevel = "error" }))!;
        var path = (string)saved["path"]!;
        Assert.StartsWith(Path.Combine(host.Root, "out", "Logs"), path, StringComparison.OrdinalIgnoreCase);
        var text = await File.ReadAllLinesAsync(path);
        Assert.Equal((int)saved["count"]!, text.Length);
        Assert.All(text, line => Assert.Matches(@"^\S+ \S+ [EF] ", line));

        await host.ResultAsync("log.stop", new { sessionId = session });
        var after = host.Transport.EventsNamed("log.lines").Count();
        await Task.Delay(300);
        Assert.Equal(after, host.Transport.EventsNamed("log.lines").Count());
        Assert.Equal("path_not_found", Code(await host.CallAsync("log.export", new { sessionId = session })));
    }

    [Fact]
    public async Task Unplugging_ends_the_log_but_its_lines_can_still_be_saved()
    {
        await using var host = await StartAsync();
        var session = (string)(await host.ResultAsync("log.start", new { deviceId = Iphone }))!["sessionId"]!;
        await Wait.UntilAsync(() => host.Transport.EventsNamed("log.lines").Any(), "first lines");
        host.Fake.Disconnect(Iphone);
        await Wait.UntilAsync(() => host.Transport.EventsNamed("log.ended").Any(), "log.ended");
        var ended = host.Transport.EventsNamed("log.ended").Single()["params"]!;
        Assert.Equal(session, (string?)ended["sessionId"]);
        Assert.Equal("device_disconnected", (string?)ended["error"]!["code"]);
        Assert.True((int)(await host.ResultAsync("log.export", new { sessionId = session }))!["count"]! > 0);
    }

    [Fact]
    public async Task Crash_reports_list_read_save_and_delete_on_an_iPhone_only()
    {
        await using var host = await StartAsync();
        var list = (await host.ResultAsync("crash.list", new { deviceId = Iphone }))!;
        Assert.True((bool)list["canDelete"]!);
        var reports = list["reports"]!.AsArray();
        Assert.Equal(5, reports.Count);
        var first = (string)reports[0]!["id"]!;
        Assert.Equal("crash", (string?)reports[0]!["kind"]);

        var text = (await host.ResultAsync("crash.read", new { deviceId = Iphone, id = first }))!;
        Assert.StartsWith("Process: Instagram", (string)text["text"]!, StringComparison.Ordinal);
        Assert.False((bool)text["truncated"]!);

        var saved = (await host.ResultAsync("crash.export", new { deviceId = Iphone, ids = new[] { first, "Retired/Telegram-2026-10-17-093012.ips" } }))!;
        var files = saved["files"]!.AsArray().Select(f => (string)f!).ToList();
        Assert.Equal(["Instagram-2026-10-19-201102.ips", "Telegram-2026-10-17-093012.ips"], files.Select(Path.GetFileName));
        Assert.All(files, f => Assert.True(File.Exists(f)));
        // Saving again never overwrites: the second copy gets a number.
        var again = (await host.ResultAsync("crash.export", new { deviceId = Iphone, ids = new[] { first }, destination = (string)saved["folder"]! }))!;
        Assert.Equal("Instagram-2026-10-19-201102 (2).ips", Path.GetFileName((string)again["files"]![0]!));

        Assert.Equal(1, (int)(await host.ResultAsync("crash.delete", new { deviceId = Iphone, ids = new[] { first } }))!["deleted"]!);
        Assert.Equal(4, (await host.ResultAsync("crash.list", new { deviceId = Iphone }))!["reports"]!.AsArray().Count);

        var android = (await host.ResultAsync("crash.list", new { deviceId = Android }))!;
        Assert.False((bool)android["canDelete"]!);
        Assert.Equal(["tombstones", "anr"], android["unreadable"]!.AsArray().Select(p => (string)p!));
        var androidId = (string)android["reports"]![0]!["id"]!;
        Assert.Equal("permission_denied", Code(await host.CallAsync("crash.delete", new { deviceId = Android, ids = new[] { androidId } })));
        Assert.Equal("data_app_crash-1760896440000.txt", Path.GetFileName(
            (string)(await host.ResultAsync("crash.export", new { deviceId = Android, ids = new[] { androidId } }))!["files"]![0]!));
    }

    [Fact]
    public async Task Report_hides_serials_unless_revealed_and_saves_html_and_pdf()
    {
        await using var host = await StartAsync();
        var masked = (string)(await host.ResultAsync("report.preview", new { deviceId = Iphone, language = "en", revealSerials = false }))!["html"]!;
        Assert.DoesNotContain("F2LZK9X1Q7", masked, StringComparison.Ordinal);
        Assert.Contains("••••••••••", masked, StringComparison.Ordinal);
        Assert.Contains("iPhone 17 Pro", masked, StringComparison.Ordinal);
        Assert.Contains("Health (estimated)", masked, StringComparison.Ordinal);

        var revealed = (string)(await host.ResultAsync("report.preview", new { deviceId = Iphone, language = "en", revealSerials = true }))!["html"]!;
        Assert.Contains("F2LZK9X1Q7", revealed, StringComparison.Ordinal);

        var persian = (string)(await host.ResultAsync("report.preview", new { deviceId = Android, language = "fa", revealSerials = false }))!["html"]!;
        Assert.Contains("dir=\"rtl\"", persian, StringComparison.Ordinal);
        Assert.Contains("گیگابایت", persian, StringComparison.Ordinal);
        Assert.Contains("۵۱۲", persian, StringComparison.Ordinal);

        var html = (string)(await host.ResultAsync("report.save", new { deviceId = Iphone, language = "en", revealSerials = false, format = "html" }))!["path"]!;
        Assert.EndsWith(".html", html, StringComparison.Ordinal);
        Assert.StartsWith("<!doctype html>", await File.ReadAllTextAsync(html), StringComparison.Ordinal);
        var pdf = (string)(await host.ResultAsync("report.save", new { deviceId = Iphone, language = "en", revealSerials = false, format = "pdf" }))!["path"]!;
        Assert.Equal("%PDF-fake", await File.ReadAllTextAsync(pdf));
        Assert.Single(host.Toolbox.Pdf.Printed);
    }

    private static string MakePhotos(TestHost host)
    {
        var folder = host.PcFolder("Trip");
        File.WriteAllBytes(Path.Combine(folder, "IMG_0001.HEIC"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(folder, "Day 2"));
        File.WriteAllBytes(Path.Combine(folder, "Day 2", "IMG_0002.heic"), [4, 5]);
        File.WriteAllBytes(Path.Combine(folder, "broken.heic"), [6]);
        File.WriteAllBytes(Path.Combine(folder, "already.jpg"), [7]);
        File.WriteAllBytes(Path.Combine(folder, "IMG_0001.jpg"), [8]); // a JPG of that name is already there
        return folder;
    }

    private static async Task<JsonNode> FinishedJobAsync(TestHost host, string jobId)
    {
        await Wait.UntilAsync(() => host.Transport.EventsNamed("toolbox.job").Any(e => (string?)e["params"]!["id"] == jobId && (string?)e["params"]!["state"] != "running"), "the HEIC job to end");
        return host.Transport.EventsNamed("toolbox.job").Last(e => (string?)e["params"]!["id"] == jobId)["params"]!;
    }

    [Fact]
    public async Task Heic_job_converts_a_folder_next_to_the_originals_and_reports_failures()
    {
        await using var host = await StartAsync();
        var folder = MakePhotos(host);
        var started = (await host.ResultAsync("heic.convert", new { files = new[] { folder }, quality = 85 }))!;
        Assert.Equal(3, (int)started["total"]!);

        var job = await FinishedJobAsync(host, (string)started["jobId"]!);
        Assert.Equal(("done", 2, 1), ((string?)job["state"], (int)job["done"]!, (int)job["failed"]!));
        Assert.Contains("broken.heic", (string)job["errors"]![0]!, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "IMG_0001 (2).jpg")));
        Assert.True(File.Exists(Path.Combine(folder, "Day 2", "IMG_0002.jpg")));
        Assert.Equal([8], await File.ReadAllBytesAsync(Path.Combine(folder, "IMG_0001.jpg"))); // untouched
        Assert.True(File.Exists(Path.Combine(folder, "IMG_0001.HEIC"))); // originals stay
        Assert.Empty(Directory.GetFiles(folder, "*.ddpart", SearchOption.AllDirectories));
        Assert.All(host.Toolbox.Heic.Calls, c => Assert.Equal(85, c.Quality));
    }

    [Fact]
    public async Task Heic_job_into_a_chosen_folder_keeps_the_dropped_folder_shape()
    {
        await using var host = await StartAsync();
        var folder = MakePhotos(host);
        var target = host.PcFolder("Converted");
        var started = (await host.ResultAsync("heic.convert", new { files = new[] { folder }, quality = 90, destination = target }))!;
        await FinishedJobAsync(host, (string)started["jobId"]!);
        Assert.True(File.Exists(Path.Combine(target, "Trip", "IMG_0001.jpg")));
        Assert.True(File.Exists(Path.Combine(target, "Trip", "Day 2", "IMG_0002.jpg")));
    }

    [Fact]
    public async Task Heic_job_can_be_cancelled_and_says_why_it_cannot_start()
    {
        await using var host = await StartAsync();
        var folder = MakePhotos(host);
        host.Toolbox.Heic.Gate = new TaskCompletionSource();
        var jobId = (string)(await host.ResultAsync("heic.convert", new { files = new[] { folder }, quality = 90 }))!["jobId"]!;
        await host.ResultAsync("toolbox.cancelJob", new { jobId });
        var job = await FinishedJobAsync(host, jobId);
        Assert.Equal("cancelled", (string?)job["state"]);
        Assert.Empty(Directory.GetFiles(folder, "*.ddpart", SearchOption.AllDirectories));
        Assert.Equal("path_not_found", Code(await host.CallAsync("toolbox.cancelJob", new { jobId })));

        host.Toolbox.Heic.Gate = null;
        Assert.Equal(-32602, (int)(await host.CallAsync("heic.convert", new { files = new[] { folder }, quality = 0 }))["error"]!["code"]!);
        Assert.Equal(-32602, (int)(await host.CallAsync("heic.convert", new { files = new[] { "relative\\path" }, quality = 90 }))["error"]!["code"]!);
        Assert.Equal("path_not_found", Code(await host.CallAsync("heic.convert", new { files = new[] { host.PcFolder("empty") }, quality = 90 })));
        host.Toolbox.Heic.Available = false;
        Assert.Equal("driver_missing", Code(await host.CallAsync("heic.convert", new { files = new[] { folder }, quality = 90 })));
        Assert.False((bool)(await host.ResultAsync("heic.status"))!["available"]!);
    }

    [Fact]
    public async Task Mirror_opens_the_window_for_a_phone_that_mirrors()
    {
        await using var host = await StartAsync();
        await host.ResultAsync("mirror.open", new { deviceId = Android });
        Assert.Equal([Android], host.Toolbox.Mirrors.Opened);
        Assert.Equal("not_supported", Code(await host.CallAsync("mirror.open", new { deviceId = Iphone })));
    }

    [Fact]
    public async Task Wifi_needs_the_android_provider_and_the_iphone_switch_round_trips()
    {
        await using var host = await StartAsync();
        Assert.False((bool)(await host.ResultAsync("wifi.status"))!["wirelessDebugging"]!);
        Assert.Equal("not_supported", Code(await host.CallAsync("wifi.pair", new { host = "192.168.1.5", port = 37099, code = "123456" })));
        Assert.Equal("not_supported", Code(await host.CallAsync("wifi.qrStart")));

        Assert.False((bool)(await host.ResultAsync("wifi.iphoneSync", new { deviceId = Iphone }))!["enabled"]!);
        Assert.True((bool)(await host.ResultAsync("wifi.setIphoneSync", new { deviceId = Iphone, enabled = true }))!["enabled"]!);
        Assert.Equal("not_supported", Code(await host.CallAsync("wifi.iphoneSync", new { deviceId = Android })));
    }

    private static string MakeWav(string folder, string name, int seconds)
    {
        const int rate = 8000;
        var data = rate * 2 * seconds;
        var wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(36 + data));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), rate);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), (uint)data);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, wav);
        return path;
    }

    [Fact]
    public async Task Ringtone_is_cut_on_the_pc_and_put_in_the_android_ringtones_folder()
    {
        await using var host = await StartAsync();
        var song = MakeWav(host.PcFolder("Music"), "Alarm song.wav", 10);

        var made = (await host.ResultAsync("ringtone.create", new { deviceId = Android, files = new[] { song }, startSeconds = 2.0, endSeconds = 5.0, name = "Wake up", setAsDefault = true }))!;
        Assert.Equal(("/sdcard/Ringtones/Wake up.wav", true, 44L + 3 * 16000), ((string?)made["devicePath"], (bool)made["isDefault"]!, (long)made["bytes"]!));
        var listed = (await host.ResultAsync("files.list", new { deviceId = Android, root = "internal", path = "/Ringtones" }))!.AsArray();
        Assert.Contains(listed, f => (string?)f!["name"] == "Wake up.wav" && (long)f["size"]! == 48044);
        Assert.Empty(Directory.GetFiles(Path.Combine(host.Root, "temp")));
        Assert.True(File.Exists(song));

        Assert.Equal("not_supported", Code(await host.CallAsync("ringtone.create", new { deviceId = Iphone, files = new[] { song }, startSeconds = 0.0, endSeconds = 5.0, setAsDefault = false })));
        var ogg = Path.Combine(host.PcFolder("Music"), "song.ogg");
        await File.WriteAllBytesAsync(ogg, [1, 2, 3]);
        Assert.Equal("not_supported", Code(await host.CallAsync("ringtone.create", new { deviceId = Android, files = new[] { ogg }, startSeconds = 0.0, endSeconds = 5.0, setAsDefault = false })));
        Assert.Equal(-32602, (int)(await host.CallAsync("ringtone.create", new { deviceId = Android, files = new[] { song }, startSeconds = 5.0, endSeconds = 2.0, setAsDefault = false }))["error"]!["code"]!);
    }
}
