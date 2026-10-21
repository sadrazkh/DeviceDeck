using System.Text.Json.Nodes;
using DeviceDeck.Bridge;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Shell.Desktop;
using static DeviceDeck.Shell.Tests.UpdateWait;

namespace DeviceDeck.Shell.Tests;

/// <summary>Updates you can see (round 4): a Setup copy (faked Velopack), the portable zip (faked GitHub), What's new, timers.</summary>
public sealed class UpdateServiceTests
{
    // ---------- A copy installed by the Setup ----------

    [Fact]
    public async Task Setup_copy_downloads_a_new_version_says_it_is_ready_once_and_restarts_into_it()
    {
        using var bench = new UpdateBench();
        var engine = new FakeUpdateEngine { Next = Offer("0.4.0", "# Notes from the package") };
        var service = bench.Start("0.3.0", engine);
        var seen = new List<UpdateStatus>();
        service.Changed += (_, s) =>
        {
            lock (seen)
            {
                seen.Add(s);
            }
        };
        Assert.Equal(UpdateState.Idle, service.Status.State);
        Assert.Equal(UpdateInstall.Setup, service.Status.Install);
        Assert.Equal("stable", service.Status.Channel);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Ready, status.State);
        Assert.Equal("0.4.0", status.NewVersion);
        Assert.Null(status.Percent);
        Assert.Equal(UpdateCheckResult.NewVersion, status.LastResult);
        Assert.Equal(bench.Time.GetUtcNow(), status.LastChecked);
        Assert.Equal(
            [UpdateState.Checking, UpdateState.Downloading, UpdateState.Downloading, UpdateState.Downloading, UpdateState.Downloading, UpdateState.Ready],
            seen.Select(s => s.State));
        Assert.Equal([0, 30, 60, 100], seen.Where(s => s.State == UpdateState.Downloading).Select(s => s.Percent!.Value));

        var shown = Assert.Single(bench.Notifications.Shown);
        Assert.Equal("DeviceDeck 0.4.0 is ready", shown.Title);
        Assert.Equal(UpdateService.NotificationTag, shown.Tag);
        Assert.Equal(NotificationCommands.ApplyUpdate, Assert.Single(shown.Buttons).Command);

        // Checking again changes nothing while it waits for the restart.
        Assert.Equal(UpdateState.Ready, (await service.CheckAsync(CancellationToken.None)).State);
        Assert.Equal(1, engine.Checks);

