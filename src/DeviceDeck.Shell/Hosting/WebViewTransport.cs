using System.Text.Json;
using DeviceDeck.Bridge;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// The bridge over WebView2 web messages. Requests arrive as strings (JSON-RPC); host-internal messages
/// (drag regions from the injected script) arrive as objects with a "$dd" key and never reach the dispatcher.
/// </summary>
internal sealed class WebViewTransport : IBridgeTransport
{
    private readonly CoreWebView2 _core;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<Uri, bool> _isTrustedSource;

    public WebViewTransport(CoreWebView2 core, DispatcherQueue dispatcher, Func<Uri, bool> isTrustedSource)
    {
        _core = core;
        _dispatcher = dispatcher;
        _isTrustedSource = isTrustedSource;
        _core.WebMessageReceived += OnWebMessage;
    }

    public event EventHandler<BridgeMessage>? MessageReceived;

    /// <summary>A host-internal message: its "$dd" name and the whole JSON object.</summary>
    public event EventHandler<(string Name, JsonElement Body)>? InternalMessage;

    public void Post(string json)
    {
        if (_dispatcher.HasThreadAccess)
        {
            PostNow(json);
        }
        else
        {
            _dispatcher.TryEnqueue(() => PostNow(json));
        }
    }

    private void PostNow(string json)
    {
        try
        {
            _core.PostWebMessageAsJson(json);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException || ex.HResult == unchecked((int)0x8007139F))
        {
            // The page is navigating or the window is closing.
        }
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !_isTrustedSource(source))
        {
            ShellLog.Info($"Ignored a web message from {e.Source}.");
            return;
        }

        string? text = null;
        try
        {
            text = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            // Not a string: an object.
        }

        if (text is null)
        {
            var json = e.WebMessageAsJson;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("$dd", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    InternalMessage?.Invoke(this, (name.GetString()!, doc.RootElement.Clone()));
                    return;
                }
            }
            catch (JsonException)
            {
            }

            text = json;
        }

        var files = new List<string>();
        if (e.AdditionalObjects is { } objects)
        {
            foreach (var item in objects)
            {
                if (item is CoreWebView2File file && !string.IsNullOrEmpty(file.Path))
                {
                    files.Add(file.Path);
                }
            }
        }

        MessageReceived?.Invoke(this, new BridgeMessage(text, files));
    }
}
