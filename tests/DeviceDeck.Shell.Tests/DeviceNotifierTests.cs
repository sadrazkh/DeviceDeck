using System.Text.Json.Nodes;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Desktop;

namespace DeviceDeck.Shell.Tests;

/// <summary>"Nima's iPhone connected · Open" and friends.</summary>
public sealed class DeviceNotifierTests
{
    private static SettingsStore Settings(DragHost host) => new(Path.Combine(host.Root, "settings.json"));

    [Fact]
    public async Task A_phone_that_connects_while_the_window_is_away_gets_one_notification()
    {
        await using var host = await DragHost.StartAsync();
        var notifications = new RecordingNotificationService();
        using var notifier = new DeviceNotifier(host.Hub, Settings(host), notifications, () => false);
        Assert.Empty(notifications.Shown); // already there at start: not news

        host.Fake.Disconnect(DragHost.PhoneId);
        host.Fake.Reconnect(DragHost.PhoneId);
        host.Fake.SetState(DragHost.PhoneId, DeviceState.Ready);

        var shown = Assert.Single(notifications.Shown);
        Assert.Equal("Drag Phone connected", shown.Title);
        Assert.Equal("Drag Phone · USB", shown.Body);
        Assert.Equal(DragHost.PhoneId, shown.DeviceId);
        Assert.Equal(DeviceNotifier.TagFor(DragHost.PhoneId), shown.Tag);
        Assert.Equal(NotificationCommands.Open, Assert.Single(shown.Buttons).Command);

        host.Fake.Disconnect(DragHost.PhoneId);
        Assert.Empty(notifications.Shown); // unplugged: the stale notification goes
    }

    [Fact]
    public async Task Nothing_while_the_window_is_in_front_or_when_turned_off()
    {
        await using var host = await DragHost.StartAsync();
        var notifications = new RecordingNotificationService();
        var inFront = true;
        var settings = Settings(host);
        using var notifier = new DeviceNotifier(host.Hub, settings, notifications, () => inFront);

        host.Fake.Disconnect(DragHost.PhoneId);
        host.Fake.Reconnect(DragHost.PhoneId);
        Assert.Empty(notifications.Shown);

        inFront = false;
        settings.Update(new JsonObject { ["notifyOnConnect"] = false });
        host.Fake.Disconnect(DragHost.PhoneId);
        host.Fake.Reconnect(DragHost.PhoneId);
        Assert.Empty(notifications.Shown);
    }

    [Fact]
    public async Task A_phone_waiting_for_Trust_says_so_in_Persian_too()
    {
        await using var host = await DragHost.StartAsync();
        var notifications = new RecordingNotificationService();
        var settings = Settings(host);
        settings.Update(new JsonObject { ["language"] = "fa" });
        using var notifier = new DeviceNotifier(host.Hub, settings, notifications, () => false);

        host.Fake.Disconnect(DragHost.PhoneId);
        host.Fake.Reconnect(DragHost.PhoneId);
        notifications.Remove(DeviceNotifier.TagFor(DragHost.PhoneId));
        host.Fake.SetState(DragHost.PhoneId, DeviceState.AwaitingTrust);

        var shown = Assert.Single(notifications.Shown);
        Assert.Equal("Drag Phone منتظر شماست", shown.Title);
        Assert.Empty(shown.Buttons);

        host.Fake.SetState(DragHost.PhoneId, DeviceState.Ready);
        Assert.Single(notifications.Shown); // same phone, same connection: no second "connected"
    }
}
