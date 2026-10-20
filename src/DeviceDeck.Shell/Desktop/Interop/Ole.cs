using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeviceDeck.Shell.Desktop.Interop;

// OLE drag and drop, virtual files and the clipboard formats File Explorer reads. Plain Win32: no WinUI here,
// so tests/DeviceDeck.Shell.Tests can compile these files on their own.

[ComImport]
[Guid("00000121-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escapePressed, uint keyState);

    [PreserveSig]
    int GiveFeedback(uint effect);
}

/// <summary>IDataObjectAsyncCapability (formerly IAsyncOperation): lets File Explorer copy virtual files on its own thread.</summary>
[ComImport]
[Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOpAsync);

    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOpAsync);

    void StartOperation(IBindCtx? reserved);

    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inAsyncOp);

    void EndOperation(int result, IBindCtx? reserved, uint effects);
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    void ParseDisplayName(nint hwnd, nint bindContext, [MarshalAs(UnmanagedType.LPWStr)] string displayName, out uint eaten, out nint pidl, ref uint attributes);

    void EnumObjects(nint hwnd, uint flags, out nint enumIdList);

    void BindToObject(nint pidl, nint bindContext, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);

    void BindToStorage(nint pidl, nint bindContext, in Guid riid, out nint result);

    [PreserveSig]
    int CompareIDs(nint lParam, nint pidl1, nint pidl2);

    void CreateViewObject(nint hwndOwner, in Guid riid, out nint result);

    void GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] pidls, ref uint attributes);

    void GetUIObjectOf(nint hwndOwner, uint count, [MarshalAs(UnmanagedType.LPArray)] nint[] pidls, in Guid riid, nint reserved, [MarshalAs(UnmanagedType.Interface)] out object result);
}

/// <summary>A drop target (File Explorer's folder, in tests).</summary>
[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDropTarget
{
    void DragEnter(nint dataObject, uint keyState, Point point, ref uint effect);

    void DragOver(uint keyState, Point point, ref uint effect);

    void DragLeave();

    void Drop(nint dataObject, uint keyState, Point point, ref uint effect);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

internal static class Ole
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int DRAGDROP_S_DROP = 0x00040100;
    public const int DRAGDROP_S_CANCEL = 0x00040101;
    public const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    public const int DATA_S_SAMEFORMATETC = 0x00040130;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int DV_E_TYMED = unchecked((int)0x80040069);
    public const int DV_E_LINDEX = unchecked((int)0x80040068);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int STG_E_READFAULT = unchecked((int)0x8003001E);
    public const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
    public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);

    public const uint DROPEFFECT_NONE = 0;
    public const uint DROPEFFECT_COPY = 1;
    public const uint MK_LBUTTON = 0x1;
    public const uint MK_RBUTTON = 0x2;

    public const short CF_HDROP = 15;

    public static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_IMarshal = new("00000003-0000-0000-C000-000000000046");
    public static readonly Guid IID_IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    public static readonly Guid IID_IDropTarget = new("00000122-0000-0000-C000-000000000046");
    public static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");

    public static short FileGroupDescriptorW { get; } = Register("FileGroupDescriptorW");
    public static short FileContents { get; } = Register("FileContents");
    public static short PreferredDropEffect { get; } = Register("Preferred DropEffect");
    public static short PerformedDropEffect { get; } = Register("Performed DropEffect");

    private static short Register(string name) => unchecked((short)RegisterClipboardFormatW(name));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterClipboardFormatW(string format);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int DoDragDrop(nint dataObject, IDropSource dropSource, uint okEffects, out uint effect);

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern int SHDoDragDrop(nint hwnd, nint dataObject, IDropSource? dropSource, uint okEffects, out uint effect);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoMarshalInterThreadInterfaceInStream(in Guid riid, nint unknown, out nint stream);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoGetInterfaceAndReleaseStream(nint stream, in Guid riid, out nint result);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern int SHCreateStdEnumFmtEtc(uint count, [In] FORMATETC[] formats, out IEnumFORMATETC enumerator);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern int SHBindToParent(nint pidl, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object parent, out nint lastPidl);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void CoTaskMemFree(nint pointer);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nuint GlobalSize(nint memory);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern nint GlobalFree(nint memory);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ScreenToClient(nint hwnd, ref Point point);

    public const uint GA_ROOT = 2;
    public const int SM_SWAPBUTTON = 23;
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;

    /// <summary>A movable HGLOBAL holding <paramref name="bytes"/>; the receiver frees it.</summary>
    public static nint ToHGlobal(ReadOnlySpan<byte> bytes)
    {
        const uint GMEM_MOVEABLE = 0x0002;
        const uint GMEM_ZEROINIT = 0x0040;
        var handle = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (nuint)Math.Max(bytes.Length, 1));
        if (handle == 0)
        {
            throw new OutOfMemoryException("GlobalAlloc failed.");
        }

        var pointer = GlobalLock(handle);
        try
        {
            unsafe
            {
                bytes.CopyTo(new Span<byte>((void*)pointer, bytes.Length));
            }
        }
        finally
        {
            GlobalUnlock(handle);
        }

        return handle;
    }

    public static byte[] FromHGlobal(nint handle)
    {
        var size = (int)GlobalSize(handle);
        var pointer = GlobalLock(handle);
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Is the primary (left, unless swapped) mouse button held down right now?</summary>
    public static bool PrimaryButtonDown()
    {
        var key = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }
}
