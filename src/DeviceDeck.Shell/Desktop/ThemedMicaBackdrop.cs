using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace DeviceDeck.Shell.Desktop;

/// <summary>
/// Mica whose light or dark tint is set by DeviceDeck, not guessed by WinUI. The page draws its title bar, rail and
/// (before the opaque content layer) its surfaces as see-through layers on Mica; when Mica's tint and the page's
/// theme disagree the text is unreadable (dark-theme near-white text on a light backdrop). The window sets
/// <see cref="Dark"/> from the same resolved theme it gives WebView2, so the two always match.
/// </summary>
internal sealed class ThemedMicaBackdrop(MicaKind kind) : SystemBackdrop
{
    private readonly AccessibilitySettings _accessibility = new();
    private MicaController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private bool _dark;
    private bool _active = true;

    public bool Dark
    {
        get => _dark;
        set
        {
            _dark = value;
            Update();
        }
    }

    /// <summary>Mica dims to a flat colour while the window is in the background, as Windows' own windows do.</summary>
    public bool InputActive
    {
        get => _active;
        set
        {
            _active = value;
            Update();
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _configuration = new SystemBackdropConfiguration();
        Update();
        _controller = new MicaController { Kind = kind };
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    /// <summary>
    /// XAML calls this when the root's theme changes. The tint here comes from <see cref="Dark"/>, not from XAML's default
    /// configuration, so there is nothing to do; the base implementation can throw (E_INVALIDARG for "target", seen
    /// after a folder picker closed and the theme then changed) and take the window down with it.
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        _configuration = null;
    }

    private void Update()
    {
        if (_configuration is null)
        {
            return;
        }

        _configuration.Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _configuration.IsInputActive = _active;
        _configuration.IsHighContrast = _accessibility.HighContrast;
    }
}
