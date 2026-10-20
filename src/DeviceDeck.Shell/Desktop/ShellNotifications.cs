using DeviceDeck.Core.Notifications;
using DeviceDeck.Shell.Hosting;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using AppNotification = DeviceDeck.Core.Notifications.AppNotification;

namespace DeviceDeck.Shell.Desktop;

/// <summary>
/// <see cref="INotificationService"/> for the window: Windows App SDK app notifications (toasts with buttons,
/// registered for this unpackaged app with its name and icon), else a balloon from the tray icon.
/// A click brings the window to the front, then raises <see cref="Activated"/>.
/// </summary>
internal sealed class ShellNotifications : INotificationService, IDisposable
{
    private const string CommandKey = "cmd";
    private const string DeviceKey = "device";
    private const string TagKey = "tag";
    private const string ArgPrefix = "a.";
    private const string Group = "devicedeck";

    private readonly Action _bringToFront;
    private readonly Func<TrayIcon?> _tray;
    private readonly bool _registered;
    private AppNotification? _lastBalloon;

    public ShellNotifications(string displayName, string iconPath, Action bringToFront, Func<TrayIcon?> tray)
    {
        _bringToFront = bringToFront;
        _tray = tray;
        try
        {
            if (AppNotificationManager.IsSupported())
            {
                var manager = AppNotificationManager.Default;
                manager.NotificationInvoked += OnInvoked;
                manager.Register(displayName, new Uri(iconPath));
                _registered = true;
            }
            else
            {
                ShellLog.Warn("App notifications aren't supported here; notifications use the tray icon.");
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error("App notifications could not be registered; notifications use the tray icon.", ex);
        }
    }

    public bool UsesAppNotifications => _registered;

    public bool IsAvailable => _registered || _tray() is { IsShown: true };

    public event EventHandler<NotificationActivation>? Activated;

    public void Show(AppNotification notification)
    {
        try
        {
            if (_registered)
            {
                AppNotificationManager.Default.Show(Build(notification));
                return;
            }

            if (_tray() is { IsShown: true } tray)
            {
                _lastBalloon = notification;
                tray.ShowBalloon(notification.Title, notification.Body ?? "");
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error($"Notification \"{notification.Title}\" could not be shown.", ex);
        }
    }

    public void Remove(string tag)
    {
        if (!_registered)
        {
            return;
        }

        _ = RemoveAsync(tag);
    }

    private static async Task RemoveAsync(string tag)
    {
        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(Truncate(tag), Group);
        }
        catch (Exception ex)
        {
            ShellLog.Warn($"Notification {tag} could not be removed: {ex.Message}");
        }
    }

    /// <summary>The tray balloon was clicked: the same as clicking the notification it showed.</summary>
    public void OnBalloonClicked()
    {
        if (_lastBalloon is { } shown)
        {
            Raise(new NotificationActivation(shown.Command, shown.DeviceId, shown.Tag, shown.Args));
        }
    }

    internal static Microsoft.Windows.AppNotifications.AppNotification Build(AppNotification n)
    {
        var builder = new AppNotificationBuilder();
        AddArguments(n, n.Command, (k, v) => builder.AddArgument(k, v));
        builder.AddText(n.Title);
        if (!string.IsNullOrEmpty(n.Body))
        {
            builder.AddText(n.Body);
        }

        foreach (var button in n.Buttons.Take(3))
        {
            var b = new AppNotificationButton(button.Label);
            AddArguments(n, button.Command, (k, v) => b.AddArgument(k, v));
            builder.AddButton(b);
        }

        builder.SetGroup(Group);
        if (n.Tag is { Length: > 0 } tag)
        {
            builder.SetTag(Truncate(tag));
        }

        return builder.BuildNotification();
    }

    private static void AddArguments(AppNotification n, string command, Action<string, string> add)
    {
        add(CommandKey, command);
        if (n.DeviceId is { } device)
        {
            add(DeviceKey, device);
        }

        if (n.Tag is { } tag)
        {
            add(TagKey, tag);
        }

        foreach (var (key, value) in n.Args)
        {
            add(ArgPrefix + key, value);
        }
    }

    // Windows keeps tags to 64 characters.
    private static string Truncate(string tag) => tag.Length <= 64 ? tag : tag[..64];

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            var arguments = args.Arguments;
            var extra = arguments.Where(a => a.Key.StartsWith(ArgPrefix, StringComparison.Ordinal))
                .ToDictionary(a => a.Key[ArgPrefix.Length..], a => a.Value);
            Raise(new NotificationActivation(
                arguments.TryGetValue(CommandKey, out var command) ? command : NotificationCommands.Open,
                arguments.TryGetValue(DeviceKey, out var device) ? device : null,
                arguments.TryGetValue(TagKey, out var tag) ? tag : null,
                extra));
        }
        catch (Exception ex)
        {
            ShellLog.Error("A notification click could not be handled.", ex);
        }
    }

    private void Raise(NotificationActivation activation)
    {
        _bringToFront();
        Task.Run(() => Activated?.Invoke(this, activation));
    }

    /// <summary>Call on exit, so Windows starts a fresh DeviceDeck for a click on an old notification.</summary>
    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            ShellLog.Warn($"App notifications could not be unregistered: {ex.Message}");
        }
    }
}
