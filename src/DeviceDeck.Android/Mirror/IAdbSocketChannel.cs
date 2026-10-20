namespace DeviceDeck.Android.Mirror;

/// <summary>
/// Raw device sockets through the adb server ("localabstract:name", "tcp:port"): the smart-socket request is sent on a
/// fresh connection after selecting the phone, and the returned stream then carries the socket's bytes.
/// Nothing is bound on the PC (unlike `adb forward`).
/// </summary>
internal interface IAdbSocketChannel
{
    Task<Stream> OpenSocketAsync(string service, CancellationToken ct);
}