        bench.Notifications.Click(shown, NotificationCommands.ApplyUpdate);
        Assert.Equal("0.4.0", Assert.Single(engine.Applied).Version);
        Assert.Equal(1, bench.Quits);
    }

    [Fact]
    public async Task Setup_copy_up_to_date_and_failing_checks_are_remembered_across_runs()
    {
        using var bench = new UpdateBench();
        var engine = new FakeUpdateEngine();
        var service = bench.Start("0.4.0", engine);
        var upToDate = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.UpToDate, upToDate.State);
        Assert.Equal(UpdateCheckResult.UpToDate, upToDate.LastResult);
        Assert.Throws<InvalidOperationException>(service.ApplyAndRestart);

        bench.Time.Advance(TimeSpan.FromHours(1));
        engine.CheckFails = new HttpRequestException("GitHub is unreachable");
        var failed = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Error, failed.State);
        Assert.Contains("couldn't reach GitHub", failed.Error);
        Assert.Equal(UpdateCheckResult.Error, failed.LastResult);
        Assert.Empty(bench.Notifications.Shown);

        var next = bench.Start("0.4.0", new FakeUpdateEngine());
        Assert.Equal(UpdateState.Idle, next.Status.State);
        Assert.Equal(UpdateCheckResult.Error, next.Status.LastResult);
        Assert.Equal(bench.Time.GetUtcNow(), next.Status.LastChecked);
    }

    [Fact]
    public void Setup_copy_with_an_update_downloaded_in_an_earlier_run_starts_ready()
    {
        using var bench = new UpdateBench();
        var engine = new FakeUpdateEngine { Pending = Offer("0.4.0") };
        var service = bench.Start("0.3.0", engine);

        Assert.Equal(UpdateState.Ready, service.Status.State);
        Assert.Equal("0.4.0", service.Status.NewVersion);
        service.ApplyAndRestart();
        Assert.Equal("0.4.0", Assert.Single(engine.Applied).Version);
    }

    [Fact]
    public async Task While_it_downloads_the_status_says_how_far()
    {
        using var bench = new UpdateBench();
        var engine = new FakeUpdateEngine { Next = Offer("0.4.0"), Hold = new TaskCompletionSource() };
        var service = bench.Start("0.3.0", engine);

        var check = service.CheckAsync(CancellationToken.None);
        await UntilAsync(() => service.Status.State == UpdateState.Downloading, "the download");
        Assert.Equal(0, service.Status.Percent);
        Assert.Equal("0.4.0", service.Status.NewVersion);
        Assert.Empty(bench.Notifications.Shown);

        engine.Hold.SetResult();
        Assert.Equal(UpdateState.Ready, (await check).State);
    }

    // ---------- The portable zip ----------

    [Fact]
    public async Task Portable_copy_finds_a_newer_version_on_GitHub_says_so_once_and_Download_opens_only_its_release_page()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/latest", "0.4.0");
        var service = bench.Start("0.3.0");
        Assert.Equal(UpdateInstall.Portable, service.Status.Install);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, status.State);
        Assert.Equal("0.4.0", status.NewVersion);
        var shown = Assert.Single(bench.Notifications.Shown);
        Assert.Equal("DeviceDeck 0.4.0 is on GitHub", shown.Title);
        Assert.Equal(UpdateService.DownloadCommand, Assert.Single(shown.Buttons).Command);
        Assert.Throws<InvalidOperationException>(service.ApplyAndRestart);

        // The same version again (every 12 hours, or Check now): no second notification.
        Assert.Equal(UpdateState.Available, (await service.CheckAsync(CancellationToken.None)).State);
        Assert.Single(bench.Notifications.Shown);

        bench.Notifications.Click(shown, UpdateService.DownloadCommand);
        service.OpenReleasePage(null);
        Assert.Equal(["https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.4.0", "https://github.com/sadrazkh/DeviceDeck/releases/latest"], bench.Opened);
        Assert.Throws<ArgumentException>(() => service.OpenReleasePage("0.4.0\" & calc"));
        Assert.All(bench.Opened, url => Assert.True(IsReleasePage(url)));
    }

    [Fact]
    public async Task Portable_copy_knows_about_the_new_version_at_once_on_the_next_start()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/latest", "0.4.0");
        await bench.Start("0.3.0").CheckAsync(CancellationToken.None);

        var next = bench.Start("0.3.0");
        Assert.Equal(UpdateState.Available, next.Status.State);
        Assert.Equal("0.4.0", next.Status.NewVersion);

        // After updating by hand to that version it is simply up to date.
        var updated = bench.Start("0.4.0");
        Assert.Equal(UpdateState.Idle, updated.Status.State);
        Assert.Equal(UpdateState.UpToDate, (await updated.CheckAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Portable_copy_up_to_date_older_or_no_release_on_GitHub()
    {
        using var bench = new UpdateBench();
        var service = bench.Start("0.4.0");
        Assert.Equal(UpdateState.UpToDate, (await service.CheckAsync(CancellationToken.None)).State); // no release yet: 404

        bench.Github.Release("releases/latest", "0.3.0"); // a build newer than the latest release
        var status = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.UpToDate, status.State);
        Assert.Null(status.NewVersion);
        Assert.Empty(bench.Notifications.Shown);
    }

    [Fact]
    public async Task Portable_copy_rate_limited_says_until_when_on_Check_now_and_keeps_quiet_on_its_own()
    {
        using var bench = new UpdateBench();
        bench.Github.Override = _ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", bench.Time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        };
        var service = bench.Start("0.3.0");

        var manual = await service.CheckAsync(CancellationToken.None);
        Assert.Equal(UpdateState.Error, manual.State);
        Assert.Equal(bench.Time.GetUtcNow().AddMinutes(30).ToUnixTimeSeconds(), manual.RetryAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(UpdateCheckResult.Error, manual.LastResult);

        // The automatic check during the wait leaves the status as it is and asks GitHub nothing.
        var quiet = bench.Start("0.3.0", options: o => new UpdateServiceOptions { Time = o.Time, OpenUrl = o.OpenUrl, FirstCheck = TimeSpan.FromMinutes(1) });
        var before = quiet.Status;
        quiet.Start();
        bench.Time.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(() => bench.Github.Requests.Count >= 1, "the first request");
        await Task.Delay(100);
        Assert.Equal(before, quiet.Status);
        Assert.Single(bench.Github.Requests);
    }

    [Fact]
    public async Task Portable_copy_offline_keeps_a_version_it_already_found()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/latest", "0.4.0");
        var service = bench.Start("0.3.0");
        await service.CheckAsync(CancellationToken.None);

        bench.Github.Override = _ => throw new HttpRequestException("offline");
        var offline = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, offline.State);
        Assert.Equal("0.4.0", offline.NewVersion);
        Assert.Equal(UpdateCheckResult.Error, offline.LastResult);

        // Without that earlier find, offline is an error.
        File.Delete(bench.HistoryFile);
        Assert.Equal(UpdateState.Error, (await bench.Start("0.3.0").CheckAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task A_prerelease_build_follows_the_prerelease_channel()
    {
        using var bench = new UpdateBench();
        bench.Github.Route("releases?per_page=10", "[" + FakeGithub.ReleaseJson("0.4.0-beta.2", prerelease: true) + "," + FakeGithub.ReleaseJson("0.3.0") + "]");
        var service = bench.Start("0.4.0-beta.1");
        Assert.Equal("prerelease", service.Status.Channel);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateState.Available, status.State);
        Assert.Equal("0.4.0-beta.2", status.NewVersion);
    }

    // ---------- Automatic checks ----------

    [Fact]
    public async Task Automatic_checks_run_a_minute_after_start_then_every_12_hours_for_the_portable_copy()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/latest", "0.3.0");
        var service = bench.Start("0.3.0");
        service.Start();

        bench.Time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Assert.Empty(bench.Github.Requests);
        bench.Time.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => service.Status.State == UpdateState.UpToDate, "the first check");

        bench.Time.Advance(TimeSpan.FromHours(11));
        await Task.Delay(50);
        Assert.Single(bench.Github.Requests);
        bench.Time.Advance(TimeSpan.FromHours(1));
        await UntilAsync(() => bench.Github.Requests.Count == 2, "the 12-hour check");
    }

    [Fact]
    public async Task Automatic_checks_run_every_6_hours_for_a_Setup_copy_and_not_at_all_with_auto_update_off()
    {
        using var bench = new UpdateBench();
        var engine = new FakeUpdateEngine();
        var service = bench.Start("0.3.0", engine);
        service.Start();

        bench.Time.Advance(TimeSpan.FromMinutes(1));
        await UntilAsync(() => engine.Checks == 1, "the first check");
        bench.Time.Advance(TimeSpan.FromHours(6));
        await UntilAsync(() => engine.Checks == 2, "the 6-hour check");

        bench.Settings.Update(new JsonObject { ["autoUpdate"] = false });
        bench.Time.Advance(TimeSpan.FromHours(6));
        await Task.Delay(100);
        Assert.Equal(2, engine.Checks);
        Assert.Equal(UpdateState.UpToDate, (await service.CheckAsync(CancellationToken.None)).State); // Check now still works
    }

    [Fact]
    public async Task A_dev_build_never_checks_by_itself()
    {
        using var bench = new UpdateBench();
        var engine = new FakeUpdateEngine();
        var service = bench.Start("0.3.0", engine, o => new UpdateServiceOptions { Time = o.Time, AutomaticChecks = false });
        service.Start();

        bench.Time.Advance(TimeSpan.FromDays(2));
        await Task.Delay(100);
        Assert.Equal(0, engine.Checks);
        Assert.Empty(bench.Github.Requests);
    }

    // ---------- What's new ----------

    [Fact]
    public void Whats_new_is_offered_once_on_the_first_start_of_a_newer_version()
    {
        using var bench = new UpdateBench();
        Assert.Null(bench.Start("0.3.0").Status.WhatsNew); // a first install: nothing to tell
        var updated = bench.Start("0.4.0", options: RanBefore);
        Assert.Equal("0.4.0", updated.Status.WhatsNew);

        var changes = new List<UpdateStatus>();
        updated.Changed += (_, s) => changes.Add(s);
        updated.MarkWhatsNewShown("0.4.0");
        Assert.Null(updated.Status.WhatsNew);
        Assert.Null(Assert.Single(changes).WhatsNew);

        Assert.Null(bench.Start("0.4.0", options: RanBefore).Status.WhatsNew);
        Assert.Null(bench.Start("0.3.0", options: RanBefore).Status.WhatsNew); // going back is no news
    }

    [Fact]
    public void An_update_from_v030_which_kept_no_history_gets_whats_new_but_a_first_install_does_not()
    {
        using (var fromOld = new UpdateBench())
        {
            Assert.Equal("0.4.0", fromOld.Start("0.4.0", options: RanBefore).Status.WhatsNew);
        }

        using var fresh = new UpdateBench();
        Assert.Null(fresh.Start("0.4.0").Status.WhatsNew);
        Assert.Null(fresh.Start("0.4.0").Status.WhatsNew);
    }

    [Fact]
    public void Whats_new_can_be_forced_on_or_off()
    {
        using var bench = new UpdateBench();
        bench.Start("0.3.0");
        Assert.Null(bench.Start("0.4.0", options: o => new UpdateServiceOptions { Time = o.Time, WhatsNew = false }).Status.WhatsNew);
        Assert.Equal("0.4.0", bench.Start("0.4.0", options: o => new UpdateServiceOptions { Time = o.Time, WhatsNew = true }).Status.WhatsNew);
    }

    // ---------- Release notes ----------

    [Fact]
    public async Task Release_notes_come_from_GitHub_then_its_cache_then_what_came_with_the_app_then_a_link()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/tags/v0.4.0", "0.4.0", "# 🚀 DeviceDeck v0.4.0\n\n- **What's new**");
        var engine = new FakeUpdateEngine { Next = Offer("0.5.0", "# Notes in the 0.5.0 package") };
        var service = bench.Start("0.4.0", engine, o => new UpdateServiceOptions { Time = o.Time, BundledNotes = "# Bundled 0.4.0 notes" });

        var github = await service.GetReleaseNotesAsync("0.4.0", CancellationToken.None);
        Assert.Equal("github", github.Source);
        Assert.Equal("https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.4.0", github.Url);
        Assert.Equal("DeviceDeck v0.4.0", github.Title);
        Assert.StartsWith("# 🚀", github.Markdown);
        Assert.Equal("github", (await service.GetReleaseNotesAsync("0.4.0", CancellationToken.None)).Source); // a 304

        bench.Github.Override = _ => throw new HttpRequestException("offline");
        var cached = await service.GetReleaseNotesAsync("0.4.0", CancellationToken.None);
        Assert.Equal("cache", cached.Source);
        Assert.Equal(github.Markdown, cached.Markdown);

        File.Delete(bench.CacheFile);
        var bundled = await bench.Start("0.4.0", engine, o => new UpdateServiceOptions { Time = o.Time, BundledNotes = "# Bundled 0.4.0 notes" })
            .GetReleaseNotesAsync("0.4.0", CancellationToken.None);
        Assert.Equal(("app", "# Bundled 0.4.0 notes"), (bundled.Source, bundled.Markdown));

        await service.CheckAsync(CancellationToken.None); // downloads 0.5.0
        var package = await service.GetReleaseNotesAsync("0.5.0", CancellationToken.None);
        Assert.Equal(("app", "# Notes in the 0.5.0 package"), (package.Source, package.Markdown));

        var none = await service.GetReleaseNotesAsync("0.3.9", CancellationToken.None);
        Assert.Equal("none", none.Source);
        Assert.Null(none.Markdown);
        Assert.Equal("https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.3.9", none.Url);
    }

    private static UpdateServiceOptions RanBefore(UpdateServiceOptions o) =>
        new() { Time = o.Time, OpenUrl = o.OpenUrl, RanBefore = true };
}
