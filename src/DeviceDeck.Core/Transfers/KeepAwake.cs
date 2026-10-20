using System.Runtime.InteropServices;

namespace DeviceDeck.Core.Transfers;

/// <summary>Keeps the PC from sleeping while at least one hold is open.</summary>
public interface IKeepAwake
{
    IDisposable Acquire();

    bool IsHeld { get; }
}

/// <summary>Reference-counted holds; the first opens the platform request, the last closes it.</summary>
public abstract class KeepAwakeBase : IKeepAwake
{
    private readonly Lock _gate = new();
    private int _holds;

    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _holds > 0;
            }
        }
    }

    public IDisposable Acquire()
    {
        lock (_gate)
        {
            if (_holds++ == 0)
            {
                Begin();
            }
        }

        return new Hold(this);
    }

    protected abstract void Begin();

    protected abstract void End();

    private void Release()
    {
        lock (_gate)
        {
            if (--_holds == 0)
            {
                End();
            }
        }
    }

    private sealed class Hold(KeepAwakeBase owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}

/// <summary>Counts holds and does nothing else (tests, non-Windows).</summary>
public sealed class NullKeepAwake : KeepAwakeBase
{
    protected override void Begin()
    {
    }

    protected override void End()
    {
    }
}

/// <summary>A Windows power request (PowerRequestSystemRequired): not tied to a thread, shows in <c>powercfg /requests</c>.</summary>
public sealed partial class WindowsKeepAwake : KeepAwakeBase
{
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;

    private nint _request;
    private nint _reason;

    protected override void Begin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _reason = Marshal.StringToHGlobalUni("DeviceDeck is copying files to or from a phone.");
        var context = new ReasonContext { Version = PowerRequestContextVersion, Flags = PowerRequestContextSimpleString, SimpleReasonString = _reason };
        _request = PowerCreateRequest(ref context);
        if (_request == -1 || _request == 0 || !PowerSetRequest(_request, PowerRequestSystemRequired))
        {
            Close();
        }
    }

    protected override void End() => Close();

    private void Close()
    {
        if (_request is not (0 or -1))
        {
            PowerClearRequest(_request, PowerRequestSystemRequired);
            CloseHandle(_request);
        }

        _request = 0;
        if (_reason != 0)
        {
            Marshal.FreeHGlobal(_reason);
            _reason = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint handle, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint handle, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
