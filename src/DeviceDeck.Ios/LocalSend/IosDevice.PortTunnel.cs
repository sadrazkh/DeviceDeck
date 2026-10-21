using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Device;

// LocalSend over the cable (round 3): usbmux Connect reaches a TCP port on the phone's own loopback, so the PC can
// talk to the LocalSend app's server without Wi-Fi. No lockdown session is involved.
public sealed partial class IosDevice : IDevicePortTunnel
{
    public async Task<Stream> ConnectPortAsync(int port, CancellationToken ct)
    {
        if (_gone)
            throw new DeviceException(DeviceErrorCodes.Disconnected, "The iPhone was disconnected.");
        try
        {
            return await Connector().ConnectPortAsync(port, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.Protocol)
        {
            // usbmux answers "connection refused" (result 3) when no app on the phone listens on that port.
            throw new DeviceException(IDevicePortTunnel.PortClosedCode, $"Nothing on the iPhone listens on port {port}.", ex);
        }
    }
}
