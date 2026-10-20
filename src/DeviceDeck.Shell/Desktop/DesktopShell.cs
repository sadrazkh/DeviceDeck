using System.Diagnostics;
using DeviceDeck.Bridge;
using DeviceDeck.Shell.Desktop.DragOut;

namespace DeviceDeck.Shell.Desktop;

/// <summary><see cref="IDesktopShell"/> for the window: drag out, opening PC files, updates.</summary>
internal sealed class DesktopShell(DragOutService dragOut, IUpdateService updates) : IDesktopShell
{
    public IUpdateService Updates => updates;

    public Task<DragOutResult> DragOutAsync(DragOutParams request, CancellationToken ct) => dragOut.DragAsync(request, ct);

    public void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) ?? "" })?.Dispose();
}
