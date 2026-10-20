using System.Runtime.InteropServices;
using DeviceDeck.Shell.Desktop.Interop;
using DeviceDeck.Shell.Hosting;

namespace DeviceDeck.Shell.Desktop;

/// <summary>
/// DeviceDeck's icon in the notification area: click to show the window, right-click for Open / Quit. Lives on the
/// UI thread (its hidden window needs that thread's message loop) and comes back when Explorer restarts.
/// Also shows balloon notifications, which Windows turns into toasts, when app notifications aren't available.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const string ClassName = "DeviceDeck.TrayWindow";
    private const uint CallbackMessage = Tray.WM_APP + 1;
    private const uint IconId = 1;
    private const uint MenuOpen = 1;
    private const uint MenuQuit = 2;

    private static readonly Dictionary<nint, TrayIcon> Instances = [];
    private static readonly uint TaskbarCreated = Tray.RegisterWindowMessage("TaskbarCreated");
    private static bool _classRegistered;

    private readonly string _iconPath;
    private readonly nint _window;
    private nint _icon;
    private nint _balloonIcon;
    private bool _added;
    private string _tooltip;

    public TrayIcon(string tooltip, string iconPath, ShellText text)
    {
        _tooltip = tooltip;
        _iconPath = iconPath;
        Text = text;
        RegisterClass();
        // A hidden top-level window (not message-only): only those get the TaskbarCreated broadcast.
        _window = Tray.CreateWindowEx(0, ClassName, "DeviceDeck tray", 0, 0, 0, 0, 0, 0, 0, Tray.GetModuleHandle(null), 0);
        if (_window == 0)
        {
            throw new InvalidOperationException($"The tray window could not be created (error {Marshal.GetLastPInvokeError()}).");
        }

        Instances[_window] = this;
        Add();
    }

    /// <summary>Click or Enter on the icon, Open in its menu, or a click on a balloon (with <see cref="BalloonClicked"/> first).</summary>
    public event Action? OpenRequested;

    public event Action? QuitRequested;

    public event Action? BalloonClicked;

    public ShellText Text { get; set; }

    public bool IsShown => _added;

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        var data = NewData(Tray.NIF_TIP | Tray.NIF_SHOWTIP);
        NotifyIconData.Copy(_tooltip, data.Tip, 128);
        Tray.ShellNotifyIcon(Tray.NIM_MODIFY, ref data);
    }

    /// <summary>A balloon; on Windows 10 and 11 it appears as a toast from DeviceDeck.</summary>
    public void ShowBalloon(string title, string body)
    {
        if (_balloonIcon == 0)
        {
            // NIIF_LARGE_ICON wants an icon of SM_CXICON size; the tray's small one is refused.
            var size = Tray.GetSystemMetricsForDpi(Tray.SM_CXICON, Tray.GetDpiForSystem());
            _balloonIcon = Tray.LoadImage(0, _iconPath, Tray.IMAGE_ICON, size, size, Tray.LR_LOADFROMFILE);
        }

        var data = NewData(Tray.NIF_INFO);
        NotifyIconData.Copy(title, data.InfoTitle, 64);
        NotifyIconData.Copy(string.IsNullOrEmpty(body) ? " " : body, data.Info, 256);
        data.InfoFlags = Tray.NIIF_USER | Tray.NIIF_RESPECT_QUIET_TIME | (_balloonIcon != 0 ? Tray.NIIF_LARGE_ICON : 0);
        data.BalloonIcon = _balloonIcon != 0 ? _balloonIcon : _icon;
        if (!Tray.ShellNotifyIcon(Tray.NIM_MODIFY, ref data))
        {
            ShellLog.Warn("The tray balloon could not be shown.");
        }
    }

    private void Add()
    {
        var dpi = Tray.GetDpiForSystem();
        var size = Tray.GetSystemMetricsForDpi(Tray.SM_CXSMICON, dpi);
        if (_icon == 0)
        {
            _icon = Tray.LoadImage(0, _iconPath, Tray.IMAGE_ICON, size, size, Tray.LR_LOADFROMFILE);
        }

        var data = NewData(Tray.NIF_MESSAGE | Tray.NIF_ICON | Tray.NIF_TIP | Tray.NIF_SHOWTIP);
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        NotifyIconData.Copy(_tooltip, data.Tip, 128);
        _added = Tray.ShellNotifyIcon(Tray.NIM_ADD, ref data);
        if (!_added)
        {
            // Explorer isn't up yet (sign-in); TaskbarCreated adds it once it is.
            ShellLog.Warn("The tray icon could not be added yet.");
            return;
        }

        data.VersionOrTimeout = Tray.NOTIFYICON_VERSION_4;
        Tray.ShellNotifyIcon(Tray.NIM_SETVERSION, ref data);
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        Size = (uint)sizeof(NotifyIconData),
        Window = _window,
        Id = IconId,
        Flags = flags,
    };

    private nint OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message == TaskbarCreated)
        {
            // Explorer restarted (the icon is gone) or the taskbar's DPI changed (the icon may still be there, and
            // NIM_ADD would fail): remove it, then add it again with an icon of the size the taskbar wants now.
            var data = NewData(0);
            Tray.ShellNotifyIcon(Tray.NIM_DELETE, ref data);
            _added = false;
            if (_icon != 0)
            {
                Tray.DestroyIcon(_icon);
                _icon = 0;
            }

            Add();
            return 0;
        }

        if (message == CallbackMessage)
        {
            var kind = (uint)(lParam & 0xFFFF);
            switch (kind)
            {
                case Tray.NIN_SELECT:
                case Tray.NIN_KEYSELECT:
                case Tray.WM_LBUTTONDBLCLK:
                    OpenRequested?.Invoke();
                    break;
                case Tray.NIN_BALLOONUSERCLICK:
                    BalloonClicked?.Invoke();
                    OpenRequested?.Invoke();
                    break;
                case Tray.WM_CONTEXTMENU:
                    ShowMenu((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    break;
            }

            return 0;
        }

        return Tray.DefWindowProc(_window, message, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = Tray.CreatePopupMenu();
        try
        {
            Tray.AppendMenu(menu, Tray.MF_STRING, MenuOpen, Text.TrayOpen);
            Tray.AppendMenu(menu, Tray.MF_SEPARATOR, 0, null);
            Tray.AppendMenu(menu, Tray.MF_STRING, MenuQuit, Text.TrayQuit);
            Tray.SetMenuDefaultItem(menu, MenuOpen, false);

            // Without the foreground the menu doesn't close when you click elsewhere (documented quirk).
            Tray.SetForegroundWindow(_window);
            var flags = Tray.TPM_RIGHTBUTTON | Tray.TPM_RETURNCMD | Tray.TPM_NONOTIFY | (Text.RightToLeft ? Tray.TPM_LAYOUTRTL : 0);
            var chosen = Tray.TrackPopupMenuEx(menu, flags, x, y, _window, 0);
            Tray.PostMessage(_window, Tray.WM_NULL, 0, 0);
            switch (chosen)
            {
                case MenuOpen:
                    OpenRequested?.Invoke();
                    break;
                case MenuQuit:
                    QuitRequested?.Invoke();
                    break;
            }
        }
        finally
        {
            Tray.DestroyMenu(menu);
        }
    }

    private static void RegisterClass()
    {
        if (_classRegistered)
        {
            return;
        }

        fixed (char* name = ClassName)
        {
            var windowClass = new WindowClassEx
            {
                Size = (uint)sizeof(WindowClassEx),
                WindowProc = &WindowProc,
                Instance = Tray.GetModuleHandle(null),
                ClassName = name,
            };
            if (Tray.RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException($"The tray window class could not be registered (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        _classRegistered = true;
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            return Instances.TryGetValue(window, out var tray)
                ? tray.OnMessage(message, wParam, lParam)
                : Tray.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            ShellLog.Error("The tray icon failed to handle a message.", ex);
            return 0;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Tray.ShellNotifyIcon(Tray.NIM_DELETE, ref data);
            _added = false;
        }

        Instances.Remove(_window);
        Tray.DestroyWindow(_window);
        if (_icon != 0)
        {
            Tray.DestroyIcon(_icon);
            _icon = 0;
        }

        if (_balloonIcon != 0)
        {
            Tray.DestroyIcon(_balloonIcon);
            _balloonIcon = 0;
        }
    }
}
