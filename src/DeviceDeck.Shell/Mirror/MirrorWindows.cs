using DeviceDeck.Bridge.Toolbox;
using DeviceDeck.Shell.Hosting;
using Microsoft.UI.Dispatching;

namespace DeviceDeck.Shell.Mirror;

/// <summary>One mirror window per phone, opened on the UI thread; asking again brings it to the front.</summary>
internal sealed class MirrorWindows(HostServices host, DispatcherQueue dispatcher) : IMirrorWindows
{
    private readonly Dictionary<string, MirrorWindow> _open = new(StringComparer.Ordinal);

    public void Open(string deviceId)
    {
        dispatcher.TryEnqueue(() =>
        {
            if (_open.TryGetValue(deviceId, out var existing))
            {
                existing.BringToFront();
                return;
            }

            try
            {
                var window = new MirrorWindow(host, deviceId);
                _open[deviceId] = window;
                window.Closed += (_, _) => _open.Remove(deviceId);
                window.Activate();
            }
            catch (Exception ex)
            {
                ShellLog.Error($"Could not open the mirror window for {deviceId}.", ex);
            }
        });
    }
}
