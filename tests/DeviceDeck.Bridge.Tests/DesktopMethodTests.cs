using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>Round 2, desktop: device.rename, transfers.retry / clear, notifications, pc.*, the video route and StateDetail.</summary>
public sealed class DesktopMethodTests
{
    [Fact]
    public async Task Rename_changes_the_name_and_announces_it()
    {
        await using var host = await TestHost.StartAsync();
        var summary = await host.ResultAsync("device.rename", new { deviceId = TestPhones.IphoneId, name = "  Sara's iPhone " });

        Assert.Equal("Sara's iPhone", (string?)summary!["name"]);
        Assert.Contains("rename", summary["capabilities"]!.AsArray().Select(c => (string?)c));
        await Wait.UntilAsync(() => host.Transport.EventsNamed("device.changed").Any(e => (string?)e["params"]!["name"] == "Sara's iPhone"), "device.changed");
        Assert.Equal("Sara's iPhone", host.Hub.ListDevices().Single(d => d.Id == TestPhones.IphoneId).Name);
    }

    [Fact]
    public async Task Rename_refuses_an_empty_name_and_a_missing_phone()
    {
        await using var host = await TestHost.StartAsync();
        var empty = await host.CallAsync("device.rename", new { deviceId = TestPhones.IphoneId, name = "   " });
        Assert.Equal(-32602, (int?)empty["error"]!["code"]);

        var gone = await host.CallAsync("device.rename", new { deviceId = "nope", name = "X" });
        Assert.Equal("device_not_found", (string?)gone["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Retry_runs_the_failed_items_of_a_job_again()
    {
        await using var host = await TestHost.StartAsync();
        var destination = host.PcFolder("retry");
        var blocker = Path.Combine(destination, "Movie.mkv");
        Directory.CreateDirectory(blocker); // a folder where the file goes: the item fails
        host.Settings.Update(new JsonObject { ["conflictRule"] = "replace" });
        var started = await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", paths = new[] { "/Movie.mkv" }, destination });
        var jobId = (string)started!["jobId"]!;
        await Wait.UntilAsync(() => host.Queue.Get(jobId)!.State == TransferState.Failed, "the job to fail");

        Directory.Delete(blocker);
        Assert.True((await host.CallAsync("transfers.retry", new { jobId })).ContainsKey("result"));
        await Wait.UntilAsync(() => host.Queue.Get(jobId)!.State == TransferState.Done, "the retried job to finish");
        Assert.Equal(300_000, new FileInfo(blocker).Length);
        Assert.Equal(0, host.Queue.Get(jobId)!.FailedItems);
    }

    [Fact]
    public async Task Retry_on_a_job_that_is_done_is_refused()
    {
        await using var host = await TestHost.StartAsync();
        var started = await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", paths = new[] { "/Subtitles" }, destination = host.PcFolder("done") });
        var jobId = (string)started!["jobId"]!;
        await Wait.UntilAsync(() => host.Queue.Get(jobId)!.State == TransferState.Done, "the job to finish");

        var retry = await host.CallAsync("transfers.retry", new { jobId });
        Assert.Equal("internal", (string?)retry["error"]!["data"]!["code"]);
        Assert.Contains("nothing to try again", (string?)retry["error"]!["message"]);
    }

    [Fact]
    public async Task Clear_removes_finished_jobs_only()
    {
        await using var host = await TestHost.StartAsync(bytesPerSecond: 200_000, options: null);
        var done = (string)(await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", paths = new[] { "/Subtitles" }, destination = host.PcFolder("a") }))!["jobId"]!;
        await Wait.UntilAsync(() => host.Queue.Get(done)!.State == TransferState.Done, "the small job to finish");
        var running = (string)(await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", paths = new[] { "/Movie.mkv" }, destination = host.PcFolder("b") }))!["jobId"]!;

        var cleared = await host.ResultAsync("transfers.clear", new { });
        Assert.Equal(1, (int?)cleared!["cleared"]);
        Assert.Null(host.Queue.Get(done));
        Assert.NotNull(host.Queue.Get(running));
        Assert.DoesNotContain(host.Store.LoadAll(), j => j.Id == done);
        host.Queue.Cancel(running);
    }

    [Fact]
    public async Task Clear_with_ids_leaves_the_other_finished_jobs()
    {
        await using var host = await TestHost.StartAsync();
        var first = (string)(await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", paths = new[] { "/Subtitles" }, destination = host.PcFolder("a") }))!["jobId"]!;
        var second = (string)(await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", paths = new[] { "/Subtitles" }, destination = host.PcFolder("b") }))!["jobId"]!;
        await Wait.UntilAsync(() => host.Queue.Get(first)!.State == TransferState.Done && host.Queue.Get(second)!.State == TransferState.Done, "both jobs to finish");

        var cleared = await host.ResultAsync("transfers.clear", new { jobIds = new[] { first, "unknown" } });
        Assert.Equal(1, (int?)cleared!["cleared"]);
        Assert.Null(host.Queue.Get(first));
        Assert.NotNull(host.Queue.Get(second));
    }

    [Fact]
    public async Task Notifications_show_goes_to_the_service_and_a_click_reaches_the_page()
    {
        var notifications = new RecordingNotificationService();
        await using var host = await TestHost.StartAsync(notifications);
        await host.ResultAsync("notifications.show", new
        {
            title = "12 new photos",
            body = "On Nima's iPhone",
            tag = "photos:x",
            deviceId = TestPhones.IphoneId,
            buttons = new[] { new { label = "Back up", command = "photos.backup" } },
        });

        var shown = Assert.Single(notifications.Shown);
        Assert.Equal("12 new photos", shown.Title);
        Assert.Equal(NotificationCommands.Open, shown.Command);
        Assert.Equal("photos.backup", Assert.Single(shown.Buttons).Command);

        notifications.Click(shown, "photos.backup");
        await Wait.UntilAsync(() => host.Transport.EventsNamed("notification.activated").Any(), "notification.activated");
        var activated = host.Transport.EventsNamed("notification.activated").Single()["params"]!;
        Assert.Equal("photos.backup", (string?)activated["command"]);
        Assert.Equal(TestPhones.IphoneId, (string?)activated["deviceId"]);
        Assert.Equal("photos:x", (string?)activated["tag"]);

        var noTitle = await host.CallAsync("notifications.show", new { title = "" });
        Assert.Equal(-32602, (int?)noTitle["error"]!["code"]);
    }

    [Fact]
    public async Task Pc_list_mkdir_and_rename_work_on_real_folders()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.PcFolder("browse");
        await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "hello");
        var hidden = Path.Combine(folder, "secret.txt");
        await File.WriteAllTextAsync(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var made = await host.ResultAsync("pc.mkdir", new { path = Path.Combine(folder, "Phone photos") });
        Assert.True((bool)made!["isDirectory"]!);

        var list = (await host.ResultAsync("pc.list", new { path = folder }))!.AsArray();
        Assert.Equal(["Phone photos", "notes.txt"], list.Select(e => (string)e!["name"]!).Order(StringComparer.Ordinal).ToArray());
        var notes = list.Single(e => (string?)e!["name"] == "notes.txt")!;
        Assert.Equal(5, (long?)notes["size"]);
        Assert.Equal(Path.Combine(folder, "notes.txt"), (string?)notes["path"]);
        Assert.Equal(3, (await host.ResultAsync("pc.list", new { path = folder, showHidden = true }))!.AsArray().Count);

        var renamed = await host.ResultAsync("pc.rename", new { path = Path.Combine(folder, "notes.txt"), name = "Notes.md" });
        Assert.Equal("Notes.md", (string?)renamed!["name"]);
        Assert.True(File.Exists(Path.Combine(folder, "Notes.md")));

        var clash = await host.CallAsync("pc.mkdir", new { path = Path.Combine(folder, "Phone photos") });
        Assert.Equal("already_exists", (string?)clash["error"]!["data"]!["code"]);
        var missing = await host.CallAsync("pc.list", new { path = Path.Combine(folder, "nope") });
        Assert.Equal("path_not_found", (string?)missing["error"]!["data"]!["code"]);
        var relative = await host.CallAsync("pc.list", new { path = "relative\\path" });
        Assert.Equal(-32602, (int?)relative["error"]!["code"]);
        var badName = await host.CallAsync("pc.rename", new { path = Path.Combine(folder, "Notes.md"), name = "a/b" });
        Assert.Equal(-32602, (int?)badName["error"]!["code"]);
    }

    [Fact]
    public async Task Pc_roots_has_the_home_folder_and_a_drive()
    {
        await using var host = await TestHost.StartAsync();
        var roots = (await host.ResultAsync("pc.roots"))!.AsArray();
        Assert.Contains(roots, r => (string?)r!["kind"] == "home");
        Assert.Contains(roots, r => (string?)r!["kind"] == "drive" && (long?)r["totalBytes"] > 0);
    }

    [Fact]
    public async Task Desktop_only_methods_fail_plainly_without_the_window()
    {
        await using var host = await TestHost.StartAsync();
        var drag = await host.CallAsync("shell.dragOut", new { source = "photos", deviceId = TestPhones.IphoneId, items = new[] { new { id = $"{TestPhones.IphoneId}-a0", name = "IMG_0100.HEIC", size = 200_000 } } });
        Assert.Equal("not_supported", (string?)drag["error"]!["data"]!["code"]);
        var bad = await host.CallAsync("shell.dragOut", new { source = "photos" });
        Assert.Equal(-32602, (int?)bad["error"]!["code"]);
        var update = await host.CallAsync("app.updateStatus");
        Assert.Equal("not_supported", (string?)update["error"]!["data"]!["code"]);
    }

    [Fact]
    public void Desktop_settings_default_to_tray_notifications_and_updates_on()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(new Core.Settings.AppSettings(), Core.Serialization.DeviceDeckJson.Options)!.AsObject();
        Assert.True((bool)json["closeToTray"]!);
        Assert.True((bool)json["notifyOnConnect"]!);
        Assert.True((bool)json["autoUpdate"]!);
    }

    [Fact]
    public async Task State_detail_is_in_the_summary_json()
    {
        await using var host = await TestHost.StartAsync();
        var list = (await host.ResultAsync("device.list"))!.AsArray();
        Assert.All(list, d => Assert.True(d!.AsObject().ContainsKey("stateDetail")));
        Assert.Null(list[0]!["stateDetail"]);
    }

    [Theory]
    [InlineData("bytes=0-", 0, 999)]
    [InlineData("bytes=100-199", 100, 199)]
    [InlineData("bytes=900-5000", 900, 999)]
    [InlineData("bytes=-10", 990, 999)]
    [InlineData("bytes=5-9, 20-30", 5, 9)]
    public void Range_headers_parse(string header, long start, long end) =>
        Assert.Equal((start, end), VideoHandler.ParseRange(header, 1000));

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=50-10")]
    [InlineData("items=0-10")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=abc-")]
    public void Unservable_ranges_are_null(string header) => Assert.Null(VideoHandler.ParseRange(header, 1000));

    [Fact]
    public async Task Video_ranges_come_from_one_copy_of_the_original()
    {
        await using var host = await TestHost.StartAsync(TestPhones.Iphone(photos: 2, photoSize: 3_000_000));
        await using var videos = new VideoHandler(host.Hub, Path.Combine(host.Root, "videos"));
        var assetId = $"{TestPhones.IphoneId}-a1";
        var url = new Uri(VideoHandler.UrlFor(TestPhones.IphoneId, assetId, 3_000_000));

        var whole = new MemoryStream();
        var photo = await host.Fake.Find(TestPhones.IphoneId)!.Photos.ReadOriginalAsync(assetId, whole, null, CancellationToken.None).ContinueWith(_ => whole.ToArray());

        var first = await videos.HandleAsync(url, "bytes=0-", CancellationToken.None);
        Assert.Equal(206, first.StatusCode);
        Assert.Contains("Content-Range: bytes 0-2999999/3000000", first.Headers);
        Assert.Contains("Accept-Ranges: bytes", first.Headers);
        first.Body!.Dispose();

        var middle = await videos.HandleAsync(url, "bytes=100-199", CancellationToken.None);
        Assert.Equal(206, middle.StatusCode);
        Assert.Contains("Content-Length: 100", middle.Headers);
        var bytes = new byte[100];
        await using (var body = middle.Body!)
        {
            await body.ReadExactlyAsync(bytes);
            Assert.Equal(0, await body.ReadAsync(new byte[1]));
        }

        Assert.Equal(photo.AsSpan(100, 100).ToArray(), bytes);

        var tail = await videos.HandleAsync(url, "bytes=-1000", CancellationToken.None);
        Assert.Contains("Content-Range: bytes 2999000-2999999/3000000", tail.Headers);
        await using (var body = tail.Body!)
        {
            var end = new byte[1000];
            await body.ReadExactlyAsync(end);
            Assert.Equal(photo.AsSpan(2_999_000).ToArray(), end);
        }

        var plain = await videos.HandleAsync(url, null, CancellationToken.None);
        Assert.Equal(200, plain.StatusCode);
        Assert.Contains("Content-Length: 3000000", plain.Headers);
        plain.Body!.Dispose();

        var tooFar = await videos.HandleAsync(url, "bytes=3000000-", CancellationToken.None);
        Assert.Equal(416, tooFar.StatusCode);
        Assert.Contains("Content-Range: bytes */3000000", tooFar.Headers);

        Assert.Single(Directory.GetFiles(Path.Combine(host.Root, "videos")));
        Assert.Equal(404, (await videos.HandleAsync(new Uri("https://device.local/video/nope/x?size=1"), null, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task Old_videos_are_deleted_once_the_viewer_moves_on()
    {
        await using var host = await TestHost.StartAsync(TestPhones.Iphone(photos: 4, photoSize: 50_000));
        var folder = Path.Combine(host.Root, "videos");
        await using (var videos = new VideoHandler(host.Hub, folder) { KeepVideos = 2 })
        {
            for (var i = 0; i < 4; i++)
            {
                var response = await videos.HandleAsync(new Uri(VideoHandler.UrlFor(TestPhones.IphoneId, $"{TestPhones.IphoneId}-a{i}", 50_000)), "bytes=0-9", CancellationToken.None);
                Assert.Equal(206, response.StatusCode);
                response.Body!.Dispose();
            }

            await Wait.UntilAsync(() => Directory.GetFiles(folder).Length == 2, "the two oldest videos to be deleted");
        }

        Assert.Empty(Directory.GetFiles(folder));
    }
}
