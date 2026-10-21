using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using DeviceDeck.Shell.Desktop.Interop;

namespace DeviceDeck.Shell.Desktop.DropIn;

/// <summary>
/// Dev only (DEVICEDECK_DEV=1): plays a File Explorer drop on DeviceDeck's window without the mouse. It finds the OLE
/// drop target the window registered (the one Explorer's drag loop calls), builds the data object Explorer itself
/// hands over for those files (their folder's IShellFolder.GetUIObjectOf), and calls DragEnter, DragOver and Drop at a
/// point of the page, one message-loop turn apart, as DoDragDrop in Explorer's process would. The page sees whatever a
/// real drop gives it; the report says which window took it and what each call answered.
/// Started by the page (DevTools) with the host message { "$dd": "dropSelfTest", x, y, paths, keepOver? }.
/// </summary>
internal static class DropSelfTest
{
    public const string MessageName = "dropSelfTest";

    private const string DropTargetProperty = "OleDropTargetInterface";

    /// <param name="window">DeviceDeck's top-level window.</param>
    /// <param name="x">Page (CSS) pixels from the window's client origin; the page fills the whole client area.</param>
    /// <param name="paths">Files to drop; all in one folder, like one Explorer selection.</param>
    /// <param name="cancel">True: DragLeave instead of Drop (hover only).</param>
    public static async Task<JsonObject> RunAsync(nint window, double scale, double x, double y, IReadOnlyList<string> paths, bool cancel)
    {
        var report = new JsonObject { ["x"] = x, ["y"] = y, ["scale"] = scale };
        var point = new Point { X = (int)Math.Round(x * scale), Y = (int)Math.Round(y * scale) };
        ClientToScreen(window, ref point);
        report["screen"] = $"{point.X},{point.Y}";

        var windows = new JsonArray();
        foreach (var (hwnd, depth) in Tree(window))
        {
            windows.Add(new JsonObject
            {
                ["class"] = ClassName(hwnd),
                ["depth"] = depth,
                ["visible"] = IsWindowVisible(hwnd),
                ["dropTarget"] = GetProp(hwnd, DropTargetProperty) != 0,
                ["props"] = string.Join(",", PropNames(hwnd)),
            });
        }

        report["windows"] = windows;
        var hit = HitTest(window, point);
        var targetWindow = hit;
        while (targetWindow != 0 && GetProp(targetWindow, DropTargetProperty) == 0)
        {
            targetWindow = targetWindow == window ? 0 : GetParent(targetWindow);
        }

        report["hit"] = hit == 0 ? null : ClassName(hit);
        if (targetWindow == 0)
        {
            report["error"] = "No window under the point registered an OLE drop target.";
            return report;
        }

        report["target"] = ClassName(targetWindow);
        var unknown = GetProp(targetWindow, DropTargetProperty);
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in Ole.IID_IDropTarget, out var targetPointer));
        var target = (IDropTarget)Marshal.GetObjectForIUnknown(targetPointer);
        Marshal.Release(targetPointer);

        var data = ExplorerDataObject(paths);
        try
        {
            var calls = new JsonArray();
            report["calls"] = calls;
            var allowed = Ole.DROPEFFECT_COPY | DROPEFFECT_MOVE | DROPEFFECT_LINK;

            var effect = allowed;
            calls.Add(Call("DragEnter", () => target.DragEnter(data, Ole.MK_LBUTTON, point, ref effect), () => effect));
            await Task.Delay(60);

            // A few moves of a pixel or two, ending on the point, as a hand would.
            for (var i = 2; i >= 0; i--)
            {
                var at = new Point { X = point.X + i, Y = point.Y + i };
                effect = allowed;
                calls.Add(Call("DragOver", () => target.DragOver(Ole.MK_LBUTTON, at, ref effect), () => effect));
                await Task.Delay(60);
            }

            if (cancel)
            {
                calls.Add(Call("DragLeave", target.DragLeave, () => 0));
            }
            else
            {
                // The button is up when Explorer's loop calls Drop.
                effect = allowed;
                calls.Add(Call("Drop", () => target.Drop(data, 0, point, ref effect), () => effect));
            }
        }
        finally
        {
            Marshal.Release(data);
        }

        return report;
    }

    private static JsonObject Call(string name, Action call, Func<uint> effect)
    {
        try
        {
            call();
            return new JsonObject { ["call"] = name, ["hr"] = 0, ["effect"] = effect() };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["call"] = name, ["hr"] = $"0x{ex.HResult:X8}", ["error"] = ex.Message };
        }
    }

    /// <summary>The data object File Explorer's folder view gives a drag of these files (CF_HDROP, shell ID lists, …).</summary>
    private static nint ExplorerDataObject(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            throw new ArgumentException("Nothing to drop.", nameof(paths));
        }

        var absolute = new List<nint>();
        try
        {
            object? parent = null;
            var children = new nint[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                Marshal.ThrowExceptionForHR(Ole.SHParseDisplayName(Path.GetFullPath(paths[i]), 0, out var pidl, 0, out _));
                absolute.Add(pidl);
                Marshal.ThrowExceptionForHR(Ole.SHBindToParent(pidl, Ole.IID_IShellFolder, out var folder, out children[i]));
                parent ??= folder;
            }

            ((IShellFolder)parent!).GetUIObjectOf(0, (uint)children.Length, children, Ole.IID_IDataObject, 0, out var data);
            var pointer = Marshal.GetComInterfaceForObject(data, typeof(System.Runtime.InteropServices.ComTypes.IDataObject));
            Marshal.ReleaseComObject(data);
            return pointer;
        }
        finally
        {
            foreach (var pidl in absolute)
            {
                Ole.CoTaskMemFree(pidl);
            }
        }
    }

    /// <summary>The window and its descendants, depth first.</summary>
    private static List<(nint Hwnd, int Depth)> Tree(nint window)
    {
        var list = new List<(nint, int)> { (window, 0) };
        void Walk(nint parent, int depth)
        {
            var child = GetWindow(parent, GW_CHILD);
            while (child != 0)
            {
                list.Add((child, depth));
                Walk(child, depth + 1);
                child = GetWindow(child, GW_HWNDNEXT);
            }
        }

        Walk(window, 1);
        return list;
    }

    /// <summary>
    /// The deepest visible window of ours that contains the screen point, by geometry only: unlike WindowFromPoint it
    /// doesn't care what else is on the desktop or where the real cursor is.
    /// </summary>
    private static nint HitTest(nint window, Point point)
    {
        nint best = 0;
        var bestDepth = -1;
        foreach (var (hwnd, depth) in Tree(window))
        {
            if (depth > bestDepth && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var r) &&
                point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom)
            {
                best = hwnd;
                bestDepth = depth;
            }
        }

        return best;
    }

    /// <summary>The window's property names (OLE keeps its drop target in one).</summary>
    private static List<string> PropNames(nint hwnd)
    {
        var names = new List<string>();
        EnumPropsExW(hwnd, (_, name, _, _) =>
        {
            names.Add(name >> 16 == 0 ? $"#{name}" : Marshal.PtrToStringUni(name) ?? "?");
            return true;
        }, 0);
        return names;
    }

    private delegate bool PropEnumProc(nint hwnd, nint name, nint data, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int EnumPropsExW(nint hwnd, PropEnumProc callback, nint param);

    private static string ClassName(nint hwnd)
    {
        var text = new StringBuilder(256);
        return GetClassNameW(hwnd, text, text.Capacity) > 0 ? text.ToString() : "?";
    }

    private const uint DROPEFFECT_MOVE = 2;
    private const uint DROPEFFECT_LINK = 4;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_CHILD = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetPropW(nint hwnd, string name);

    private static nint GetProp(nint hwnd, string name) => GetPropW(hwnd, name);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetParent(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint hwnd, ref Point point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int max);
}
