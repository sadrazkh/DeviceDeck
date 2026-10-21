using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeviceDeck.Shell.Desktop.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeviceDeck.Shell.Desktop.DropIn;

/// <summary>
/// The OLE drop target File Explorer's drag loop finds on the window: it reads the dragged paths (CF_HDROP) and
/// hands the drag to <see cref="DropInForwarder"/> in page pixels. Anything without files (a drag out of DeviceDeck
/// passing back over its own window carries virtual files only) is refused and never reaches the page.
/// Plain Win32: tests/DeviceDeck.Shell.Tests compiles this file on its own.
/// </summary>
[ComVisible(true)]
internal sealed class OleDropTarget(DropInForwarder forwarder, nint window, Func<double> scale) : IDropTarget
{
    public void DragEnter(nint dataObject, uint keyState, Point point, ref uint effect)
    {
        var items = ReadFiles(dataObject);
        if (items.Count == 0)
        {
            effect = Ole.DROPEFFECT_NONE;
            return;
        }

        var (x, y) = ToPage(point);
        forwarder.Enter(x, y, items);
        effect = Effect(effect);
    }

    public void DragOver(uint keyState, Point point, ref uint effect)
    {
        if (!forwarder.Active)
        {
            effect = Ole.DROPEFFECT_NONE;
            return;
        }

        var (x, y) = ToPage(point);
        forwarder.Over(x, y);
        effect = Effect(effect);
    }

    public void DragLeave() => forwarder.Leave();

    public void Drop(nint dataObject, uint keyState, Point point, ref uint effect)
    {
        if (!forwarder.Active)
        {
            effect = Ole.DROPEFFECT_NONE;
            return;
        }

        var (x, y) = ToPage(point);
        var taken = forwarder.Drop(x, y, ReadFiles(dataObject));
        effect = taken && (effect & Ole.DROPEFFECT_COPY) != 0 ? Ole.DROPEFFECT_COPY : Ole.DROPEFFECT_NONE;
    }

    /// <summary>Always a copy: nothing dragged in is moved or linked.</summary>
    private uint Effect(uint allowed) => forwarder.Accepts && (allowed & Ole.DROPEFFECT_COPY) != 0 ? Ole.DROPEFFECT_COPY : Ole.DROPEFFECT_NONE;

    /// <summary>Screen pixels → page pixels: the page fills the window's client area at the window's DPI scale.</summary>
    private (double X, double Y) ToPage(Point point)
    {
        if (window != 0)
        {
            ScreenToClient(window, ref point);
        }

        var s = Math.Max(scale(), 0.5);
        return (point.X / s, point.Y / s);
    }

    /// <summary>The paths of a CF_HDROP, or none when the data has no files.</summary>
    internal static IReadOnlyList<DroppedItem> ReadFiles(nint dataObject)
    {
        if (dataObject == 0)
        {
            return [];
        }

        var data = (ComIDataObject)Marshal.GetObjectForIUnknown(dataObject);
        try
        {
            var format = new FORMATETC
            {
                cfFormat = Ole.CF_HDROP,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = -1,
                tymed = TYMED.TYMED_HGLOBAL,
            };
            if (data.QueryGetData(ref format) != Ole.S_OK)
            {
                return [];
            }

            data.GetData(ref format, out var medium);
            try
            {
                var drop = medium.unionmember;
                var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
                var items = new List<DroppedItem>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var length = DragQueryFileW(drop, i, null, 0);
                    var buffer = new char[length + 1];
                    DragQueryFileW(drop, i, buffer, (uint)buffer.Length);
                    var path = new string(buffer, 0, (int)length);
                    items.Add(new DroppedItem(path, Directory.Exists(path)));
                }

                return items;
            }
            finally
            {
                Ole.ReleaseStgMedium(ref medium);
            }
        }
        catch (COMException)
        {
            return [];
        }
        finally
        {
            // A data object from another process (File Explorer) is a COM wrapper; one of ours comes back as itself.
            if (Marshal.IsComObject(data))
            {
                Marshal.ReleaseComObject(data);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint DragQueryFileW(nint drop, uint index, [Out] char[]? file, uint size);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref Point point);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int RegisterDragDrop(nint hwnd, IDropTarget target);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int RevokeDragDrop(nint hwnd);
}
