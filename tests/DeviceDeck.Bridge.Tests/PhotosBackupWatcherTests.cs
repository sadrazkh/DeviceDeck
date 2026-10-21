using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>"When this phone connects": nothing, a notification of new photos with "Back up now", or a backup by itself.</summary>
public sealed class PhotosBackupWatcherTests
{
    private static readonly DateTimeOffset Recent = DateTimeOffset.Now.AddDays(-2);

    private static (PhotoBackupWatcher Watcher, RecordingNotificationService Notifications, List<string> Opened) Watch(PhotosBackupHost host)
    {
        var notifications = new RecordingNotificationService();
        var opened = new List<string>();
        var watcher = new PhotoBackupWatcher(host.Hub, host.Backup, notifications, host.Settings, new PhotoBackupWatcherOptions
        {
            Settle = TimeSpan.Zero,
            OpenFolder = opened.Add,
        });
        return (watcher, notifications, opened);
    }

    private static AppNotification Find(RecordingNotificationService notifications, string tag) => notifications.Shown.Single(n => n.Tag == tag);

    private static async Task<AppNotification> WaitForAsync(RecordingNotificationService notifications, string tag, Func<AppNotification, bool>? matching = null)
    {
        await Wait.UntilAsync(() => notifications.Shown.Any(n => n.Tag == tag && (matching?.Invoke(n) ?? true)), $"the notification {tag}");
        return Find(notifications, tag);
    }

