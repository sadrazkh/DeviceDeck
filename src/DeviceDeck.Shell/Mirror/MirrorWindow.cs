using DeviceDeck.Bridge.Toolbox;
using DeviceDeck.Shell.Hosting;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace DeviceDeck.Shell.Mirror;

/// <summary>
/// A phone's screen in a window of its own: web/mirror.html decodes the H.264 stream with WebCodecs and sends taps,
/// keys and scrolls back through a <see cref="MirrorBridge"/>. Closing the window stops mirroring on the phone.
/// </summary>
internal sealed class MirrorWindow : Window
{
    private readonly HostServices _host;
    private readonly WebContent _content;
    private readonly WebView2 _webView = new() { DefaultBackgroundColor = Colors.Black };
    private MirrorBridge? _bridge;

    public MirrorWindow(HostServices host, string deviceId)
    {
        _host = host;
        DeviceId = deviceId;
        _content = WebContent.Resolve(host.DevMode);
        Title = "DeviceDeck mirror";
        Content = new Grid { Background = new SolidColorBrush(Colors.Black), Children = { _webView } };
        SystemBackdrop = new MicaBackdrop();

        // A phone-shaped window on the right of the primary display: 85 % of its height, about 1:2 plus the toolbar.
        var work = DisplayArea.Primary.WorkArea;
        var height = (int)(work.Height * 0.85);
        var width = Math.Min((int)(height * 0.5), work.Width);
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - width - work.Width / 20, work.Y + (work.Height - height) / 2, width, height));
        Closed += (_, _) => Shutdown();
        _ = InitializeAsync();
    }

    public string DeviceId { get; }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        AppWindow.Show();
        Activate();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _host.Paths.Ensure(_host.Paths.WebViewData), new CoreWebView2EnvironmentOptions());
            await _webView.EnsureCoreWebView2Async(environment);
            var core = _webView.CoreWebView2;
            var settings = core.Settings;
            settings.AreDevToolsEnabled = _host.DevMode;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsWebMessageEnabled = true;
            core.Profile.PreferredColorScheme = _host.Settings.Current.Theme switch
            {
                Core.Settings.AppTheme.Light => CoreWebView2PreferredColorScheme.Light,
                Core.Settings.AppTheme.Dark => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
            core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "DeviceDeck mirror" : core.DocumentTitle;
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || !_content.IsTrustedSource(target))
                {
                    e.Cancel = true;
                    return;
                }

                _bridge?.ResetPage();
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.ProcessFailed += (_, e) => ShellLog.Error($"Mirror WebView2 process failed: {e.ProcessFailedKind} ({e.Reason}).");

            var transport = new WebViewTransport(core, DispatcherQueue, _content.IsTrustedSource);
            _bridge = new MirrorBridge(transport, _host.BridgeServices(new ShellServices(DispatcherQueue, AppWindow.Id)));
            _bridge.Dispatcher.UnexpectedError += (_, e) => ShellLog.Error($"Mirror {e.Method} failed.", e.Error);

            if (_content.Folder is { } folder)
            {
                core.SetVirtualHostNameToFolderMapping(WebContent.AppHost, folder, CoreWebView2HostResourceAccessKind.Allow);
            }

            var start = new Uri(_content.StartUri, "mirror.html?device=" + Uri.EscapeDataString(DeviceId));
            ShellLog.Info($"Mirror window for {DeviceId}: {start}");
            core.Navigate(start.ToString());
        }
        catch (Exception ex)
        {
            ShellLog.Error("The mirror window's WebView2 could not start.", ex);
        }
    }

    private void Shutdown()
    {
        _bridge?.Dispose();
        try
        {
            _webView.Close();
        }
        catch (Exception)
        {
        }
    }
}
