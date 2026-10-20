using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The transfer queue's state machine, driven through the bridge with fake phones.</summary>
public sealed class TransferQueueTests
{
    private static readonly string[] Months = ["2026/10", "2026/09", "2026/08", "2026/07", "2026/06", "2026/05"];

    private static async Task<string> ExportAsync(TestHost host, string destination, params string[] assetIds)
    {
        var ids = assetIds.Length > 0 ? assetIds : Enumerable.Range(0, 4).Select(i => $"{TestPhones.IphoneId}-a{i}").ToArray();
        var result = await host.ResultAsync("photos.export", new
        {
            deviceId = TestPhones.IphoneId,
            assetIds = ids,
            destination,
            convertHeic = false,
            folderPattern = "yyyy/MM",
        });
        return (string)result!["jobId"]!;
    }

    private static string PhotoPath(string destination, int i) =>
        Path.Combine(destination, Months[i].Replace('/', Path.DirectorySeparatorChar), $"IMG_{100 + i:D4}.HEIC");

    private static void AssertNoPartials(string folder) =>
        Assert.Empty(Directory.EnumerateFiles(folder, "*.ddpart", SearchOption.AllDirectories));

    [Fact]
    public async Task Export_runs_to_done_into_year_month_folders()
    {
        await using var host = await TestHost.StartAsync();
        var destination = host.PcFolder("export");
        var jobId = await ExportAsync(host, destination);

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        var job = host.Queue.Get(jobId)!;
        Assert.Equal("Exporting 4 photos to " + destination, job.Title);
        Assert.Equal((4, 4, 0), (job.TotalItems, job.DoneItems, job.FailedItems));
        Assert.Equal(800_000, job.TotalBytes);
        Assert.Equal(800_000, job.DoneBytes);
        Assert.NotNull(job.FinishedAt);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(200_000, new FileInfo(PhotoPath(destination, i)).Length);
        }

        AssertNoPartials(destination);
        Assert.False(host.KeepAwake.IsHeld);
        Assert.True(host.KeepAwake.Begins >= 1);
        Assert.Equal(host.KeepAwake.Begins, host.KeepAwake.Ends);

