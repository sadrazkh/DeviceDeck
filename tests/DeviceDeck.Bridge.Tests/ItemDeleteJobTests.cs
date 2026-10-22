using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Cleanup;
using DeviceDeck.Core.Deletes;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>
/// v0.6: every delete as a transfer job. files.deleteItems and apps.uninstallItems answer with the job at once; the job
/// deletes one item at a time with a result each (transfers.items), goes on past an item the phone refuses, stops between
/// items, and Clean-up's report drops what went whichever screen it was deleted from.
/// </summary>
public sealed class ItemDeleteJobTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Id = CleanupPhones.AndroidId;

    private static async Task<JsonArray> FinishedItemsAsync(TestHost host, string jobId)
    {
        await Wait.UntilAsync(() => host.Queue.Get(jobId) is { } j && DeleteJobs.IsFinished(j.State), $"delete job {jobId} (now {host.Queue.Get(jobId)?.State})");
        return (await host.ResultAsync("transfers.items", new { jobId }))!.AsArray();
    }

    private static async Task<CleanupReport> AnalyzeAsync(TestHost host)
    {
        host.Cleanup.Analyze(Id);
        await Wait.UntilAsync(() => host.Cleanup.GetStatus(Id).State is not CleanupState.Running, "the analysis");
        return host.Cleanup.GetStatus(Id).Report!;
    }

    [Fact]
    public async Task Files_and_folders_go_as_a_job_with_a_result_each()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        await AnalyzeAsync(host);
        var started = (await host.ResultAsync("files.deleteItems", new
        {
            deviceId = Id,
            items = new object[]
            {
                new { root = "internal", path = "/Download/setup.apk", size = 250_000 },
                new { root = "internal", path = "/Android/media/com.whatsapp", size = 402_000, isDirectory = true },
            },
            expectedBytes = 652_000,
        }))!.AsObject();
        Assert.Equal(["jobId"], started.Select(p => p.Key));
        var jobId = (string)started["jobId"]!;
        var job = (await host.ResultAsync("transfers.list"))!.AsArray().Single(j => (string?)j!["id"] == jobId)!;
        Assert.Equal(("filesDelete", 2, "Deleting 2 items from Clean Android"), ((string?)job["kind"], (int)job["totalItems"]!, (string?)job["title"]));

        var items = await FinishedItemsAsync(host, jobId);
        Assert.Equal(["index", "name", "source", "size", "state", "code", "message"], items[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["setup.apk", "com.whatsapp"], items.Select(i => (string?)i!["name"]));
        Assert.All(items, i => Assert.Equal("done", (string?)i!["state"]));
        var done = host.Queue.Get(jobId)!;
        Assert.Equal((TransferState.Done, 2, 652_000L, 0), (done.State, done.DoneItems, done.DoneBytes, done.SkippedItems));

        await using var fs = await host.Hub.GetDevice(Id).OpenFileSystemAsync("internal", None);
        Assert.Null(await fs.StatAsync("/Download/setup.apk", None));
        Assert.Null(await fs.StatAsync("/Android/media/com.whatsapp", None));
        Assert.NotNull(await fs.StatAsync("/Download/small.txt", None));

        // Deleted from Files, gone from Clean-up's list too, with what was inside the folder.
        Assert.Equal(["/Documents/report.pdf"], host.Cleanup.GetStatus(Id).Report!.AppFiles!.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task An_item_the_phone_refuses_fails_and_the_rest_still_go()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var started = (await host.ResultAsync("files.deleteItems", new
        {
            deviceId = Id,
            items = new object[]
            {
                new { root = "internal", path = "/Android/data", size = 0, isDirectory = true },
                new { root = "internal", path = "/Documents/report.pdf", size = 150_000 },
            },
            expectedBytes = 150_000,
        }))!;

        var jobId = (string)started["jobId"]!;
        var items = await FinishedItemsAsync(host, jobId);
        Assert.Equal(("failed", "permission_denied"), ((string?)items[0]!["state"], (string?)items[0]!["code"]));
        Assert.Equal("done", (string?)items[1]!["state"]);
        var job = host.Queue.Get(jobId)!;
        Assert.Equal((TransferState.Done, 1, 1), (job.State, job.DoneItems, job.FailedItems));
        Assert.Null(job.Error);
    }

    [Fact]
    public async Task A_file_that_changed_after_it_was_chosen_is_left_alone()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        // Jobs take turns, so a file can change between Delete and its turn: this one grew from 250,000 bytes.
        await using (var fs = await host.Hub.GetDevice(Id).OpenFileSystemAsync("internal", None))
        {
            await fs.WriteAsync("/Download/setup.apk", new MemoryStream(new byte[260_000]), 260_000, null, None);
        }

        var started = (await host.ResultAsync("files.deleteItems", new
        {
            deviceId = Id,
            items = new object[]
            {
                new { root = "internal", path = "/Download/setup.apk", size = 250_000 },
                new { root = "internal", path = "/Download/small.txt", size = 500 },
                new { root = "internal", path = "/Download/gone.txt", size = 10 },
            },
            expectedBytes = 250_510,
        }))!;
        var jobId = (string)started["jobId"]!;
        var items = await FinishedItemsAsync(host, jobId);
        Assert.Equal(("skipped", "changed"), ((string?)items[0]!["state"], (string?)items[0]!["code"]));
        Assert.Equal("done", (string?)items[1]!["state"]);
        Assert.Equal(("skipped", "missing"), ((string?)items[2]!["state"], (string?)items[2]!["code"]));
        var job = host.Queue.Get(jobId)!;
        Assert.Equal((TransferState.Done, 1, 0, 2, 500L), (job.State, job.DoneItems, job.FailedItems, job.SkippedItems, job.DoneBytes));
        await using var check = await host.Hub.GetDevice(Id).OpenFileSystemAsync("internal", None);
        Assert.Equal(260_000, (await check.StatAsync("/Download/setup.apk", None))!.Size);
    }

    [Fact]
    public async Task Clean_up_drops_an_app_already_gone_and_the_files_of_an_app_that_went()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Iphone());
        const string iphone = CleanupPhones.IphoneId;
        host.Cleanup.Analyze(iphone);
        await Wait.UntilAsync(() => host.Cleanup.GetStatus(iphone).State is not CleanupState.Running, "the analysis");
        Assert.Contains(host.Cleanup.GetStatus(iphone).Report!.AppFiles!.Files, f => f.Name == "Movie.mkv");

        // Removed in Apps: VLC's own files leave Clean-up's list with it.
        var vlc = (await host.ResultAsync("apps.uninstallItems", new { deviceId = iphone, items = new[] { new { id = "org.videolan.vlc-ios", size = 780_000, name = "VLC" } }, expectedBytes = 780_000 }))!;
        await FinishedItemsAsync(host, (string)vlc["jobId"]!);
        Assert.DoesNotContain(host.Cleanup.GetStatus(iphone).Report!.AppFiles!.Files, f => f.AppId == "org.videolan.vlc-ios");

        // Removed on the phone meanwhile: Clean-up's job finds it gone, leaves it alone, and the list drops it anyway.
        await host.Hub.GetDevice(iphone).Apps.UninstallAsync("com.readdle.Documents", None);
        var started = await host.Cleanup.UninstallAppsAsync(iphone, [new("com.readdle.Documents", 720_000)], 720_000, null, None);
        var result = await DeleteJobs.FinishedAsync(host, started);
        Assert.Equal((0, 1, "missing"), (result.Deleted, result.Skipped, result.Items[0].ErrorCode));
        var report = host.Cleanup.GetStatus(iphone).Report!;
        Assert.Empty(report.Apps!.Largest);
        Assert.Empty(report.AppFiles!.Files);
    }

    [Fact]
    public async Task Apps_go_as_a_job_and_one_the_phone_refuses_fails_while_the_rest_go()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        await AnalyzeAsync(host);
        var started = (await host.ResultAsync("apps.uninstallItems", new
        {
            deviceId = Id,
            items = new object[] { new { id = "com.android.chrome", size = 0, name = "Chrome" }, new { id = "com.example.game", size = 3_001_000, name = "Big Game" } },
            expectedBytes = 3_001_000,
        }))!;
        var jobId = (string)started["jobId"]!;
        var job = host.Queue.Get(jobId)!;
        Assert.Equal((TransferKind.AppsUninstall, "Removing 2 apps from Clean Android"), (job.Kind, job.Title));

        var items = await FinishedItemsAsync(host, jobId);
        Assert.Equal(["Chrome", "Big Game"], items.Select(i => (string?)i!["name"]));
        Assert.Equal(("failed", "permission_denied"), ((string?)items[0]!["state"], (string?)items[0]!["code"]));
        Assert.Equal("done", (string?)items[1]!["state"]);
        Assert.Equal(TransferState.Done, host.Queue.Get(jobId)!.State);

        var apps = await host.Hub.GetDevice(Id).Apps.ListAsync(true, None);
        Assert.DoesNotContain(apps, a => a.Id == "com.example.game");
        Assert.Contains(apps, a => a.Id == "com.android.chrome");
        // Removed from Apps, gone from Clean-up's list too.
        Assert.DoesNotContain(host.Cleanup.GetStatus(Id).Report!.Apps!.Largest, a => a.Id == "com.example.game");
    }

    [Fact]
    public async Task When_nothing_could_be_removed_the_job_fails_with_the_reason()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var started = (await host.ResultAsync("apps.uninstallItems", new
        {
            deviceId = Id,
            items = new object[] { new { id = "com.android.chrome", size = 0, name = "Chrome" } },
            expectedBytes = 0,
        }))!;
        var jobId = (string)started["jobId"]!;
        await FinishedItemsAsync(host, jobId);
        var job = host.Queue.Get(jobId)!;
        Assert.Equal(TransferState.Failed, job.State);
        Assert.Equal("permission_denied", job.Error?.Code);
        Assert.StartsWith("Nothing was deleted. Chrome:", job.Error?.Message);
    }

    [Fact]
    public async Task Stop_while_copying_first_removes_nothing_and_stops_the_copy()
    {
        // 10 kB/s: saving the 3 MB game to the PC takes minutes, so the job is still copying when Stop comes.
        await using var host = await TestHost.StartAsync(10_000, null, CleanupPhones.Android());
        var started = (await host.ResultAsync("apps.uninstallItems", new
        {
            deviceId = Id,
            items = new object[] { new { id = "com.example.game", size = 3_001_000, name = "Big Game" } },
            expectedBytes = 3_001_000,
            copyFirstTo = host.PcFolder("apks"),
        }))!;
        var jobId = (string)started["jobId"]!;
        await Wait.UntilAsync(() => host.Queue.Get(jobId)?.Stage?.Name == ItemDeleteHandler<AppsUninstallRequest, AppUninstallItem>.Copying
            && host.Queue.List().Any(j => j.Kind == TransferKind.AppExport && j.State == TransferState.Running), "the copy to the PC to run");

        await host.ResultAsync("transfers.cancel", new { jobId });
        var items = await FinishedItemsAsync(host, jobId);
        Assert.Equal(TransferState.Cancelled, host.Queue.Get(jobId)!.State);
        Assert.Equal(("queued", "Not tried: the delete was stopped."), ((string?)items[0]!["state"], (string?)items[0]!["message"]));
        Assert.Contains(await host.Hub.GetDevice(Id).Apps.ListAsync(false, None), a => a.Id == "com.example.game");
        var copy = host.Queue.List().Single(j => j.Kind == TransferKind.AppExport);
        await Wait.UntilAsync(() => host.Queue.Get(copy.Id)!.State == TransferState.Cancelled, "the copy to stop");
    }

    [Fact]
    public async Task A_whole_shared_folder_a_wrong_total_or_a_repeat_is_refused_before_any_job()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var root = await host.CallAsync("files.deleteItems", new { deviceId = Id, items = new[] { new { root = "internal", path = "/", size = 0 } }, expectedBytes = 0 });
        Assert.Equal(-32602, (int)root["error"]!["code"]!);

        var total = await host.CallAsync("files.deleteItems", new { deviceId = Id, items = new[] { new { root = "internal", path = "/Download/small.txt", size = 500 } }, expectedBytes = 501 });
        Assert.Equal(-32602, (int)total["error"]!["code"]!);

        var twice = await host.CallAsync("apps.uninstallItems", new
        {
            deviceId = Id,
            items = new[] { new { id = "com.whatsapp", size = 1 }, new { id = "com.whatsapp", size = 1 } },
            expectedBytes = 2,
        });
        Assert.Equal(-32602, (int)twice["error"]!["code"]!);

        Assert.Empty(host.Queue.List());
    }

    [Fact]
    public void A_delete_DeviceDeck_closed_in_the_middle_of_ends_stopped_and_never_runs_again()
    {
        var job = new StoredJob
        {
            Id = "j",
            Seq = 1,
            DeviceId = Id,
            Kind = TransferKind.FilesDelete,
            Title = "Deleting 3 items from Clean Android",
            State = TransferState.Running,
            RequestJson = "{}",
            Rule = ConflictRule.KeepBoth,
            CreatedAt = DateTimeOffset.Now,
            Items =
            [
                new TransferItem { Index = 0, Source = "a", Destination = "", Name = "a", Size = 1, State = TransferItemState.Done },
                new TransferItem { Index = 1, Source = "b", Destination = "", Name = "b", Size = 1, State = TransferItemState.Running },
                new TransferItem { Index = 2, Source = "c", Destination = "", Name = "c", Size = 1, State = TransferItemState.Queued, ErrorMessage = FilesDeleteHandler.StoppedNote },
            ],
        };

        new FilesDeleteHandler().Restarted(job);

        Assert.Equal(TransferState.Cancelled, job.State);
        Assert.Equal("interrupted", job.Error?.Code);
        Assert.Equal((TransferItemState.Failed, "interrupted"), (job.Items[1].State, job.Items[1].ErrorCode));
        // Settled as "stopped" as DeviceDeck closed: it says why.
        Assert.Equal((TransferItemState.Queued, "Not tried: DeviceDeck closed before the delete got to it."), (job.Items[2].State, job.Items[2].ErrorMessage));
        Assert.Contains("never deleted by itself", job.Error!.Message);
    }
}
