namespace DeviceDeck.Bridge;

/// <summary>One web message from the page: the JSON text and the paths of any files sent with postMessageWithAdditionalObjects.</summary>
public sealed record BridgeMessage(string Json, IReadOnlyList<string> FilePaths)
{
    public static BridgeMessage Text(string json) => new(json, []);
}

/// <summary>
/// The page ↔ host channel. The Shell implements it over WebView2 (WebMessageReceived, PostWebMessageAsJson);
/// tests use an in-memory one. Post may be called from any thread.
/// </summary>
public interface IBridgeTransport
{
    event EventHandler<BridgeMessage>? MessageReceived;

    /// <summary>Sends one JSON text to the page (a response or an event).</summary>
    void Post(string json);
}

/// <summary>Things only the window can do: folder picker, Explorer.</summary>
public interface IShellServices
{
    /// <summary>The chosen folder, or null when the user cancels.</summary>
    Task<string?> PickFolderAsync(string? title, CancellationToken ct);

    void RevealInExplorer(string path);
}
