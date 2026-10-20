using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Tests.Infrastructure;

internal sealed record FakeDevice(string Serial, string State, long TransportId, string Model = "Pixel_7", string Product = "panther");

internal sealed record FakeShellReply(byte[] Stdout, string Stderr, int ExitCode)
{
    public static FakeShellReply Text(string stdout, int exitCode = 0, string stderr = "") => new(Encoding.UTF8.GetBytes(stdout), stderr, exitCode);
}

/// <summary>
/// Speaks enough of the adb server's smart-socket protocol for the transport tests: host:version, devices-l,
/// track-devices-l (pushes updates), transport selection (with "device unauthorized" for such devices), shell v2,
/// exec with stdin, sync STAT/RECV/SEND/QUIT, and reboot:. Listens on an OS-assigned loopback port.
/// </summary>
internal sealed partial class FakeAdbServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<NetworkStream> _trackers = [];
    private readonly object _gate = new();
    private readonly Task _accept;
    private List<FakeDevice> _devices = [];

    public FakeAdbServer()
    {
        _listener.Start();
        _accept = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>
    /// False = behave like a port nobody serves yet: connections are closed at once, so host:version gets no answer.
    /// host:kill sets it back to false, like a real server exiting.
    /// </summary>
    public volatile bool Running = true;

    /// <summary>What host:mdns:check answers (adb's text when the server runs with ADB_MDNS=0).</summary>
    public string MdnsCheck { get; set; } = "ERROR: mdns discovery disabled";

    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public Func<string, FakeShellReply> Shell { get; set; } = command => FakeShellReply.Text("", 127, $"sh: {command}: not found");

    public Func<string, byte[], string> Exec { get; set; } = (_, _) => "Success\n";

    public async Task SetDevicesAsync(params FakeDevice[] devices)
    {
        List<NetworkStream> trackers;
        lock (_gate)
        {
            _devices = devices.ToList();
            trackers = _trackers.ToList();
        }
        foreach (var tracker in trackers)
        {
            try
            {
                await WriteHexStringAsync(tracker, DeviceList(), _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private string DeviceList()
    {
        lock (_gate)
        {
            return string.Concat(_devices.Select(d => d.State == "device"
                ? $"{d.Serial,-22} device usb:1-1 product:{d.Product} model:{d.Model} device:{d.Product} transport_id:{d.TransportId}\n"
                : $"{d.Serial,-22} {d.State} usb:1-1 transport_id:{d.TransportId}\n"));
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    [GeneratedRegex(@"-S (?<size>\d+)")]
    private static partial Regex SizeArgument();

    private async Task HandleAsync(TcpClient client)
    {
        var ct = _stop.Token;
        using var _ = client;
        var stream = client.GetStream();
        bool keepOpen = false;
        if (!Running)
        {
            return;
        }
        try
        {
            while (true)
            {
                string? request = await ReadRequestAsync(stream, ct);
                if (request == null)
                {
                    return;
                }
                Requests.Enqueue(request);

                if (request == "host:version")
                {
                    await OkayAsync(stream, ct);
                    await WriteHexStringAsync(stream, "0029", ct);
                    return;
                }
                if (request is "host:devices-l" or "host:devices")
                {
                    await OkayAsync(stream, ct);
                    await WriteHexStringAsync(stream, DeviceList(), ct);
                    return;
                }
                if (request == "host:track-devices-l")
                {
                    await OkayAsync(stream, ct);
                    lock (_gate)
                    {
                        _trackers.Add(stream);
                    }
                    await WriteHexStringAsync(stream, DeviceList(), ct);
                    keepOpen = true;
                    await Task.Delay(Timeout.Infinite, ct);
                    return;
                }
                if (request.StartsWith("host:transport", StringComparison.Ordinal))
                {
                    FakeDevice? device;
                    lock (_gate)
                    {
                        device = request.StartsWith("host:transport-id:", StringComparison.Ordinal)
                            ? _devices.FirstOrDefault(d => d.TransportId.ToString() == request["host:transport-id:".Length..])
                            : _devices.FirstOrDefault(d => d.Serial == request["host:transport:".Length..]);
                    }
                    if (device == null)
                    {
                        await FailAsync(stream, "device not found", ct);
                        return;
                    }
                    if (device.State == "unauthorized")
                    {
                        await FailAsync(stream, "device unauthorized.\nThis adb server's $ADB_VENDOR_KEYS is not set\nTry 'adb kill-server' if that seems wrong.\nOtherwise check for a confirmation dialog on your device.", ct);
                        return;
                    }
                    await OkayAsync(stream, ct);
                    continue; // the service request follows on this connection
                }
                if (request.StartsWith("shell,v2,raw:", StringComparison.Ordinal))
                {
                    await OkayAsync(stream, ct);
                    // Drain the client's close-stdin packet so closing doesn't reset the connection.
                    byte[] closeStdin = new byte[5];
                    await stream.ReadExactlyAsync(closeStdin, ct);
                    var reply = Shell(request["shell,v2,raw:".Length..]);
                    for (int offset = 0; offset < reply.Stdout.Length; offset += 8192)
                    {
                        await WritePacketAsync(stream, 1, reply.Stdout.AsSpan(offset, Math.Min(8192, reply.Stdout.Length - offset)).ToArray(), ct);
                    }
                    if (reply.Stderr.Length > 0)
                    {
                        await WritePacketAsync(stream, 2, Encoding.UTF8.GetBytes(reply.Stderr), ct);
                    }
                    await WritePacketAsync(stream, 3, [(byte)reply.ExitCode], ct);
                    return;
                }
                if (request.StartsWith("exec:", StringComparison.Ordinal))
                {
                    await OkayAsync(stream, ct);
                    string command = request["exec:".Length..];
                    byte[] input = [];
                    if (SizeArgument().Match(command) is { Success: true } size && command.Contains("install", StringComparison.Ordinal))
                    {
                        input = new byte[int.Parse(size.Groups["size"].Value)];
                        await stream.ReadExactlyAsync(input, ct);
                    }
                    byte[] output = Encoding.UTF8.GetBytes(Exec(command, input));
                    await stream.WriteAsync(output, ct);
                    return;
                }
                if (request == "sync:")
                {
                    await OkayAsync(stream, ct);
                    await SyncAsync(stream, ct);
                    return;
                }
                if (request == "host:mdns:check")
                {
                    await OkayAsync(stream, ct);
                    await WriteHexStringAsync(stream, MdnsCheck, ct);
                    return;
                }
                if (request == "host:kill")
                {
                    Running = false;
                    await OkayAsync(stream, ct);
                    return;
                }
                if (request.StartsWith("reboot:", StringComparison.Ordinal))
                {
                    await OkayAsync(stream, ct);
                    return;
                }
                await FailAsync(stream, $"unknown host service '{request}'", ct);
                return;
            }
        }
        catch (Exception) when (ct.IsCancellationRequested || !keepOpen)
        {
        }
        finally
        {
            lock (_gate)
            {
                _trackers.Remove(stream);
            }
        }
    }

    private async Task SyncAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[8];
        while (true)
        {
            if (!await TryReadExactlyAsync(stream, header, ct))
            {
                return;
            }
            string id = Encoding.ASCII.GetString(header, 0, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            switch (id)
            {
                case "STAT":
                {
                    string path = await ReadStringAsync(stream, length, ct);
                    byte[] reply = new byte[16];
                    Encoding.ASCII.GetBytes("STAT", reply);
                    if (Files.TryGetValue(path, out byte[]? data))
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), 0x81A4);
                        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), (uint)data.Length);
                        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(12), 1700000000);
                    }
                    await stream.WriteAsync(reply, ct);
                    break;
                }
                case "RECV":
                {
                    string path = await ReadStringAsync(stream, length, ct);
                    if (!Files.TryGetValue(path, out byte[]? data))
                    {
                        await WriteSyncAsync(stream, "FAIL", Encoding.UTF8.GetBytes("No such file or directory"), ct);
                        break;
                    }
                    for (int offset = 0; offset < data.Length; offset += 64 * 1024 - 8)
                    {
                        await WriteSyncAsync(stream, "DATA", data.AsSpan(offset, Math.Min(64 * 1024 - 8, data.Length - offset)).ToArray(), ct);
                    }
                    await WriteSyncAsync(stream, "DONE", [], ct);
                    break;
                }
                case "SEND":
                {
                    string spec = await ReadStringAsync(stream, length, ct);
                    string path = spec[..spec.LastIndexOf(',')];
                    using var content = new MemoryStream();
                    while (true)
                    {
                        await stream.ReadExactlyAsync(header, ct);
                        string chunk = Encoding.ASCII.GetString(header, 0, 4);
                        int size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                        if (chunk == "DONE")
                        {
                            break;
                        }
                        byte[] data = new byte[size];
                        await stream.ReadExactlyAsync(data, ct);
                        content.Write(data);
                    }
                    Files[path] = content.ToArray();
                    await WriteSyncAsync(stream, "OKAY", [], ct);
                    break;
                }
                case "QUIT":
                    return;
                default:
                    await WriteSyncAsync(stream, "FAIL", Encoding.UTF8.GetBytes($"unknown sync command {id}"), ct);
                    return;
            }
        }
    }

    private static async Task<bool> TryReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                return false;
            }
            total += read;
        }
        return true;
    }

    private static async Task<string> ReadStringAsync(NetworkStream stream, int length, CancellationToken ct)
    {
        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task WriteSyncAsync(NetworkStream stream, string id, byte[] data, CancellationToken ct)
    {
        byte[] message = new byte[8 + data.Length];
        Encoding.ASCII.GetBytes(id, message);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(4), data.Length);
        data.CopyTo(message, 8);
        await stream.WriteAsync(message, ct);
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        if (!await TryReadExactlyAsync(stream, header, ct))
        {
            return null;
        }
        int length = int.Parse(Encoding.ASCII.GetString(header), System.Globalization.NumberStyles.HexNumber);
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return Encoding.UTF8.GetString(body);
    }

    private static Task OkayAsync(NetworkStream stream, CancellationToken ct) => stream.WriteAsync("OKAY"u8.ToArray(), ct).AsTask();

    private static async Task FailAsync(NetworkStream stream, string message, CancellationToken ct)
    {
        await stream.WriteAsync("FAIL"u8.ToArray(), ct);
        await WriteHexStringAsync(stream, message, ct);
    }

    private static async Task WriteHexStringAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString("x4")), ct);
        await stream.WriteAsync(body, ct);
    }

    private static async Task WritePacketAsync(NetworkStream stream, byte id, byte[] data, CancellationToken ct)
    {
        byte[] packet = new byte[5 + data.Length];
        packet[0] = id;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), (uint)data.Length);
        data.CopyTo(packet, 5);
        await stream.WriteAsync(packet, ct);
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accept;
        }
        catch (Exception)
        {
        }
        _stop.Dispose();
    }
}