    [Fact]
    public async Task By_default_a_phone_plugged_in_gets_a_notification_and_back_up_now_backs_it_up()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Recent);
        phone.Library.Add("IMG_0002.MOV", Recent, kind: Core.Abstractions.PhotoKind.Video);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var (watcher, notifications, opened) = Watch(host);
        using var _ = watcher;
        Assert.Equal(PhotoBackupOnConnect.Notify, (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).OnConnect);

        // Plugged in before DeviceDeck started: not news.
        watcher.Arm();
        await Task.Delay(200);
        Assert.Empty(notifications.Shown);

        host.Provider.Unplug(phone);
        host.Provider.PlugBack(phone);
        var news = await WaitForAsync(notifications, PhotoBackupWatcher.TagFor(phone.Summary.Id));
        Assert.Equal("2 new photos on Test iPhone", news.Title);
        Assert.StartsWith("1 photo · 1 video · about", news.Body);
        Assert.Equal(phone.Summary.Id, news.DeviceId);
        Assert.Equal([PhotoBackupCommands.BackUpNow], news.Buttons.Select(b => b.Command));
        Assert.Empty(host.Queue.List());

        notifications.Click(news, PhotoBackupCommands.BackUpNow);
        var done = await WaitForAsync(notifications, PhotoBackupWatcher.FinishedTagFor(phone.Summary.Id));
        Assert.Equal("2 photos backed up", done.Title);
        Assert.StartsWith("From Test iPhone · ", done.Body);
        Assert.Equal(host.Destination(phone), done.Args[PhotoBackupCommands.FolderArg]);
        Assert.DoesNotContain(notifications.Shown, n => n.Tag == PhotoBackupWatcher.TagFor(phone.Summary.Id)); // the offer went once taken
        Assert.Equal(PhotoBackupTrigger.Notification, host.Backup.Ledger.GetLastRun(phone.Summary.Id)!.Trigger);
        Assert.Equal(2, host.Files(phone).Length);

        // "Open folder" opens the backup folder, and only a backup folder.
        notifications.Click(done, PhotoBackupCommands.OpenFolder);
        notifications.Click(done with { Args = new Dictionary<string, string> { [PhotoBackupCommands.FolderArg] = host.Root } }, PhotoBackupCommands.OpenFolder);
        Assert.Equal([host.Destination(phone)], opened);
    }

    [Fact]
    public async Task Back_up_automatically_starts_by_itself_with_the_range_and_says_when_it_is_done()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Recent);
        phone.Library.Add("IMG_0002.HEIC", Recent.AddHours(1));
        phone.Library.Add("IMG_0000.HEIC", DateTimeOffset.Now.AddDays(-90));
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, range: PhotoBackupRange.LastDays(30), onConnect: PhotoBackupOnConnect.BackUp);
        var (watcher, notifications, _) = Watch(host);
        using var __ = watcher;

        // Plugged in when DeviceDeck started: the automatic backup still runs.
        watcher.Arm();
        var done = await WaitForAsync(notifications, PhotoBackupWatcher.FinishedTagFor(phone.Summary.Id));
        Assert.Equal("2 photos backed up", done.Title);
        Assert.Equal(["IMG_0001.HEIC", "IMG_0002.HEIC"], host.Files(phone).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var run = host.Backup.Ledger.GetLastRun(phone.Summary.Id)!;
        Assert.Equal((PhotoBackupTrigger.Automatic, PhotoBackupRunState.Done), (run.Trigger, run.State));
        Assert.Single(host.Queue.List()); // it shows in Transfers like any backup

        // Plugged in again with nothing new: no job, no notification.
        var shown = notifications.Shown.Count;
        host.Provider.Unplug(phone);
        host.Provider.PlugBack(phone);
        await Task.Delay(300);
        Assert.Single(host.Queue.List());
        Assert.Equal(shown, notifications.Shown.Count);

        // A new photo the next time: backed up by itself again.
        phone.Library.Add("IMG_0003.HEIC", DateTimeOffset.Now.AddHours(-1));
        host.Provider.Unplug(phone);
        host.Provider.PlugBack(phone);
        await WaitForAsync(notifications, PhotoBackupWatcher.FinishedTagFor(phone.Summary.Id), n => n.Title == "1 photo backed up");
        Assert.Equal(3, host.Files(phone).Length);
    }

    [Fact]
    public async Task An_automatic_backup_never_starts_into_a_missing_or_full_destination()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Recent);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, onConnect: PhotoBackupOnConnect.BackUp);
        host.DiskSpace = _ => new PcDiskSpace(300_000_000, 250_000_000_000);
        var (watcher, notifications, _) = Watch(host);
        using var __ = watcher;

        watcher.Arm();
        var refused = await WaitForAsync(notifications, PhotoBackupWatcher.TagFor(phone.Summary.Id));
        Assert.Equal("Test iPhone’s photos weren’t backed up", refused.Title);
        Assert.Contains("Not enough space on", refused.Body);
        Assert.Equal(PhotoBackupCommands.OpenPanel, refused.Buttons.Single().Command);
        Assert.Empty(host.Queue.List());

        host.DiskSpace = _ => new PcDiskSpace(300_000_000_000, 500_000_000_000);
        host.RootExists = _ => false;
        host.Provider.Unplug(phone);
        Assert.Empty(notifications.Shown); // unplugging takes the offer back
        host.Provider.PlugBack(phone);
        refused = await WaitForAsync(notifications, PhotoBackupWatcher.TagFor(phone.Summary.Id));
        Assert.Contains("isn't there", refused.Body);
        Assert.Empty(host.Queue.List());
    }

    [Fact]
    public async Task Do_nothing_does_nothing_and_a_manual_backup_ends_without_a_notification()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Recent);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var bridge = new BridgeCalls(host.Transport);
        var set = (await bridge.ResultAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, onConnect = "nothing" }))!;
        Assert.Equal("nothing", (string?)set["onConnect"]);
        var bad = await bridge.CallAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, onConnect = "sometimes" });
        Assert.Equal(-32602, (int)bad["error"]!["code"]!);
        var (watcher, notifications, _) = Watch(host);
        using var __ = watcher;

        watcher.Arm();
        host.Provider.Unplug(phone);
        host.Provider.PlugBack(phone);
        await Task.Delay(300);
        Assert.Empty(notifications.Shown);

        await host.BackUpAsync(phone);
        await Task.Delay(200);
        Assert.Empty(notifications.Shown);
        Assert.Equal(PhotoBackupTrigger.Manual, host.Backup.Ledger.GetLastRun(phone.Summary.Id)!.Trigger);

        // The choice is the phone's own: "Use the Settings defaults" leaves it alone, and it outlives a restart.
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, useDefaults: true);
        await host.RestartAsync();
        Assert.Equal(PhotoBackupOnConnect.Nothing, (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).OnConnect);
    }

    [Fact]
    public async Task Notifications_speak_persian_when_the_app_does()
    {
        var phone = new PhotosTestPhone();
        for (var i = 0; i < 12; i++)
        {
            phone.Library.Add($"IMG_{i:D4}.HEIC", Recent);
        }

        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.Settings.Update(new JsonObject { ["language"] = "fa" });
        var (watcher, notifications, _) = Watch(host);
        using var __ = watcher;
        watcher.Arm();

        host.Provider.Unplug(phone);
        host.Provider.PlugBack(phone);
        var news = await WaitForAsync(notifications, PhotoBackupWatcher.TagFor(phone.Summary.Id));
        Assert.Equal("۱۲ عکس تازه روی Test iPhone", news.Title);
        Assert.Equal("پشتیبان‌گیری", news.Buttons.Single().Label);
    }
}
