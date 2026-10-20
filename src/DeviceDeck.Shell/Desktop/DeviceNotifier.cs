using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Settings;

namespace DeviceDeck.Shell.Desktop;

/// <summary>
/// "Nima's iPhone connected · Open" when a phone becomes ready while the window isn't in front (hidden in the tray,
/// minimised, or another app on top), and "… is waiting for you" while it shows Trust / Allow USB debugging.
/// Once per connection; settings.notifyOnConnect turns it off. Unplugging removes the notification.
/// Phones that show up while the providers first start (before <see cref="Arm"/>) were there already: not news.
/// </summary>
internal sealed class DeviceNotifier : IDisposable
{
    private readonly DeviceHub _hub;
    private readonly SettingsStore _settings;
    private readonly INotificationService _notifications;
    private readonly Func<bool> _windowInFront;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _ready = new(StringComparer.Ordinal);
    private readonly HashSet<string> _waiting = new(StringComparer.Ordinal);
    private bool _armed;

    public DeviceNotifier(DeviceHub hub, SettingsStore settings, INotificationService notifications, Func<bool> windowInFront)
    {
        _hub = hub;
        _settings = settings;
        _notifications = notifications;
        _windowInFront = windowInFront;
        lock (_gate)
        {
            // Phones already there when the window opened are not news.
            foreach (var device in hub.ListDevices())
            {
                _ready.Add(device.Id);
            }
        }

        hub.DeviceChanged += OnDeviceChanged;
    }

    public static string TagFor(string deviceId) => "device:" + deviceId;

    /// <summary>The providers' first start is over: from now on a phone that connects is news.</summary>
    public void Arm()
    {
        lock (_gate)
        {
            _armed = true;
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        var device = e.Summary;
        if (e.Change == DeviceChange.Removed || device.State == DeviceState.Disconnected)
        {
            bool shown;
            lock (_gate)
            {
                shown = _ready.Remove(device.Id) | _waiting.Remove(device.Id);
            }

            if (shown)
            {
                _notifications.Remove(TagFor(device.Id));
            }

            return;
        }

        var settings = _settings.Current;
        var text = ShellText.For(settings.Language);
        AppNotification? notification = null;
        lock (_gate)
        {
            if (!_armed)
            {
                // Found while starting (an iPhone may take a few seconds more to be ready): already there.
                _ready.Add(device.Id);
                return;
            }

            if (device.State == DeviceState.Ready && _ready.Add(device.Id))
            {
                _waiting.Remove(device.Id);
                notification = new AppNotification(string.Format(text.Connected, device.Name), text.DeviceLine(device))
                {
                    Buttons = [new NotificationButton(text.Open, NotificationCommands.Open)],
                };
            }
            else if (device.State is DeviceState.AwaitingTrust or DeviceState.Unauthorized && _waiting.Add(device.Id))
            {
                notification = new AppNotification(string.Format(text.Waiting, device.Name), device.StateDetail ?? text.DeviceLine(device));
            }
        }

        if (notification is null || !settings.NotifyOnConnect || _windowInFront())
        {
            return;
        }

        _notifications.Show(notification with { Tag = TagFor(device.Id), DeviceId = device.Id });
    }

    public void Dispose() => _hub.DeviceChanged -= OnDeviceChanged;
}
