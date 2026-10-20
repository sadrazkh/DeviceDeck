using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DeviceDeck.Shell;

/// <summary>Single instance: a second launch hands its activation to the running window and exits.</summary>
public static partial class Program
{
    private const string InstanceKey = "DeviceDeck.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectedToRunningInstance())
        {
            return 0;
        }

        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });

        Shutdown();
        return 0;
    }

    /// <summary>
    /// The last window has closed and the message loop is over. Let a new launch start fresh instead of being
    /// redirected here, then stop the host and wait for it: the process must not exit before every provider's
    /// DisposeAsync has finished (the Android provider stops its adb server there).
    /// </summary>
    private static void Shutdown()
    {
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            AppInstance.GetCurrent().UnregisterKey();
        }
        catch (Exception ex)
        {
            Hosting.ShellLog.Error("Could not release the single-instance key.", ex);
        }

        if (App.Host is { } host)
        {
            // Off this thread: its dispatcher is gone, so nothing may try to resume on it.
            Task.Run(async () => await host.DisposeAsync()).GetAwaiter().GetResult();
        }
    }

    /// <summary>Raised in the first instance when a later launch was redirected to it.</summary>
    public static event EventHandler<AppActivationArguments>? Reactivated;

    private static bool RedirectedToRunningInstance()
    {
        var mine = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (mine.IsCurrent)
        {
            mine.Activated += (_, e) => Reactivated?.Invoke(null, e);
            return false;
        }

        var args = AppInstance.GetCurrent().GetActivatedEventArgs();

        // This process holds the foreground right now; let the running one take it.
        AllowSetForegroundWindow((uint)mine.ProcessId);

        // Redirect off the STA thread and wait with a COM-friendly wait, as the Windows App SDK sample does.
        using var done = new ManualResetEventSlim();
        _ = Task.Run(() =>
        {
            try
            {
                mine.RedirectActivationToAsync(args).AsTask().Wait();
            }
            finally
            {
                done.Set();
            }
        });
        var handles = new[] { done.WaitHandle.SafeWaitHandle.DangerousGetHandle() };
        _ = CoWaitForMultipleObjects(0, 10_000, 1, handles, out _);

        try
        {
            var window = Process.GetProcessById((int)mine.ProcessId).MainWindowHandle;
            if (window != 0)
            {
                SetForegroundWindow(window);
            }
        }
        catch (ArgumentException)
        {
        }

        return true;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, [In] nint[] handles, out uint index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);
}
