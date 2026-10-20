using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeviceDeck.Shell.Desktop.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeviceDeck.Shell.Desktop.DragOut;

/// <summary>
/// What a drag out of the window carries. For phone items: virtual files (FileGroupDescriptorW + FileContents),
/// whose bytes arrive through the transfer queue while File Explorer reads them, with IDataObjectAsyncCapability
/// so Explorer copies on its own thread. For PC files: CF_HDROP with their paths (Explorer copies those itself).
/// It also keeps whatever the drop target or the drag-image helper stores with SetData.
/// <para>
/// Not agile: QueryInterface for IMarshal and IAgileObject fails, so COM marshals it the standard way from the
/// apartment it was created in. Created on a thread-pool (MTA) thread, File Explorer's calls (and the waiting
/// IStream reads) run on RPC threads there, never on the window's UI thread that runs DoDragDrop.
/// </para>
/// </summary>
[ComVisible(true)]
internal sealed class VirtualFileDataObject : ComIDataObject, IDataObjectAsyncCapability, ICustomQueryInterface
{
    private const uint FD_ATTRIBUTES = 0x00000004;
    private const uint FD_WRITESTIME = 0x00000020;
    private const uint FD_FILESIZE = 0x00000040;
    private const uint FD_PROGRESSUI = 0x00004000;
    private const uint FD_UNICODE = 0x80000000;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int FileDescriptorSize = 592;

    private readonly DragOutSession? _session;
    private readonly IReadOnlyList<string>? _pcPaths;
    private readonly object _gate = new();
    private readonly Dictionary<short, byte[]> _stored = [];
    private bool _inOperation;

    /// <summary>Virtual files from the phone.</summary>
    public VirtualFileDataObject(DragOutSession session) => _session = session;

    /// <summary>Real files on this PC.</summary>
    public VirtualFileDataObject(IReadOnlyList<string> pcPaths) => _pcPaths = pcPaths;

    public bool InAsyncOperation
    {
        get
        {
            lock (_gate)
            {
                return _inOperation;
            }
        }
    }

    private IEnumerable<FORMATETC> Formats()
    {
        if (_session is not null)
        {
            yield return Format(Ole.FileGroupDescriptorW, TYMED.TYMED_HGLOBAL);
            yield return Format(Ole.FileContents, TYMED.TYMED_ISTREAM);
        }
        else
        {
            yield return Format(Ole.CF_HDROP, TYMED.TYMED_HGLOBAL);
        }

        yield return Format(Ole.PreferredDropEffect, TYMED.TYMED_HGLOBAL);
        short[] stored;
        lock (_gate)
        {
            stored = [.. _stored.Keys];
        }

        foreach (var format in stored)
        {
            yield return Format(format, TYMED.TYMED_HGLOBAL);
        }
    }

