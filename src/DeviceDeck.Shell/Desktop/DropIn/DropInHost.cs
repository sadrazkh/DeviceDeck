using System.Runtime.InteropServices;
using System.Text;
using DeviceDeck.Shell.Desktop.Interop;
using DeviceDeck.Shell.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeviceDeck.Shell.Desktop.DropIn;

/// <summary>
/// Takes files dragged from File Explorer onto the window and gives them to the page (see <see cref="DropInForwarder"/>).
/// Two routes, whichever Windows uses for the drag: the OLE drop target registered on WinUI's content window (the
/// one Explorer's drag loop finds under the pointer), and XAML's drag events on the root (WinUI's own drag and drop,
/// through CoreDragDropManager). Both end in the same forwarder.
/// </summary>
internal sealed class DropInHost : IDisposable
{
    private const string ContentWindowClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

    private readonly nint _window;
    private readonly FrameworkElement _root;
    private readonly Func<double> _scale;
    private readonly Func<CoreWebView2?> _core;
    private readonly Func<CoreWebView2Environment?> _environment;
    private readonly Action<string> _post;
    private OleDropTarget? _target;
    private nint _registeredOn;

    public DropInHost(nint window, FrameworkElement root, Func<double> scale, Func<CoreWebView2?> core, Func<CoreWebView2Environment?> environment, Action<string> post)
    {
        _window = window;
        _root = root;
        _scale = scale;
        _core = core;
        _environment = environment;
        _post = post;
        Forwarder = new DropInForwarder(Post);

        _root.AllowDrop = true;
        _root.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(OnXamlDragEnter), true);
        _root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnXamlDragOver), true);
        _root.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(OnXamlDragLeave), true);
        _root.AddHandler(UIElement.DropEvent, new DragEventHandler(OnXamlDrop), true);
    }

    public DropInForwarder Forwarder { get; }

    /// <summary>Registers the OLE drop target on WinUI's content window. Call on the UI thread once the content is up.</summary>
    public void RegisterOleTarget()
    {
        if (_registeredOn != 0)
        {
            return;
        }

        var content = FindChild(_window, ContentWindowClass);
        var on = content != 0 ? content : _window;
        Ole.OleInitialize(0);
        _target = new OleDropTarget(Forwarder, _window, _scale);
        var hr = OleDropTarget.RegisterDragDrop(on, _target);
        if (hr < 0)
        {
            ShellLog.Warn($"Dropping files from File Explorer: RegisterDragDrop failed (0x{hr:X8}); XAML's drag events are the only route.");
            _target = null;
            return;
        }

        _registeredOn = on;
        ShellLog.Info($"Dropping files from File Explorer: OLE drop target on {(content != 0 ? ContentWindowClass : "the window")}, XAML drag events on the root.");
    }

    public void Dispose()
    {
        if (_registeredOn != 0)
        {
            OleDropTarget.RevokeDragDrop(_registeredOn);
            _registeredOn = 0;
        }
    }

    // ---- XAML route ----

    private async void OnXamlDragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var point = e.GetPosition(_root);
            var items = await ReadItemsAsync(e.DataView);
            Forwarder.Enter(point.X, point.Y, items);
            e.AcceptedOperation = Forwarder.Accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
        }
        catch (Exception ex)
        {
            ShellLog.Error("A drag from File Explorer could not be read.", ex);
            e.AcceptedOperation = DataPackageOperation.None;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnXamlDragOver(object sender, DragEventArgs e)
    {
        if (!Forwarder.Active)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        var point = e.GetPosition(_root);
        Forwarder.Over(point.X, point.Y);
        e.AcceptedOperation = Forwarder.Accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
    }

    private void OnXamlDragLeave(object sender, DragEventArgs e) => Forwarder.Leave();

    private async void OnXamlDrop(object sender, DragEventArgs e)
    {
        if (!Forwarder.Active)
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var point = e.GetPosition(_root);
            var items = await ReadItemsAsync(e.DataView);
            e.AcceptedOperation = Forwarder.Drop(point.X, point.Y, items) ? DataPackageOperation.Copy : DataPackageOperation.None;
        }
        catch (Exception ex)
        {
            ShellLog.Error("A drop from File Explorer could not be read.", ex);
            Forwarder.Leave();
        }
        finally
        {
            deferral.Complete();
        }
    }

    private static async Task<IReadOnlyList<DroppedItem>> ReadItemsAsync(DataPackageView view)
    {
        var items = await view.GetStorageItemsAsync();
        return items.Where(i => !string.IsNullOrEmpty(i.Path)).Select(i => new DroppedItem(i.Path, i.IsOfType(StorageItemTypes.Folder))).ToArray();
    }

    // ---- to the page ----

    /// <summary>Plain messages as JSON; a drop also carries each file's read-only handle, so the page gets real files.</summary>
    private void Post(string json, IReadOnlyList<DroppedItem>? attach)
    {
        if (attach is null || _core() is not { } core || _environment() is not { } environment)
        {
            _post(json);
            return;
        }

        try
        {
            var handles = attach.Where(i => !i.IsDirectory)
                .Select(i => (object)environment.CreateWebFileSystemFileHandle(i.Path, CoreWebView2FileSystemHandlePermission.ReadOnly))
                .ToList();
            core.PostWebMessageAsJson(json, handles);
        }
        catch (Exception ex) when (ex is ArgumentException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The page still gets the paths; only a tool that reads a file's bytes in the page would miss them.
            ShellLog.Warn($"Dropped files went to the page without handles: {ex.Message}");
            _post(json);
        }
    }

    private static nint FindChild(nint parent, string className)
    {
        for (var child = GetWindow(parent, GW_CHILD); child != 0; child = GetWindow(child, GW_HWNDNEXT))
        {
            var name = new StringBuilder(256);
            if (GetClassNameW(child, name, name.Capacity) > 0 && name.ToString() == className)
            {
                return child;
            }
        }

        return 0;
    }

    private const uint GW_HWNDNEXT = 2;
    private const uint GW_CHILD = 5;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
}
