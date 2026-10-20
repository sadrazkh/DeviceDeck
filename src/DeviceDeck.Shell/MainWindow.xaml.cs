using System.Runtime.InteropServices;
using DeviceDeck.Bridge;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Hosting;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

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
            "micaalt" => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
            _ => new MicaBackdrop(),
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
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) =>
        {
            ApplyMinimumSize();
            PublishCaptionInsets();
        };

        ApplyTheme(host.Settings.Current.Theme);
        host.Settings.Changed += OnSettingsChanged;

        Activated += (_, _) =>
        {
            if (!_loggedBackdrop)
            {
                _loggedBackdrop = true;
                ShellLog.Info($"Backdrop {SystemBackdrop?.GetType().Name}; Mica supported: {Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()}.");
            }
        };

        _ = InitializeWebViewAsync();
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
        DispatcherQueue.TryEnqueue(() => ApplyTheme(settings.Theme));

    /// <summary>settings.theme drives the window (and so Mica's tint), the caption buttons and the page's prefers-color-scheme.</summary>
    private void ApplyTheme(AppTheme theme)
    {
        Root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
        if (WebView.CoreWebView2 is { } core)
        {
            core.Profile.PreferredColorScheme = theme switch
            {
                AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
                AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
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
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _host.Settings.Changed -= OnSettingsChanged;
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
            core.DOMContentLoaded += (_, _) => PublishCaptionInsets();
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += (_, e) => ShellLog.Error($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason}, exit {e.ExitCode}).");

            _transport = new WebViewTransport(core, DispatcherQueue, _content.IsTrustedSource);
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
            };
            _bridge = new BridgeHost(_transport, _host.BridgeServices(new ShellServices(DispatcherQueue, AppWindow.Id)));
            _bridge.Dispatcher.UnexpectedError += (_, e) => ShellLog.Error($"{e.Method} failed.", e.Error);

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
            var response = await _host.Images.HandleAsync(new Uri(e.Request.Uri), CancellationToken.None);
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

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);
}