        await Wait.UntilAsync(() => host.Transport.EventsNamed("transfer.updated").Any(e => (string?)e["params"]!["state"] == "done"), "the done event");
        var states = host.Transport.EventsNamed("transfer.updated").Select(e => (string)e["params"]!["state"]!).Distinct().ToArray();
        Assert.Equal(["queued", "running", "done"], states);
    }

    [Fact]
    public async Task Pause_stops_the_job_and_resume_finishes_it()
    {
        await using var host = await TestHost.StartAsync(1_000_000, null, TestPhones.Iphone(photos: 6));
        var destination = host.PcFolder("export");
        var jobId = await ExportAsync(host, destination, Enumerable.Range(0, 6).Select(i => $"{TestPhones.IphoneId}-a{i}").ToArray());

        await Wait.UntilAsync(() => host.Queue.Get(jobId)!.DoneItems >= 1, "the first photo");
        await host.ResultAsync("transfers.pause", new { jobId });
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Paused);

        var paused = host.Queue.Get(jobId)!;
        Assert.InRange(paused.DoneItems, 1, 5);
        Assert.Null(paused.BytesPerSecond);
        AssertNoPartials(destination);
        await Task.Delay(300);
        Assert.Equal(paused.DoneItems, host.Queue.Get(jobId)!.DoneItems);
        Assert.False(host.KeepAwake.IsHeld);

        await host.ResultAsync("transfers.resume", new { jobId });
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Equal(6, host.Queue.Get(jobId)!.DoneItems);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(200_000, new FileInfo(PhotoPath(destination, i)).Length);
        }

        AssertNoPartials(destination);
    }

    [Fact]
    public async Task Cancel_stops_for_good_and_removes_the_partial_file()
    {
        await using var host = await TestHost.StartAsync(1_000_000, null, TestPhones.Iphone(photos: 6));
        var destination = host.PcFolder("export");
        var jobId = await ExportAsync(host, destination, Enumerable.Range(0, 6).Select(i => $"{TestPhones.IphoneId}-a{i}").ToArray());

        await Wait.UntilAsync(() => host.Queue.Get(jobId)!.DoneItems >= 1, "the first photo");
        await host.ResultAsync("transfers.cancel", new { jobId });
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Cancelled);

        var job = host.Queue.Get(jobId)!;
        Assert.NotNull(job.FinishedAt);
        Assert.True(File.Exists(PhotoPath(destination, 0)));
        AssertNoPartials(destination);

        // Cancelled is final: resume does nothing.
        await host.ResultAsync("transfers.resume", new { jobId });
        await Task.Delay(200);
        Assert.Equal(TransferState.Cancelled, host.Queue.Get(jobId)!.State);
    }

    [Fact]
    public async Task A_pulled_cable_waits_for_the_same_phone_and_resumes()
    {
        await using var host = await TestHost.StartAsync(1_000_000, null, TestPhones.Iphone(photos: 6));
        var destination = host.PcFolder("export");
        var jobId = await ExportAsync(host, destination, Enumerable.Range(0, 6).Select(i => $"{TestPhones.IphoneId}-a{i}").ToArray());

        await Wait.UntilAsync(() => host.Queue.Get(jobId)!.DoneItems >= 1, "the first photo");
        host.Fake.Disconnect(TestPhones.IphoneId);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.WaitingForDevice);
        var waiting = host.Queue.Get(jobId)!;
        AssertNoPartials(destination);
        Assert.Null(waiting.Error);

        // Still waiting a while later: nothing retries against a phone that is gone.
        await Task.Delay(300);
        Assert.Equal(TransferState.WaitingForDevice, host.Queue.Get(jobId)!.State);

        host.Fake.Reconnect(TestPhones.IphoneId);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        var done = host.Queue.Get(jobId)!;
        Assert.Equal((6, 0), (done.DoneItems, done.FailedItems));
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(200_000, new FileInfo(PhotoPath(destination, i)).Length);
        }
    }

    [Fact]
    public async Task Queued_jobs_wait_too_and_a_locked_phone_still_counts_as_present()
    {
        var options = TestHost.FastQueue();
        await using var host = await TestHost.StartAsync(1_000_000, new TransferQueueOptions { WorkersPerDevice = 1, RetryDelay = options.RetryDelay }, TestPhones.Iphone(photos: 4));
        var first = await ExportAsync(host, host.PcFolder("one"));
        var second = await ExportAsync(host, host.PcFolder("two"));

        await Wait.ForStateAsync(host.Queue, first, TransferState.Running);
        Assert.Equal(TransferState.Queued, host.Queue.Get(second)!.State);

        host.Fake.Disconnect(TestPhones.IphoneId);
        await Wait.ForStateAsync(host.Queue, first, TransferState.WaitingForDevice);
        await Wait.ForStateAsync(host.Queue, second, TransferState.WaitingForDevice);

        host.Fake.Reconnect(TestPhones.IphoneId);
        host.Fake.SetState(TestPhones.IphoneId, DeviceState.Locked);
        await Wait.ForStateAsync(host.Queue, first, TransferState.Done);
        await Wait.ForStateAsync(host.Queue, second, TransferState.Done);
    }

    [Fact]
    public async Task A_conflict_asks_and_skip_keeps_the_existing_file()
    {
        await using var host = await TestHost.StartAsync();
        var destination = host.PcFolder("export");
        var existing = PhotoPath(destination, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "old");

        var jobId = await ExportAsync(host, destination);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.NeedsDecision);
        var asking = host.Queue.Get(jobId)!;
        Assert.Equal(new TransferConflict("IMG_0101.HEIC", 3, 200_000), asking.Conflict);
        Assert.Equal(1, asking.DoneItems);

        var wrong = await host.CallAsync("transfers.resolveConflict", new { jobId = "nope", choice = "skip", applyToAll = false });
        Assert.Equal(-32602, (int)wrong["error"]!["code"]!);

        await host.ResultAsync("transfers.resolveConflict", new { jobId, choice = "skip", applyToAll = false });
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Equal("old", await File.ReadAllTextAsync(existing));
        var done = host.Queue.Get(jobId)!;
        Assert.Equal((4, 0), (done.DoneItems, done.FailedItems));
        Assert.Null(done.Conflict);

        var again = await host.CallAsync("transfers.resolveConflict", new { jobId, choice = "skip", applyToAll = false });
        Assert.Equal("internal", (string?)again["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Keep_both_for_all_answers_every_later_conflict()
    {
        await using var host = await TestHost.StartAsync();
        var destination = host.PcFolder("export");
        foreach (var i in new[] { 0, 2 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PhotoPath(destination, i))!);
            await File.WriteAllTextAsync(PhotoPath(destination, i), "old");
        }

        var jobId = await ExportAsync(host, destination);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.NeedsDecision);
        var asked = host.Transport.Events.Count;

        await host.ResultAsync("transfers.resolveConflict", new { jobId, choice = "keepBoth", applyToAll = true });
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);

        foreach (var i in new[] { 0, 2 })
        {
            Assert.Equal("old", await File.ReadAllTextAsync(PhotoPath(destination, i)));
            var copy = Path.Combine(Path.GetDirectoryName(PhotoPath(destination, i))!, $"IMG_{100 + i:D4} (1).HEIC");
            Assert.Equal(200_000, new FileInfo(copy).Length);
        }

        Assert.DoesNotContain(host.Transport.Events.Skip(asked), e => (string?)e["params"]?["state"] == "needsDecision" && (string?)e["params"]?["id"] == jobId);
        Assert.Equal(ConflictRule.KeepBoth, host.Store.LoadAll().Single(j => j.Id == jobId).Rule);
    }

    [Fact]
    public async Task The_settings_rule_replace_overwrites_without_asking()
    {
        await using var host = await TestHost.StartAsync();
        await host.ResultAsync("settings.set", new { conflictRule = "replace" });
        var destination = host.PcFolder("export");
        Directory.CreateDirectory(Path.GetDirectoryName(PhotoPath(destination, 0))!);
        await File.WriteAllTextAsync(PhotoPath(destination, 0), "old");

        var jobId = await ExportAsync(host, destination);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Equal(200_000, new FileInfo(PhotoPath(destination, 0)).Length);
    }

    [Fact]
    public async Task Failed_items_are_counted_and_resume_retries_them()
    {
        await using var host = await TestHost.StartAsync();
        var destination = host.PcFolder("export");
        var mixed = await ExportAsync(host, destination, $"{TestPhones.IphoneId}-a0", "ghost");
        await Wait.ForStateAsync(host.Queue, mixed, TransferState.Done);
        Assert.Equal((2, 1, 1), (host.Queue.Get(mixed)!.TotalItems, host.Queue.Get(mixed)!.DoneItems, host.Queue.Get(mixed)!.FailedItems));

        var hopeless = await ExportAsync(host, destination, "ghost");
        await Wait.ForStateAsync(host.Queue, hopeless, TransferState.Failed);
        var failed = host.Queue.Get(hopeless)!;
        Assert.Equal("path_not_found", failed.Error!.Code);
        Assert.NotNull(failed.FinishedAt);

        await host.ResultAsync("transfers.resume", new { jobId = hopeless });
        await Wait.UntilAsync(() => host.Transport.EventsNamed("transfer.updated").Count(e => (string?)e["params"]!["id"] == hopeless && (string?)e["params"]!["state"] == "running") >= 2, "the retry");
        await Wait.ForStateAsync(host.Queue, hopeless, TransferState.Failed);
    }

    [Fact]
    public async Task Progress_events_are_throttled_and_carry_speed()
    {
        await using var host = await TestHost.StartAsync(20_000_000, null, TestPhones.Iphone(photos: 1, photoSize: 20_000_000));
        var destination = host.PcFolder("export");
        var started = DateTime.UtcNow;
        var jobId = await ExportAsync(host, destination, $"{TestPhones.IphoneId}-a0");
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        var seconds = (DateTime.UtcNow - started).TotalSeconds;
        await Task.Delay(250);

        var events = host.Transport.EventsNamed("transfer.updated").Where(e => (string?)e["params"]!["id"] == jobId).ToList();
        var running = events.Where(e => (string?)e["params"]!["state"] == "running").ToList();
        // ~80 progress reports arrive (256 KB chunks of 20 MB); at most 10 a second may go out,
        // plus the immediate ones for state changes (queued, running, planned, done) and one trailing update each.
        Assert.True(running.Count >= 4, $"only {running.Count} running events");
        Assert.True(running.Count <= seconds * 10 + 6, $"{running.Count} running events in {seconds:F2}s");
        Assert.True(events.Count <= seconds * 10 + 10, $"{events.Count} events in {seconds:F2}s");
        Assert.Contains(running, e => e["params"]!["bytesPerSecond"] is JsonValue v && v.GetValue<long>() > 1_000_000);
        Assert.Contains(running, e => e["params"]!["etaSeconds"] is not null);
        var doneBytes = running.Select(e => (long)e["params"]!["doneBytes"]!).ToList();
        Assert.Equal(doneBytes.Order(), doneBytes);
    }

    [Fact]
    public async Task Jobs_survive_a_restart_and_carry_on()
    {
        await using var host = await TestHost.StartAsync(1_000_000, null, TestPhones.Iphone(photos: 6));
        var destination = host.PcFolder("export");
        var jobId = await ExportAsync(host, destination, Enumerable.Range(0, 6).Select(i => $"{TestPhones.IphoneId}-a{i}").ToArray());
        await Wait.UntilAsync(() => host.Queue.Get(jobId)!.DoneItems >= 2, "two photos");

        await host.Queue.DisposeAsync(); // the app closes mid-copy
        var stored = host.Store.LoadAll().Single(j => j.Id == jobId);
        Assert.Equal(TransferState.Running, stored.State);
        Assert.True(stored.Planned);
        Assert.InRange(stored.Items.Count(i => i.State == TransferItemState.Done), 2, 5);

        using var reopened = new SqliteTransferStore(Path.Combine(host.Root, "app", "transfers.db"));
        await using var queue = new TransferQueue(host.Hub, reopened, TransferHandlers.All(), new CountingKeepAwake(), TestHost.FastQueue());
        queue.Start();
        Assert.Equal("Exporting 6 photos to " + destination, queue.Get(jobId)!.Title);
        await Wait.ForStateAsync(queue, jobId, TransferState.Done);
        Assert.Equal(6, queue.Get(jobId)!.DoneItems);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(200_000, new FileInfo(PhotoPath(destination, i)).Length);
        }

        AssertNoPartials(destination);
    }

    [Fact]
    public async Task Copy_to_the_phone_keeps_both_and_never_shows_a_partial_name()
    {
        await using var host = await TestHost.StartAsync();
        await host.ResultAsync("settings.set", new { conflictRule = "keepBoth" });
        var local = Path.Combine(host.PcFolder("films"), "Movie.mkv");
        await File.WriteAllBytesAsync(local, new byte[100_000]);

        var result = await host.ResultAsync("files.copyToDevice", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", files = new[] { local }, path = "/" });
        var jobId = (string)result!["jobId"]!;
        Assert.Equal("Copying Movie.mkv into VLC", host.Queue.Get(jobId)!.Title);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);

        var files = (await host.ResultAsync("files.list", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", path = "/" }))!.AsArray();
        var names = files.Select(f => (string)f!["name"]!).ToArray();
        Assert.Contains("Movie (1).mkv", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".ddpart", StringComparison.Ordinal));
        Assert.Equal(100_000, (long)files.Single(f => (string?)f!["name"] == "Movie (1).mkv")!["size"]!);
        Assert.Equal(300_000, (long)files.Single(f => (string?)f!["name"] == "Movie.mkv")!["size"]!);
    }

    [Fact]
    public async Task Dropped_files_and_folders_copy_to_the_phone()
    {
        await using var host = await TestHost.StartAsync();
        var folder = host.PcFolder("drop");
        Directory.CreateDirectory(Path.Combine(folder, "Season 1"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "Season 1", "E01.mkv"), new byte[1234]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Season 1", "E02.mkv"), new byte[4321]);

        // As the page sends it: params.files is [] and the paths come from postMessageWithAdditionalObjects.
        var reply = await host.CallAsync("files.copyToDevice",
            new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", files = Array.Empty<string>(), path = "/" },
            [Path.Combine(folder, "Season 1")]);
        var jobId = (string)reply["result"]!["jobId"]!;
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);

        var episodes = (await host.ResultAsync("files.list", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", path = "/Season 1" }))!.AsArray();
        Assert.Equal(["E01.mkv", "E02.mkv"], episodes.Select(f => (string)f!["name"]!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Copy_to_the_pc_walks_folders()
    {
        await using var host = await TestHost.StartAsync();
        var destination = host.PcFolder("copies");
        var result = await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", paths = new[] { "/Subtitles", "/Movie.mkv" }, destination });
        var jobId = (string)result!["jobId"]!;
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Equal(2_000, new FileInfo(Path.Combine(destination, "Subtitles", "Movie.srt")).Length);
        Assert.Equal(300_000, new FileInfo(Path.Combine(destination, "Movie.mkv")).Length);
        Assert.Equal("Copying 2 items to " + destination, host.Queue.Get(jobId)!.Title);
    }

    [Fact]
    public async Task Apps_install_and_export_go_through_the_queue()
    {
        await using var host = await TestHost.StartAsync();
        var apk = Path.Combine(host.PcFolder("apks"), "Termux.apk");
        await File.WriteAllBytesAsync(apk, new byte[50_000]);

        var install = (string)(await host.ResultAsync("apps.install", new { deviceId = TestPhones.AndroidId, files = new[] { apk } }))!["jobId"]!;
        await Wait.ForStateAsync(host.Queue, install, TransferState.Done);
        var apps = (await host.ResultAsync("apps.list", new { deviceId = TestPhones.AndroidId, includeSystem = true }))!.AsArray();
        Assert.Contains(apps, a => (string?)a!["id"] == "com.example.termux" && (string?)a["source"] == "sideloaded");

        var destination = host.PcFolder("saved");
        var export = (string)(await host.ResultAsync("apps.export", new { deviceId = TestPhones.AndroidId, appId = "com.android.chrome", destination }))!["jobId"]!;
        Assert.Equal("Saving Chrome to " + destination, host.Queue.Get(export)!.Title);
        await Wait.ForStateAsync(host.Queue, export, TransferState.Done);
        Assert.Single(Directory.GetFiles(destination, "*.apk"));

        var ipa = Path.Combine(host.PcFolder("ipas"), "Game-unsigned.ipa");
        await File.WriteAllBytesAsync(ipa, new byte[10]);
        var rejected = (string)(await host.ResultAsync("apps.install", new { deviceId = TestPhones.IphoneId, files = new[] { ipa } }))!["jobId"]!;
        await Wait.ForStateAsync(host.Queue, rejected, TransferState.Failed);
        Assert.Equal("install_failed", host.Queue.Get(rejected)!.Error!.Code);
    }

    [Fact]
    public async Task The_wrong_package_type_fails_the_call_at_once()
    {
        await using var host = await TestHost.StartAsync();
        var zip = Path.Combine(host.PcFolder("pkgs"), "thing.zip");
        var apk = Path.Combine(host.PcFolder("pkgs"), "thing.apk");
        await File.WriteAllBytesAsync(zip, [1]);
        await File.WriteAllBytesAsync(apk, [1]);

        var onAndroid = await host.CallAsync("apps.install", new { deviceId = TestPhones.AndroidId, files = new[] { zip } });
        Assert.Equal("install_failed", (string?)onAndroid["error"]!["data"]!["code"]);
        var onIphone = await host.CallAsync("apps.install", new { deviceId = TestPhones.IphoneId, files = new[] { apk } });
        Assert.Equal("install_failed", (string?)onIphone["error"]!["data"]!["code"]);
        Assert.Empty(host.Queue.List());
    }

    [Fact]
    public async Task Bad_requests_are_refused_before_a_job_exists()
    {
        await using var host = await TestHost.StartAsync();
        var relative = await host.CallAsync("photos.export", new { deviceId = TestPhones.IphoneId, assetIds = new[] { "x" }, destination = "photos", convertHeic = false, folderPattern = "" });
        Assert.Equal(-32602, (int)relative["error"]!["code"]!);

        var empty = await host.CallAsync("photos.export", new { deviceId = TestPhones.IphoneId, assetIds = Array.Empty<string>(), destination = host.PcFolder("x"), convertHeic = false, folderPattern = "" });
        Assert.Equal(-32602, (int)empty["error"]!["code"]!);

        host.Fake.Disconnect(TestPhones.IphoneId);
        var gone = await host.CallAsync("photos.export", new { deviceId = TestPhones.IphoneId, assetIds = new[] { "x" }, destination = host.PcFolder("x"), convertHeic = false, folderPattern = "" });
        Assert.Equal("device_not_found", (string?)gone["error"]!["data"]!["code"]);

        Assert.Empty(host.Queue.List());
    }
}
