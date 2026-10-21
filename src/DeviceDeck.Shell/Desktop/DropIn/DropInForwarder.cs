using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeviceDeck.Shell.Desktop.DropIn;

/// <summary>A file or folder dragged in from File Explorer.</summary>
internal sealed record DroppedItem(string Path, bool IsDirectory)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;
}

/// <summary>
/// Files dragged from File Explorer onto the window, handed to the page. WinUI hosts WebView2 through composition, and
/// its WebView2 control neither takes drops (AllowDrop is false, no OLE drop target is registered) nor passes them to
/// the browser, so the page never saw a drop from Explorer. The host takes the drag itself (<see cref="OleDropTarget"/>
/// on the content window, and XAML's drag events as a second route) and tells the page where the pointer is; the page
/// plays it as HTML drag events at that point (web/src/components/nativeDrop.ts), so every drop zone works as in a
/// browser. The page answers each move with whether the element under the pointer takes files: that is the effect
/// File Explorer shows (copy or no-drop).
/// Messages (host → page, PostWebMessageAsJson): { "$dd": "nativeDrag", phase: enter|over|leave|drop, x, y, files? };
/// on drop each file's read-only FileSystemFileHandle goes along as an additional object. Page → host:
/// { "$dd": "nativeDragFeedback", accept }.
/// </summary>
internal sealed class DropInForwarder
{
    public const string MessageName = "nativeDrag";
    public const string FeedbackName = "nativeDragFeedback";

    private readonly Action<string, IReadOnlyList<DroppedItem>?> _post;
    private IReadOnlyList<DroppedItem>? _items;
    private volatile bool _accepts;

    /// <param name="post">Sends a message to the page; on a drop, with the items whose handles go along.</param>
    public DropInForwarder(Action<string, IReadOnlyList<DroppedItem>?> post) => _post = post;

    /// <summary>A drag of files from outside is over the window.</summary>
    public bool Active => _items is not null;

    /// <summary>The page's last answer: the element under the pointer takes files.</summary>
    public bool Accepts => _items is not null && _accepts;

    /// <summary>Page pixels (CSS px from the window's client origin).</summary>
    public void Enter(double x, double y, IReadOnlyList<DroppedItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        _items = items;
        // Until the page has answered: the window as a whole takes files (the page refuses where it can't).
        _accepts = true;
        _post(Message("enter", x, y, items), null);
    }

    public void Over(double x, double y)
    {
        if (_items is not null)
        {
            _post(Message("over", x, y, null), null);
        }
    }

    public void Leave()
    {
        if (_items is null)
        {
            return;
        }

        _items = null;
        _post(new JsonObject { ["$dd"] = MessageName, ["phase"] = "leave" }.ToJsonString(), null);
    }

    /// <summary>The drop; returns whether the page said it takes the files (the effect to report: copy or none).</summary>
    public bool Drop(double x, double y, IReadOnlyList<DroppedItem>? items = null)
    {
        var dropped = items is { Count: > 0 } ? items : _items;
        var accepted = _items is not null && _accepts;
        _items = null;
        if (dropped is null)
        {
            return false;
        }

        _post(Message("drop", x, y, dropped), dropped);
        return accepted;
    }

    /// <summary>{ "$dd": "nativeDragFeedback", "accept": true|false } from the page.</summary>
    public void OnFeedback(JsonElement body)
    {
        if (body.TryGetProperty("accept", out var accept) && accept.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            _accepts = accept.ValueKind == JsonValueKind.True;
        }
    }

    internal static string Message(string phase, double x, double y, IReadOnlyList<DroppedItem>? items)
    {
        var message = new JsonObject
        {
            ["$dd"] = MessageName,
            ["phase"] = phase,
            ["x"] = Math.Round(x, 1),
            ["y"] = Math.Round(y, 1),
        };
        if (items is not null)
        {
            var files = new JsonArray();
            var handle = 0;
            foreach (var item in items)
            {
                // Files get a handle on drop (additional object number `handle`); folders go by path only.
                files.Add(new JsonObject
                {
                    ["name"] = item.Name,
                    ["path"] = item.Path,
                    ["directory"] = item.IsDirectory,
                    ["handle"] = item.IsDirectory ? -1 : handle++,
                });
            }

            message["files"] = files;
        }

        return message.ToJsonString();
    }
}
