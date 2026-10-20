using System.Runtime.InteropServices;
using System.Security;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Shell.Hosting;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using AppNotification = DeviceDeck.Core.Notifications.AppNotification;

namespace DeviceDeck.Shell.Desktop;

/// <summary>
/// <see cref="INotificationService"/> for the window: Windows toasts (with buttons) under DeviceDeck's own app id,
/// else a balloon from the tray icon. A click brings the window to the front, then raises <see cref="Activated"/>.
/// <para>
/// Windows App SDK app notifications can't register from a self-contained build (they need a resource of the
/// framework package), so this uses the system toast API: the app id (Velopack's, when installed; "DeviceDeck"
/// otherwise) is registered per user with DeviceDeck's name and icon. Clicks arrive while DeviceDeck runs, which in
/// the notification area it does; old toasts are cleared when it quits.
/// </para>
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
    private readonly string? _appId;
    private readonly ToastNotifier? _notifier;
    private AppNotification? _lastBalloon;

    public ShellNotifications(string displayName, string iconPath, Action bringToFront, Func<TrayIcon?> tray)
    {
        _bringToFront = bringToFront;
        _tray = tray;
        try
        {
            _appId = AppIdentity.Ensure(displayName, iconPath);
            var notifier = ToastNotificationManager.CreateToastNotifier(_appId);
            var setting = SettingOf(notifier);
            if (setting is null or NotificationSetting.Enabled)
            {
                _notifier = notifier;
                ShellLog.Info($"Notifications as {_appId} ({setting?.ToString() ?? "not shown any yet"}).");
            }
            else
            {
                ShellLog.Warn($"Windows has notifications for {_appId} turned off ({setting}); using the tray icon.");
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error("Windows notifications aren't available; using the tray icon.", ex);
        }
    }

    /// <summary>Null until the app id has shown its first toast: Windows only knows it from then on (0x80070490 before).</summary>
    private static NotificationSetting? SettingOf(ToastNotifier notifier)
    {
        try
        {
            return notifier.Setting;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            return null;
        }
    }

    public bool UsesToasts => _notifier is not null;

    public bool IsAvailable => _notifier is not null || _tray() is { IsShown: true };

    public event EventHandler<NotificationActivation>? Activated;

    public void Show(AppNotification notification)
    {
        try
        {
            if (_notifier is not null)
            {
                var toast = new ToastNotification(Build(notification)) { Group = Group };
                if (notification.Tag is { Length: > 0 } tag)
                {
                    toast.Tag = Truncate(tag);
                }

                toast.Activated += OnActivated;
                toast.Failed += (_, e) => ShellLog.Warn($"Notification \"{notification.Title}\" failed: 0x{e.ErrorCode.HResult:X8}.");
                _notifier.Show(toast);
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
        if (_notifier is null || _appId is null)
        {
            return;
        }

        try
        {
            ToastNotificationManager.History.Remove(Truncate(tag), Group, _appId);
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

    /// <summary>The toast as XML: title, body, up to three buttons; every click carries cmd/device/tag/a.* arguments.</summary>
    internal static XmlDocument Build(AppNotification n)
    {
        var doc = new XmlDocument();
        var toast = doc.CreateElement("toast");
        toast.SetAttribute("launch", Arguments(n, n.Command));
        doc.AppendChild(toast);

        var binding = doc.CreateElement("binding");
        binding.SetAttribute("template", "ToastGeneric");
        var visual = doc.CreateElement("visual");
        visual.AppendChild(binding);
        toast.AppendChild(visual);
        foreach (var line in new[] { n.Title, n.Body }.Where(l => !string.IsNullOrEmpty(l)))
        {
            var text = doc.CreateElement("text");
            text.InnerText = line!;
            binding.AppendChild(text);
        }

        if (n.Buttons.Count > 0)
        {
            var actions = doc.CreateElement("actions");
            foreach (var button in n.Buttons.Take(3))
            {
                var action = doc.CreateElement("action");
                action.SetAttribute("content", button.Label);
                action.SetAttribute("arguments", Arguments(n, button.Command));
                action.SetAttribute("activationType", "foreground");
                actions.AppendChild(action);
            }

            toast.AppendChild(actions);
        }

        return doc;
    }

    /// <summary>"cmd=open&amp;device=…" with every value escaped.</summary>
    internal static string Arguments(AppNotification n, string command)
    {
        var pairs = new List<(string Key, string Value)> { (CommandKey, command) };
        if (n.DeviceId is { } device)
        {
            pairs.Add((DeviceKey, device));
        }

        if (n.Tag is { } tag)
        {
            pairs.Add((TagKey, tag));
        }

        pairs.AddRange(n.Args.Select(a => (ArgPrefix + a.Key, a.Value)));
        return string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }

    internal static NotificationActivation Parse(string arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(cut < 0 ? pair : pair[..cut]);
            values[key] = cut < 0 ? "" : Uri.UnescapeDataString(pair[(cut + 1)..]);
        }

        return new NotificationActivation(
            values.GetValueOrDefault(CommandKey) ?? NotificationCommands.Open,
            values.GetValueOrDefault(DeviceKey),
            values.GetValueOrDefault(TagKey),
            values.Where(v => v.Key.StartsWith(ArgPrefix, StringComparison.Ordinal)).ToDictionary(v => v.Key[ArgPrefix.Length..], v => v.Value));
    }

    // Windows keeps tags to 64 characters.
    private static string Truncate(string tag) => tag.Length <= 64 ? tag : tag[..64];

    private void OnActivated(ToastNotification sender, object args)
    {
        try
        {
            Raise(Parse(args is ToastActivatedEventArgs activated ? activated.Arguments : ""));
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

    /// <summary>On quit: DeviceDeck's toasts go from Action Center (a click could no longer reach it).</summary>
    public void Dispose()
    {
        if (_notifier is null || _appId is null)
        {
            return;
        }

        try
        {
            ToastNotificationManager.History.Clear(_appId);
        }
        catch (Exception ex)
        {
            ShellLog.Warn($"Notifications could not be cleared: {ex.Message}");
        }
    }
}

/// <summary>The app id Windows shows DeviceDeck's notifications under, with its name and icon.</summary>
internal static partial class AppIdentity
{
    public const string PortableAppId = "DeviceDeck";
    private const string Key = @"Software\Classes\AppUserModelId\";

    /// <summary>
    /// The process's app id (Velopack sets one for an installed copy, matching its Start menu shortcut), else
    /// <see cref="PortableAppId"/>; registered under HKCU so Windows has a name and an icon for it.
    /// </summary>
    public static string Ensure(string displayName, string iconPath)
    {
        var appId = Current();
        if (appId is null)
        {
            appId = PortableAppId;
            Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));
        }

        using var key = Registry.CurrentUser.CreateSubKey(Key + appId);
        key.SetValue("DisplayName", displayName);
        key.SetValue("IconUri", iconPath);
        return appId;
    }

    /// <summary>Uninstalling: forget the registration and the toasts of every app id DeviceDeck may have used.</summary>
    public static void Forget()
    {
        foreach (var appId in new[] { Current(), PortableAppId }.OfType<string>().Distinct())
        {
            try
            {
                ToastNotificationManager.History.Clear(appId);
            }
            catch (Exception)
            {
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(Key + appId, throwOnMissingSubKey: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
            }
        }
    }

    private static string? Current()
    {
        if (GetCurrentProcessExplicitAppUserModelID(out var pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer) is { Length: > 0 } id ? id : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("shell32.dll")]
    private static partial int GetCurrentProcessExplicitAppUserModelID(out nint appId);
}
