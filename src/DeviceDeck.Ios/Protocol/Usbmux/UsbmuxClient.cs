using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Usbmux;

/// <summary>
/// usbmux plist protocol (version 1): a 16-byte little-endian header {length, version=1, message=8, tag}
/// then an XML plist. On Windows, Apple Mobile Device Service listens on 127.0.0.1:27015.
/// One socket serves one purpose: a request/response exchange, a Listen stream, or a Connect tunnel.
/// </summary>
public sealed class UsbmuxClient : IAsyncDisposable
{
    public const int HeaderSize = 16;
    public const int DefaultPort = 27015;
    public const string EndpointEnvironmentVariable = "USBMUXD_SOCKET_ADDRESS";
    private const uint PlistVersion = 1;
    private const uint PlistMessage = 8;
    private const int MaxPayload = 16 * 1024 * 1024;

    private readonly Stream _stream;
    private uint _nextTag = 1;
    private bool _handedOver;

    public UsbmuxClient(Stream stream) => _stream = stream;

    /// <summary>127.0.0.1:27015, or USBMUXD_SOCKET_ADDRESS ("host:port") when set.</summary>
    public static IPEndPoint DefaultEndpoint()
    {
        var env = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env) && IPEndPoint.TryParse(env.Trim(), out var ep))
            return ep;
        return new IPEndPoint(IPAddress.Loopback, DefaultPort);
    }

    public static async Task<UsbmuxClient> ConnectAsync(IPEndPoint? endpoint, CancellationToken ct)
    {
        endpoint ??= DefaultEndpoint();
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            socket.Dispose();
            throw new DeviceException(DeviceErrorCodes.DriverMissing,
                $"Apple Mobile Device Service is not reachable on {endpoint}.", ex);
        }
        return new UsbmuxClient(new NetworkStream(socket, ownsSocket: true));
    }

    public async Task<IReadOnlyList<UsbmuxDevice>> ListDevicesAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(Message("ListDevices"), ct).ConfigureAwait(false);
        ThrowIfResultError(reply, "ListDevices");
        var list = reply.GetArray("DeviceList") ?? [];
        var devices = new List<UsbmuxDevice>(list.Count);
        foreach (var item in list)
        {
            if (item is PlistDict entry && entry.GetDict("Properties") is { } props)
                devices.Add(UsbmuxDevice.FromProperties(entry.GetInt("DeviceID") ?? -1, props));
        }
        return devices;
    }

    /// <summary>The host's SystemBUID, or null when this usbmux build does not answer ReadBUID.</summary>
    public async Task<string?> ReadBuidAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(Message("ReadBUID"), ct).ConfigureAwait(false);
        return reply.GetString("BUID");
    }

    /// <summary>The pair record plist bytes, or null when usbmux has none (or does not support the call).</summary>
    public async Task<byte[]?> ReadPairRecordAsync(string udid, CancellationToken ct)
    {
        var msg = Message("ReadPairRecord");
        msg["PairRecordID"] = udid;
        var reply = await RequestAsync(msg, ct).ConfigureAwait(false);
        return reply.GetData("PairRecordData");
    }

    /// <summary>Stores a pair record with usbmux. Returns false when usbmux refuses.</summary>
    public async Task<bool> SavePairRecordAsync(string udid, int deviceId, byte[] record, CancellationToken ct)
    {
        var msg = Message("SavePairRecord");
        msg["PairRecordID"] = udid;
        msg["PairRecordData"] = record;
        msg["DeviceID"] = (long)deviceId;
        var reply = await RequestAsync(msg, ct).ConfigureAwait(false);
        return reply.GetLong("Number") == 0;
    }

    /// <summary>Subscribes to attach/detach events; read them with <see cref="ReadEventAsync"/>.</summary>
    public async Task ListenAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(Message("Listen"), ct).ConfigureAwait(false);
        ThrowIfResultError(reply, "Listen");
    }

    /// <summary>Next Attached / Detached / Paired message after <see cref="ListenAsync"/>; unknown messages are skipped.</summary>
    public async Task<UsbmuxEvent> ReadEventAsync(CancellationToken ct)
    {
        while (true)
        {
            var (_, msg) = await ReceiveAsync(ct).ConfigureAwait(false);
            var id = msg.GetInt("DeviceID");
            switch (msg.GetString("MessageType"))
            {
                case "Attached" when id is not null && msg.GetDict("Properties") is { } props:
                    return new UsbmuxEvent(UsbmuxEventKind.Attached, id.Value, UsbmuxDevice.FromProperties(id.Value, props));
                case "Detached" when id is not null:
                    return new UsbmuxEvent(UsbmuxEventKind.Detached, id.Value, null);
                case "Paired" when id is not null:
                    return new UsbmuxEvent(UsbmuxEventKind.Paired, id.Value, null);
            }
        }
    }

    /// <summary>
    /// Opens a tunnel to a TCP port on the phone. On success the socket belongs to the caller as a raw
    /// stream to the phone, and this client must not be used again.
    /// </summary>
    public async Task<Stream> ConnectToDeviceAsync(int deviceId, int port, CancellationToken ct)
    {
        var msg = Message("Connect");
        msg["DeviceID"] = (long)deviceId;
        // usbmux wants the port in network byte order inside a host-order integer.
        msg["PortNumber"] = (long)(ushort)IPAddress.HostToNetworkOrder((short)port);
        var reply = await RequestAsync(msg, ct).ConfigureAwait(false);
        var number = reply.GetLong("Number");
        switch (number)
        {
            case 0:
                _handedOver = true;
                return _stream;
            case 2:
                throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone is no longer connected (usbmux: bad device).");
            case 3:
                throw new DeviceException(DeviceErrorCodes.Protocol, $"The phone refused a connection to port {port}.");
            default:
                throw new DeviceException(DeviceErrorCodes.Protocol, $"usbmux Connect failed with result {number?.ToString() ?? "(none)"}.");
        }
    }

    public async Task<PlistDict> RequestAsync(PlistDict message, CancellationToken ct)
    {
        var tag = _nextTag++;
        await _stream.WriteAsync(Encode(message, tag), ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        while (true)
        {
            var (replyTag, reply) = await ReceiveAsync(ct).ConfigureAwait(false);
            if (replyTag == tag)
                return reply;
            // Tag 0 is an unsolicited event; anything else is a reply to an earlier request. Skip both.
        }
    }

    private async Task<(uint Tag, PlistDict Message)> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var (length, version, message, tag) = DecodeHeader(header);
        if (length < HeaderSize || length - HeaderSize > MaxPayload)
            throw new FormatException($"usbmux message length {length} is out of range.");
        var payload = new byte[length - HeaderSize];
        await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        if (version != PlistVersion || message != PlistMessage)
            throw new FormatException($"usbmux answered with protocol version {version}, message {message}; expected plist (1, 8).");
        return (tag, PlistSerializer.ParseDict(payload));
    }

    internal static byte[] Encode(PlistDict payload, uint tag)
    {
        var body = PlistSerializer.ToXml(payload);
        var frame = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0), (uint)frame.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), PlistVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), PlistMessage);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), tag);
        body.CopyTo(frame, HeaderSize);
        return frame;
    }

    internal static (uint Length, uint Version, uint Message, uint Tag) DecodeHeader(ReadOnlySpan<byte> header) => (
        BinaryPrimitives.ReadUInt32LittleEndian(header),
        BinaryPrimitives.ReadUInt32LittleEndian(header[4..]),
        BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));

    private static PlistDict Message(string type) => new()
    {
        ["MessageType"] = type,
        ["ClientVersionString"] = "DeviceDeck",
        ["ProgName"] = "DeviceDeck",
        ["kLibUSBMuxVersion"] = 3L,
    };

    private static void ThrowIfResultError(PlistDict reply, string request)
    {
        if (reply.GetString("MessageType") == "Result" && reply.GetLong("Number") is { } n && n != 0)
            throw new DeviceException(DeviceErrorCodes.Protocol, $"usbmux {request} failed with result {n}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_handedOver)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
