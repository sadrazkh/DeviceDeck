namespace DeviceDeck.Core.Abstractions;

// LocalSend over the cable (round 3): an iPhone opens TCP connections to its own ports through usbmux.

/// <summary>A phone whose own TCP ports can be reached over its cable, without Wi-Fi.</summary>
public interface IDevicePortTunnel
{
    /// <summary>
    /// A raw stream to <paramref name="port"/> on the phone (its loopback). Throws <see cref="DeviceException"/>
    /// <see cref="PortClosedCode"/> when nothing on the phone listens there, <c>device_disconnected</c> when the phone is gone.
    /// </summary>
    Task<Stream> ConnectPortAsync(int port, CancellationToken ct);

    public const string PortClosedCode = "port_closed";
}
