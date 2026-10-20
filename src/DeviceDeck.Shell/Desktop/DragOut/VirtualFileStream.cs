using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DeviceDeck.Shell.Desktop.Interop;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace DeviceDeck.Shell.Desktop.DragOut;

/// <summary>
/// The contents of one virtual file, as File Explorer reads it: bytes from the item's staging file, waiting for
/// the ones the transfer queue hasn't copied yet. A read only returns short at the end of the file, because some
/// readers take a short read for the end. Not agile on purpose (see <see cref="VirtualFileDataObject"/>).
/// </summary>
[ComVisible(true)]
internal sealed class VirtualFileStream(DragOutSession session, DragOutFile file) : IStream, ICustomQueryInterface, IDisposable
{
    private const int STATFLAG_NONAME = 1;
    private const int STGTY_STREAM = 2;

    private readonly object _gate = new();
    private long _position;
    private FileStream? _reader;

    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        var total = 0;
        lock (_gate)
        {
            try
            {
                while (total < cb)
                {
                    var want = cb - total;
                    if (file.Size is { } size)
                    {
                        want = (int)Math.Min(want, size - _position);
                    }

                    if (want <= 0)
                    {
                        CloseReader(); // end of file; a seek back opens it again
                        break;
                    }

                    var available = session.WaitFor(file.ItemIndex, _position + want);
                    var count = (int)Math.Min(want, available - _position);
                    if (count <= 0)
                    {
                        CloseReader(); // the item is complete: end of file
                        break;
                    }

                    ReadStaged(pv.AsSpan(total, count));
                    total += count;
                }
            }
            catch (IOException ex)
            {
                CloseReader();
                throw new COMException(ex.Message, Ole.STG_E_READFAULT);
            }
        }

        if (pcbRead != 0)
        {
            Marshal.WriteInt32(pcbRead, total);
        }
    }

    private void ReadStaged(Span<byte> buffer)
    {
        _reader ??= new FileStream(session.StagingPath(file.ItemIndex), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        _reader.Position = _position;
        _reader.ReadExactly(buffer);
        _position += buffer.Length;
    }

    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        lock (_gate)
        {
            var target = dwOrigin switch
            {
                0 => dlibMove,
                1 => _position + dlibMove,
                2 => (file.Size ?? WaitForEnd()) + dlibMove,
                _ => throw new COMException("Bad seek origin.", Ole.STG_E_INVALIDFUNCTION),
            };
            if (target < 0)
            {
                throw new COMException("Seek before the start.", Ole.STG_E_INVALIDFUNCTION);
            }

            _position = target;
            if (plibNewPosition != 0)
            {
                Marshal.WriteInt64(plibNewPosition, _position);
            }
        }
    }

    private long WaitForEnd()
    {
        try
        {
            return session.WaitFor(file.ItemIndex, long.MaxValue);
        }
        catch (IOException ex)
        {
            throw new COMException(ex.Message, Ole.STG_E_READFAULT);
        }
    }

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        var modified = file.Modified is { } time ? time.UtcDateTime.ToFileTimeUtc() : 0;
        pstatstg = new STATSTG
        {
            pwcsName = (grfStatFlag & STATFLAG_NONAME) != 0 ? null! : file.Name,
            type = STGTY_STREAM,
            cbSize = file.Size ?? 0,
            mtime = new System.Runtime.InteropServices.ComTypes.FILETIME { dwLowDateTime = (int)(modified & 0xFFFFFFFF), dwHighDateTime = (int)(modified >> 32) },
            grfMode = 0,
        };
    }

    public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten)
    {
        var buffer = new byte[1 << 16];
        long copied = 0;
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (copied < cb)
            {
                Read(buffer, (int)Math.Min(buffer.Length, cb - copied), read);
                var n = Marshal.ReadInt32(read);
                if (n == 0)
                {
                    break;
                }

                pstm.Write(n == buffer.Length ? buffer : buffer[..n], n, 0);
                copied += n;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
        }

        if (pcbRead != 0)
        {
            Marshal.WriteInt64(pcbRead, copied);
        }

        if (pcbWritten != 0)
        {
            Marshal.WriteInt64(pcbWritten, copied);
        }
    }

    public void Write(byte[] pv, int cb, nint pcbWritten) => throw new COMException("Read-only.", Ole.STG_E_ACCESSDENIED);

    public void SetSize(long libNewSize) => throw new COMException("Read-only.", Ole.STG_E_ACCESSDENIED);

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert()
    {
    }

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not supported.", Ole.STG_E_INVALIDFUNCTION);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not supported.", Ole.STG_E_INVALIDFUNCTION);

    public void Clone(out IStream ppstm) => throw new COMException("Not supported.", Ole.E_NOTIMPL);

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv) => VirtualFileDataObject.RefuseAgility(ref iid, out ppv);

    private void CloseReader()
    {
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseReader();
        }
    }
}