    private static FORMATETC Format(short format, TYMED tymed) =>
        new() { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, ptd = 0, tymed = tymed };

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == Ole.FileContents && _session is not null)
        {
            if ((format.tymed & TYMED.TYMED_ISTREAM) == 0)
            {
                throw new COMException("FileContents comes as a stream.", Ole.DV_E_TYMED);
            }

            var files = _session.Files;
            if (format.lindex < 0 || format.lindex >= files.Count || files[format.lindex].IsDirectory)
            {
                throw new COMException("No file at that index.", Ole.DV_E_LINDEX);
            }

            _session.EnsureJob();
            var stream = new VirtualFileStream(_session, files[format.lindex]);
            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            return;
        }

        if ((format.tymed & TYMED.TYMED_HGLOBAL) == 0)
        {
            throw new COMException("Only HGLOBAL is offered for this format.", Ole.DV_E_TYMED);
        }

        byte[]? bytes;
        if (format.cfFormat == Ole.FileGroupDescriptorW && _session is not null)
        {
            bytes = FileGroupDescriptor(_session.Files);
        }
        else if (format.cfFormat == Ole.CF_HDROP && _pcPaths is not null)
        {
            bytes = HDrop(_pcPaths);
        }
        else if (format.cfFormat == Ole.PreferredDropEffect)
        {
            bytes = BitConverter.GetBytes(Ole.DROPEFFECT_COPY);
        }
        else
        {
            lock (_gate)
            {
                bytes = _stored.GetValueOrDefault(format.cfFormat);
            }
        }

        if (bytes is null)
        {
            throw new COMException("That format isn't offered.", Ole.DV_E_FORMATETC);
        }

        medium.tymed = TYMED.TYMED_HGLOBAL;
        medium.unionmember = Ole.ToHGlobal(bytes);
    }

    public int QueryGetData(ref FORMATETC format)
    {
        foreach (var offered in Formats())
        {
            if (offered.cfFormat == format.cfFormat)
            {
                return (offered.tymed & format.tymed) != 0 ? Ole.S_OK : Ole.DV_E_TYMED;
            }
        }

        return Ole.DV_E_FORMATETC;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0)
            {
                throw new COMException("Only HGLOBAL data is kept.", Ole.DV_E_TYMED);
            }

            var bytes = Ole.FromHGlobal(medium.unionmember);
            lock (_gate)
            {
                _stored[formatIn.cfFormat] = bytes;
            }
        }
        finally
        {
            if (release)
            {
                Ole.ReleaseStgMedium(ref medium);
            }
        }
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
        {
            throw new COMException("Only reading is enumerated.", Ole.E_NOTIMPL);
        }

        var formats = Formats().ToArray();
        Marshal.ThrowExceptionForHR(Ole.SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var enumerator));
        return enumerator;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = 0;
        return Ole.DATA_S_SAMEFORMATETC;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Not supported.", Ole.E_NOTIMPL);

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return Ole.OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Not supported.", Ole.OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return Ole.OLE_E_ADVISENOTSUPPORTED;
    }

    // ---- IDataObjectAsyncCapability ----

    public void SetAsyncMode(bool doOpAsync)
    {
        // Always asynchronous for virtual files; nothing to change.
    }

    public void GetAsyncMode(out bool isOpAsync) => isOpAsync = _session is not null;

    public void StartOperation(IBindCtx? reserved)
    {
        lock (_gate)
        {
            _inOperation = true;
        }

        _session?.StartOperation();
    }

    public void InOperation(out bool inAsyncOp) => inAsyncOp = InAsyncOperation;

    public void EndOperation(int result, IBindCtx? reserved, uint effects)
    {
        lock (_gate)
        {
            _inOperation = false;
        }

        _session?.EndOperation(result >= 0 && effects != Ole.DROPEFFECT_NONE);
    }

    // ---- not agile ----

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv) => RefuseAgility(ref iid, out ppv);

    internal static CustomQueryInterfaceResult RefuseAgility(ref Guid iid, out nint ppv)
    {
        ppv = 0;
        return iid == Ole.IID_IMarshal || iid == Ole.IID_IAgileObject ? CustomQueryInterfaceResult.Failed : CustomQueryInterfaceResult.NotHandled;
    }

    // ---- formats ----

    /// <summary>FILEGROUPDESCRIPTORW: a count, then one 592-byte FILEDESCRIPTORW per folder or file.</summary>
    internal static byte[] FileGroupDescriptor(IReadOnlyList<DragOutFile> files)
    {
        var bytes = new byte[4 + FileDescriptorSize * files.Count];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (uint)files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var d = bytes.AsSpan(4 + FileDescriptorSize * i, FileDescriptorSize);
            var flags = FD_ATTRIBUTES | FD_PROGRESSUI | FD_UNICODE;
            if (file.Size is { } size && !file.IsDirectory)
            {
                flags |= FD_FILESIZE;
                BitConverter.TryWriteBytes(d[64..], (uint)(size >> 32));
                BitConverter.TryWriteBytes(d[68..], (uint)(size & 0xFFFFFFFF));
            }

            if (file.Modified is { } modified)
            {
                flags |= FD_WRITESTIME;
                BitConverter.TryWriteBytes(d[56..], modified.UtcDateTime.ToFileTimeUtc());
            }

            BitConverter.TryWriteBytes(d[..4], flags);
            BitConverter.TryWriteBytes(d[36..], file.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL);
            var name = file.RelativePath.Length > 259 ? file.RelativePath[..259] : file.RelativePath;
            Encoding.Unicode.GetBytes(name, d[72..]);
        }

        return bytes;
    }

    /// <summary>DROPFILES followed by the double-NUL-terminated list of wide paths.</summary>
    internal static byte[] HDrop(IReadOnlyList<string> paths)
    {
        const int header = 20;
        var list = string.Join('\0', paths) + "\0\0";
        var bytes = new byte[header + Encoding.Unicode.GetByteCount(list)];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (uint)header);
        BitConverter.TryWriteBytes(bytes.AsSpan(16, 4), 1u); // fWide
        Encoding.Unicode.GetBytes(list, bytes.AsSpan(header));
        return bytes;
    }
}
