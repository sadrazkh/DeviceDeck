using System.Runtime.InteropServices;
using DeviceDeck.Bridge;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Desktop;
using DeviceDeck.Shell.Desktop.DragOut;
using DeviceDeck.Shell.Desktop.DropIn;
using DeviceDeck.Shell.Desktop.Interop;
using DeviceDeck.Shell.Hosting;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace DeviceDeck.Shell;

public sealed partial class MainWindow : Window
{
    private const int MinWidth = 1024;
    private const int MinHeight = 680;
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 800;

    private readonly HostServices _host;
    private readonly WebContent _content;
    private readonly DragRegions _dragRegions;
    private readonly nint _hwnd;
    private RectInt32 _restoredBounds;
    private BridgeHost? _bridge;
    private WebViewTransport? _transport;
    private bool _loggedBackdrop;
    private double _loggedScale;

    // Desktop (round 2): tray, notifications, updates, drag out.
    private readonly string _iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "DeviceDeck.ico");
    private readonly ShellNotifications _notifications;
    private readonly DeviceNotifier _deviceNotifier;
    private readonly UpdateService _updates;
    // Photo backup (round 3): "When this phone connects" (new photos, or a backup by itself).
    private readonly DeviceDeck.Core.Photos.PhotoBackupWatcher _photoBackupWatcher;
    private readonly DesktopShell _desktop;
    private TrayIcon? _tray;
    private bool _quitting;

    // Files dropped from File Explorer (round 3): the host takes the drag and plays it in the page.
    private DropInHost? _dropIn;
    private CoreWebView2Environment? _webEnvironment;

    // Windows' app theme, for settings.theme "system" (and its changes while DeviceDeck runs).
    private readonly UISettings _windowsColors = new();
    private bool? _appliedDark;

    internal MainWindow(HostServices host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _content = WebContent.Resolve(host.DevMode);
        _dragRegions = new DragRegions(AppWindow.Id);

        Title = "DeviceDeck";
        // DEVICEDECK_BACKDROP=micaalt|acrylic tries the other materials; Mica is the design.
        SystemBackdrop = Environment.GetEnvironmentVariable("DEVICEDECK_BACKDROP") switch
        {
            "acrylic" => new DesktopAcrylicBackdrop(),
            // Where Mica isn't supported, WinUI's own MicaBackdrop falls back to a solid colour.
            _ when !Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported() => new MicaBackdrop(),
            "micaalt" => new ThemedMicaBackdrop(Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt),
            _ => new ThemedMicaBackdrop(Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base),
        };
        ExtendsContentIntoTitleBar = true;
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        ApplyMinimumSize();
        RestorePlacement();
        AppWindow.Changed += OnAppWindowChanged;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Root.Loaded += (_, _) =>
        {
            LogScale();
            Root.XamlRoot.Changed += (_, _) =>
            {
                ApplyMinimumSize();
                PublishCaptionInsets();
                LogScale();
            };
        };

        ApplyTheme(host.Settings.Current.Theme);
        host.Settings.Changed += OnSettingsChanged;
        _windowsColors.ColorValuesChanged += OnWindowsColorsChanged;

        if (File.Exists(_iconPath))
        {
            AppWindow.SetIcon(_iconPath);
        }

        UpdateTray(host.Settings.Current);
        _notifications = new ShellNotifications("DeviceDeck", Path.Combine(AppContext.BaseDirectory, "Assets", "DeviceDeck.png"),
            () => DispatcherQueue.TryEnqueue(BringToFront), action => DispatcherQueue.TryEnqueue(() => action()), () => _tray);
        _notifications.Activated += OnNotificationActivated;
        _deviceNotifier = new DeviceNotifier(host.Hub, host.Settings, _notifications, () => Tray.IsInFront(_hwnd));
        _photoBackupWatcher = new DeviceDeck.Core.Photos.PhotoBackupWatcher(host.Hub, host.PhotoBackup, _notifications, host.Settings,
            new DeviceDeck.Core.Photos.PhotoBackupWatcherOptions { OpenFolder = folder => new ShellServices(DispatcherQueue, AppWindow.Id).RevealInExplorer(folder) });
        _photoBackupWatcher.CheckFailed += (_, ex) => ShellLog.Error("The photo backup's look at a connected phone failed.", ex);
        _updates = new UpdateService(HostServices.Version, host.Settings, _notifications, Quit);
        _updates.Start();
        var dragOut = new DragOutService(host.Hub, host.Transfers, host.DragOuts, host.DragOutStaging,
            action => DispatcherQueue.TryEnqueue(() => action()), _hwnd, () => Scale);
        _desktop = new DesktopShell(dragOut, _updates);

        Activated += (_, e) =>
        {
            if (SystemBackdrop is ThemedMicaBackdrop mica)
            {
                mica.InputActive = e.WindowActivationState != WindowActivationState.Deactivated;
            }

            if (!_loggedBackdrop)
            {
                _loggedBackdrop = true;
                ShellLog.Info($"Backdrop {SystemBackdrop?.GetType().Name}; Mica supported: {Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()}.");
            }
        };

        _ = InitializeWebViewAsync();
    }

    /// <summary>Called once the providers have started: phones that connect from now on get a notification.</summary>
    public void ArmNotifications()
    {
        _deviceNotifier.Arm();
        _photoBackupWatcher.Arm();
    }

    /// <summary>For a second launch: show this window instead.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
    }

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyTheme(settings.Theme);
            UpdateTray(settings);
        });

    /// <summary>Quit from the tray menu or an update: really closes the window, so the host stops and the process ends.</summary>
    public void Quit() => DispatcherQueue.TryEnqueue(() =>
    {
        _quitting = true;
        Close();
    });

    /// <summary>The tray icon is there while closing keeps DeviceDeck running (settings.closeToTray).</summary>
    private void UpdateTray(AppSettings settings)
    {
        var text = ShellText.For(settings.Language);
        if (settings.CloseToTray && _tray is null)
        {
            try
            {
                _tray = new TrayIcon("DeviceDeck", _iconPath, text);
                _tray.OpenRequested += BringToFront;
                _tray.QuitRequested += Quit;
                _tray.BalloonClicked += () => _notifications.OnBalloonClicked();
            }
            catch (Exception ex)
            {
                ShellLog.Error("The tray icon could not be created.", ex);
            }
        }
        else if (!settings.CloseToTray && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }

        if (_tray is not null)
        {
            _tray.Text = text;
        }
    }

    private void OnNotificationActivated(object? sender, NotificationActivation activation)
    {
        if (activation.Command == NotificationCommands.ApplyUpdate)
        {
            try
            {
                _updates.ApplyAndRestart();
            }
            catch (Exception ex)
            {
                ShellLog.Error("The update could not be applied.", ex);
            }
        }
    }

    /// <summary>The first time the window goes to the tray, say so once.</summary>
    private void ShowTrayHintOnce()
    {
        var marker = Path.Combine(_host.Paths.Root, "tray-hint-shown");
        if (File.Exists(marker))
        {
            return;
        }

        try
        {
            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var text = ShellText.For(_host.Settings.Current.Language);
        _notifications.Show(new AppNotification(text.StillRunning, text.StillRunningBody) { Tag = "app:tray" });
    }

    /// <summary>Logs the window's DPI scale and XAML's rasterization scale (WebView2 renders at the latter) when either changes.</summary>
    private void LogScale()
    {
        var xaml = Root.XamlRoot?.RasterizationScale ?? 0;
        if (xaml <= 0 || Math.Abs(xaml - _loggedScale) < 0.001)
        {
            return;
        }

        _loggedScale = xaml;
        var window = Scale;
        ShellLog.Info($"Scale: window {window:0.##} ({GetDpiForWindow(_hwnd)} dpi), XAML rasterization {xaml:0.##}.");
        if (Math.Abs(window - xaml) > 0.01)
        {
            ShellLog.Warn("The XAML rasterization scale differs from the window's DPI scale; text may be resampled.");
        }

        if (WebView.CoreWebView2 is { } core)
        {
            _ = LogPageScaleAsync(core, xaml);
        }
    }

    private static async Task LogPageScaleAsync(CoreWebView2 core, double xaml)
    {
        try
        {
            var ratio = double.Parse(await core.ExecuteScriptAsync("window.devicePixelRatio"), System.Globalization.CultureInfo.InvariantCulture);
            ShellLog.Info($"Scale: page devicePixelRatio {ratio:0.##}.");
            if (Math.Abs(ratio - xaml) > 0.01)
            {
                ShellLog.Warn("The page's devicePixelRatio differs from the XAML rasterization scale.");
            }
        }
        catch (Exception ex) when (ex is FormatException or COMException or InvalidOperationException)
        {
        }
    }

    private void OnWindowsColorsChanged(UISettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => ApplyTheme(_host.Settings.Current.Theme));

    /// <summary>Windows' "default app mode" is dark: its app background colour is dark.</summary>
    private bool WindowsAppsDark()
    {
        var background = _windowsColors.GetColorValue(UIColorType.Background);
        return background.R + background.G + background.B < 3 * 128;
    }

    /// <summary>
    /// settings.theme drives the window, Mica's tint, the caption buttons and the page's prefers-color-scheme, always as
    /// one explicit Light or Dark resolved here ("system" from Windows' app mode, followed live). The page's title bar,
    /// rail and see-through surfaces sit on Mica, so a page in one theme over Mica tinted for the other is unreadable
    /// (dark-theme near-white text on a light backdrop): nothing is left for WinUI or WebView2 to work out on their own.
    /// </summary>
    private void ApplyTheme(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark || (theme == AppTheme.System && WindowsAppsDark());
        if (SystemBackdrop is ThemedMicaBackdrop mica)
        {
            mica.Dark = dark;
        }

        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        AppWindow.TitleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        if (WebView.CoreWebView2 is { } core)
        {
            core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        }

        if (_appliedDark != dark)
        {
            _appliedDark = dark;
            ShellLog.Info($"Theme {theme}: {(dark ? "dark" : "light")} for the window, Mica and the page.");
        }
    }

    private void ApplyMinimumSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)Math.Round(MinWidth * Scale);
            presenter.PreferredMinimumHeight = (int)Math.Round(MinHeight * Scale);
        }
    }

    private void RestorePlacement()
    {
        var saved = WindowPlacement.Load(_host.Paths.WindowFile);
        var scale = Scale;
        var minW = (int)Math.Round(MinWidth * scale);
        var minH = (int)Math.Round(MinHeight * scale);
        if (saved is not null && saved.Width >= minW && saved.Height >= minH &&
            DisplayArea.GetFromRect(new RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.None) is { } display &&
            Overlaps(display.WorkArea, new RectInt32(saved.X, saved.Y, saved.Width, saved.Height)))
        {
            _restoredBounds = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);
            AppWindow.MoveAndResize(_restoredBounds);
            if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }

            return;
        }

        // First run (or the saved spot is off-screen now): 1280×800 centred on the primary display's work area.
        var work = DisplayArea.Primary.WorkArea;
        var width = Math.Min((int)Math.Round(DefaultWidth * scale), work.Width);
        var height = Math.Min((int)Math.Round(DefaultHeight * scale), work.Height);
        _restoredBounds = new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height);
        AppWindow.MoveAndResize(_restoredBounds);
    }

    private static bool Overlaps(RectInt32 area, RectInt32 window)
    {
        // At least a 120×48 strip of the window (its title bar) must be on that display.
        var left = Math.Max(area.X, window.X);
        var right = Math.Min(area.X + area.Width, window.X + window.Width);
        var top = Math.Max(area.Y, window.Y);
        var bottom = Math.Min(area.Y + area.Height, window.Y + 48);
        return right - left >= 120 && bottom - top >= 24;
    }

    /// <summary>
    /// Tells the page where the caption buttons are, in CSS px, as variables on &lt;html&gt;:
    /// --dd-caption-left, --dd-caption-right and --dd-titlebar-height. The buttons stay on the right in Persian too.
    /// </summary>
    private void PublishCaptionInsets()
    {
        if (WebView.CoreWebView2 is not { } core)
        {
            return;
        }

        var scale = Scale;
        var bar = AppWindow.TitleBar;
        var script = FormattableString.Invariant(
            $"(()=>{{const s=document.documentElement.style;s.setProperty('--dd-caption-left','{bar.LeftInset / scale:0.##}px');s.setProperty('--dd-caption-right','{bar.RightInset / scale:0.##}px');s.setProperty('--dd-titlebar-height','{bar.Height / scale:0.##}px');}})()");
        _ = core.ExecuteScriptAsync(script);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPresenterChange)
        {
            PublishCaptionInsets();
        }

        if ((args.DidPositionChange || args.DidSizeChange) &&
            sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _restoredBounds = new RectInt32(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height);
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var maximized = sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        new WindowPlacement(_restoredBounds.X, _restoredBounds.Y, _restoredBounds.Width, _restoredBounds.Height, maximized)
            .Save(_host.Paths.WindowFile);

        if (!_quitting && _host.Settings.Current.CloseToTray && _tray is { IsShown: true })
        {
            // Keep running in the notification area: phones keep being watched, copies keep going.
            args.Cancel = true;
            sender.Hide();
            ShowTrayHintOnce();
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _host.Settings.Changed -= OnSettingsChanged;
        _windowsColors.ColorValuesChanged -= OnWindowsColorsChanged;
        _deviceNotifier.Dispose();
        _photoBackupWatcher.Dispose();
        _updates.Dispose();
        _tray?.Dispose();
        _tray = null;
        _notifications.Dispose();
        _dropIn?.Dispose();
        _bridge?.Dispose();
        try
        {
            WebView.Close();
        }
        catch (Exception)
        {
        }

        // The host itself (queue, providers) is stopped by Program.Main once the message loop has ended.
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                _host.Paths.Ensure(_host.Paths.WebViewData),
                new CoreWebView2EnvironmentOptions());

            // Transparent from the very first frame: set on the controller options, before the controller exists.
            var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
            controllerOptions.DefaultBackgroundColor = Colors.Transparent;
            await WebView.EnsureCoreWebView2Async(environment, controllerOptions);

            var core = WebView.CoreWebView2;
            ConfigureSettings(core.Settings);
            ApplyTheme(_host.Settings.Current.Theme);
            if (Environment.GetEnvironmentVariable("DEVICEDECK_DRAG") != "native")
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(DragRegions.Script);
            }

            core.AddWebResourceRequestedFilter(DeviceImageHandler.Filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (sender, e) => _ = ServeImageAsync(environment, e);
            core.NavigationStarting += OnNavigationStarting;
            core.DOMContentLoaded += (_, _) =>
            {
                PublishCaptionInsets();
                _loggedScale = 0;
                LogScale();
            };
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += (_, e) => ShellLog.Error($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason}, exit {e.ExitCode}).");

            _transport = new WebViewTransport(core, DispatcherQueue, _content.IsTrustedSource);
            _webEnvironment = environment;
            _dropIn = new DropInHost(_hwnd, Root, () => Scale, () => WebView.CoreWebView2, () => _webEnvironment, json => _transport?.Post(json));
            _dropIn.RegisterOleTarget();
            _transport.InternalMessage += (_, message) =>
            {
                if (message.Name == DragRegions.MessageName)
                {
                    _dragRegions.Apply(message.Body);
                    if (_dragRegions.Updates <= 3 || Environment.GetEnvironmentVariable("DEVICEDECK_DRAG") == "trace")
                    {
                        ShellLog.Info($"Drag regions: caption [{string.Join(" ", _dragRegions.Caption.Select(r => $"{r.X},{r.Y},{r.Width}x{r.Height}"))}] passthrough [{string.Join(" ", _dragRegions.Passthrough.Select(r => $"{r.X},{r.Y},{r.Width}x{r.Height}"))}]");
                    }
                }
                else if (message.Name == DropInForwarder.FeedbackName)
                {
                    _dropIn?.Forwarder.OnFeedback(message.Body);
                }
                else if (message.Name == DropSelfTest.MessageName && _host.DevMode)
                {
                    _ = RunDropSelfTestAsync(message.Body);
                }
            };
            _bridge = new BridgeHost(_transport, _host.BridgeServices(new ShellServices(DispatcherQueue, AppWindow.Id), _notifications, _desktop));
            _bridge.Dispatcher.UnexpectedError += (_, e) => ShellLog.Error($"{e.Method} failed.", e.Error);
            _bridge.Dispatcher.SlowCall += (_, call) => ShellLog.Warn(call.Finished
                ? $"Slow call: {call.Method}{(call.DeviceId is null ? "" : $" on {call.DeviceId}")} took {call.Elapsed.TotalSeconds:0.0} s ({call.Outcome})."
                : $"Slow call: {call.Method}{(call.DeviceId is null ? "" : $" on {call.DeviceId}")} still running after {call.Elapsed.TotalSeconds:0.0} s.");

            if (_content.Folder is { } folder)
            {
                core.SetVirtualHostNameToFolderMapping(WebContent.AppHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            }

            ShellLog.Info($"Loading {_content.StartUri} ({_content.Description}).");
            core.Navigate(_content.StartUri.ToString());
        }
        catch (Exception ex)
        {
            // Most likely the WebView2 Runtime is missing (it ships with Windows 11).
            ShellLog.Error("WebView2 could not start.", ex);
        }
    }

    private void ConfigureSettings(CoreWebView2Settings settings)
    {
        var dev = _host.DevMode;
        settings.IsNonClientRegionSupportEnabled = true;
        settings.AreDevToolsEnabled = dev;
        settings.AreDefaultContextMenusEnabled = dev;
        settings.AreBrowserAcceleratorKeysEnabled = dev;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = true;
        settings.IsWebMessageEnabled = true;
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && _content.IsTrustedSource(target))
        {
            _bridge?.ResetPage();
            _dragRegions.Clear();
            return;
        }

        // The page is the app; links elsewhere open in the browser.
        e.Cancel = true;
        OpenExternally(e.Uri);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    private static void OpenExternally(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "https" or "http" or "mailto")
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    }

    private async Task ServeImageAsync(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (VideoHandler.Handles(uri))
            {
                var range = e.Request.Headers.Contains("Range") ? e.Request.Headers.GetHeader("Range") : null;
                var media = await _host.Videos.HandleAsync(uri, range, CancellationToken.None);
                e.Response = environment.CreateWebResourceResponse(media.Body?.AsRandomAccessStream(), media.StatusCode, media.ReasonPhrase, media.Headers);
                return;
            }

            var response = await _host.Images.HandleAsync(uri, CancellationToken.None);
            e.Response = environment.CreateWebResourceResponse(
                response.Body?.AsRandomAccessStream(),
                response.StatusCode,
                response.ReasonPhrase,
                response.Headers);
        }
        catch (Exception ex)
        {
            ShellLog.Error($"Image request failed: {e.Request.Uri}", ex);
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>DEVICEDECK_DEV only: an Explorer drop played on this window (see <see cref="DropSelfTest"/>); the report goes back to the page.</summary>
    private async Task RunDropSelfTestAsync(System.Text.Json.JsonElement body)
    {
        System.Text.Json.Nodes.JsonObject report;
        try
        {
            var paths = body.GetProperty("paths").EnumerateArray().Select(p => p.GetString()!).ToArray();
            var cancel = body.TryGetProperty("cancel", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True;
            report = await DropSelfTest.RunAsync(_hwnd, Scale, body.GetProperty("x").GetDouble(), body.GetProperty("y").GetDouble(), paths, cancel);
        }
        catch (Exception ex)
        {
            report = new System.Text.Json.Nodes.JsonObject { ["error"] = ex.Message };
        }

        ShellLog.Info($"Drop self-test: {report.ToJsonString()}");
        _transport?.Post(new System.Text.Json.Nodes.JsonObject { ["$dd"] = DropSelfTest.MessageName, ["report"] = report }.ToJsonString());
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
