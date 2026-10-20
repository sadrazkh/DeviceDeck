using System.Net;

namespace DeviceDeck.Ios.Protocol.Usbmux;

/// <summary>Opens fresh usbmux sockets. Swapped for an in-memory fake in tests.</summary>
public interface IUsbmuxConnector
{
    Task<UsbmuxClient> ConnectAsync(CancellationToken ct);
}

public sealed class TcpUsbmuxConnector(IPEndPoint? endpoint = null) : IUsbmuxConnector
{
    public IPEndPoint Endpoint { get; } = endpoint ?? UsbmuxClient.DefaultEndpoint();

    public Task<UsbmuxClient> ConnectAsync(CancellationToken ct) => UsbmuxClient.ConnectAsync(Endpoint, ct);
}
