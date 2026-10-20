namespace DeviceDeck.Core.Notifications;

// Windows notifications ("Nima's iPhone connected · Open", "12 new photos · Back up").
// The Shell implements INotificationService with Windows App SDK app notifications (falling back to a
// tray balloon); everything else asks for one through it and listens for the clicks. See docs/bridge-contract.md,
// "Desktop: notifications".

/// <summary>A button on a notification. <paramref name="Command"/> comes back in <see cref="NotificationActivation"/> when it is clicked.</summary>
public sealed record NotificationButton(string Label, string Command);

/// <summary>One notification. Title is the first line, Body the optional second.</summary>
public sealed record AppNotification(string Title, string? Body = null)
{
    /// <summary>A later notification with the same tag replaces this one (for example one per phone and kind).</summary>
    public string? Tag { get; init; }

    /// <summary>The phone it is about; the page selects it when the notification is clicked.</summary>
    public string? DeviceId { get; init; }

    /// <summary>What a click on the notification itself means. <see cref="NotificationCommands.Open"/> just shows the window.</summary>
    public string Command { get; init; } = NotificationCommands.Open;

    /// <summary>Up to three buttons, in order.</summary>
    public IReadOnlyList<NotificationButton> Buttons { get; init; } = [];

    /// <summary>Extra values handed back on a click, for the feature that showed it.</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();
}

/// <summary>A click on a notification or one of its buttons. The window is already in front when this is raised.</summary>
public sealed record NotificationActivation(string Command, string? DeviceId, string? Tag, IReadOnlyDictionary<string, string> Args);

public static class NotificationCommands
{
    /// <summary>Show the window (and select <see cref="AppNotification.DeviceId"/> when there is one).</summary>
    public const string Open = "open";

    /// <summary>Restart into a downloaded update.</summary>
    public const string ApplyUpdate = "app.applyUpdate";
}

public interface INotificationService
{
    /// <summary>False when Windows can't show notifications for this app (then <see cref="Show"/> does nothing).</summary>
    bool IsAvailable { get; }

    /// <summary>Shows a notification. Never throws: a notification that can't be shown is logged and dropped.</summary>
    void Show(AppNotification notification);

    /// <summary>Removes a shown notification with this tag from Action Center, if it is still there.</summary>
    void Remove(string tag);

    /// <summary>Raised on a background thread after the window has been brought to the front.</summary>
    event EventHandler<NotificationActivation>? Activated;
}

/// <summary>For tests and hosts without Windows notifications.</summary>
public sealed class NullNotificationService : INotificationService
{
    public static readonly NullNotificationService Instance = new();

    public bool IsAvailable => false;

    public void Show(AppNotification notification)
    {
    }

    public void Remove(string tag)
    {
    }

    public event EventHandler<NotificationActivation>? Activated
    {
        add { }
        remove { }
    }
}

/// <summary>Records what was shown; lets tests click it.</summary>
public sealed class RecordingNotificationService : INotificationService
{
    private readonly Lock _gate = new();
    private readonly List<AppNotification> _shown = [];

    public bool IsAvailable => true;

    public IReadOnlyList<AppNotification> Shown
    {
        get
        {
            lock (_gate)
            {
                return _shown.ToArray();
            }
        }
    }

    public void Show(AppNotification notification)
    {
        lock (_gate)
        {
            if (notification.Tag is { } tag)
            {
                _shown.RemoveAll(n => n.Tag == tag);
            }

            _shown.Add(notification);
        }
    }

    public void Remove(string tag)
    {
        lock (_gate)
        {
            _shown.RemoveAll(n => n.Tag == tag);
        }
    }

    public event EventHandler<NotificationActivation>? Activated;

    /// <summary>Plays a click on the notification (command null) or on one of its buttons.</summary>
    public void Click(AppNotification notification, string? command = null) =>
        Activated?.Invoke(this, new NotificationActivation(command ?? notification.Command, notification.DeviceId, notification.Tag, notification.Args));
}
