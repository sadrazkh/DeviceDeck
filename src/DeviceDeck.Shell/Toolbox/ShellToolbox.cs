using DeviceDeck.Bridge.Toolbox;
using DeviceDeck.Shell.Hosting;
using DeviceDeck.Shell.Mirror;
using Microsoft.UI.Dispatching;

namespace DeviceDeck.Shell.Toolbox;

/// <summary>The Windows side of the toolbox: WIC, Media Foundation, WebView2 printing and mirror windows.</summary>
internal static class ShellToolbox
{
    private static readonly WicHeicConverter Heic = new();
    private static readonly MediaAudioTrimmer Trimmer = new();
    private static MirrorWindows? _mirrors;

    /// <summary>Call on the UI thread (the window's WebView set-up does): printing and windows need its dispatcher.</summary>
    public static ToolboxServices Create(HostServices host)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher is not null)
        {
            _mirrors ??= new MirrorWindows(host, dispatcher);
        }

        return new ToolboxServices
        {
            Heic = Heic,
            AudioTrimmer = Trimmer,
            Pdf = dispatcher is null ? null : new WebViewPdfPrinter(dispatcher, host.Paths.Ensure(host.Paths.WebViewData)),
            MirrorWindows = _mirrors,
        };
    }
}
