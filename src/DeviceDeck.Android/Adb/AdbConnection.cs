using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DeviceDeck.Android.Adb;

/// <summary>The adb server answered FAIL; <see cref="AdbMessage"/> is its text ("device unauthorized.", "device offline", ...).</summary>
internal sealed class AdbFailException(string adbMessage) : Exception(adbMessage)
{
    public string AdbMessage { get; } = adbMessage;
}

/// <summary>Nothing listens on the adb server port.</summary>
internal sealed class AdbServerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An open connection dropped: the phone went away or the adb server stopped mid-request.</summary>
internal sealed class AdbConnectionLostException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One connection to the adb server using its smart-socket protocol: a request is "%04x" + text, the answer is
/// OKAY or FAIL + "%04x" + message. After a transport is selected the socket carries the device service's bytes.
/// </summary>
internal sealed class AdbConnection : IAsyncDisposable, IDisposable
{
    private AdbConnection(Socket socket)
    {
        Stream = new NetworkStream(socket, ownsSocket: true);
    }

    public NetworkStream Stream { get; }

    public static async Task<AdbConnection> ConnectAsync(EndPoint endPoint, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new AdbServerUnavailableException($"Can't reach the adb server at {endPoint}: {ex.SocketErrorCode}.", ex);
        }
        return new AdbConnection(socket);
    }

    /// <summary>Sends one request and reads OKAY; throws <see cref="AdbFailException"/> on FAIL.</summary>
    public async Task SendRequestAsync(string request, CancellationToken ct)
    {
        byte[] payload = Encoding.UTF8.GetBytes(request);
        if (payload.Length > 0xFFFF)
        {
            throw new ArgumentException("An adb request can't be longer than 65535 bytes.", nameof(request));
        }
        byte[] message = new byte[4 + payload.Length];
        Encoding.ASCII.GetBytes(payload.Length.ToString("x4"), message);
        payload.CopyTo(message, 4);
        try
        {
            await Stream.WriteAsync(message, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new AdbConnectionLostException("The adb server closed the connection.", ex);
        }
        await ReadStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Switches this connection to one device: by transport id when known (stable while connected), else by serial.</summary>
    public Task SelectDeviceAsync(AdbDeviceRef device, CancellationToken ct) =>
        SendRequestAsync(device.TransportId is { } id ? $"host:transport-id:{id}" : $"host:transport:{device.Serial}", ct);

    /// <summary>Reads a "%04x"-prefixed string (host:version, host:track-devices-l); null on a clean end of stream.</summary>
    public async Task<string?> ReadLengthPrefixedStringAsync(CancellationToken ct)
    {
        byte[] header = new byte[4];
        if (!await ReadExactOrEndAsync(header, ct).ConfigureAwait(false))
        {
            return null;
        }
        int length = ParseHexLength(header);
        byte[] body = new byte[length];
        if (length > 0 && !await ReadExactOrEndAsync(body, ct).ConfigureAwait(false))
        {
            throw new AdbConnectionLostException("The adb server closed the connection mid-message.");
        }
        return Encoding.UTF8.GetString(body);
    }

    /// <summary>Fills <paramref name="buffer"/>; false when the stream ended before the first byte.</summary>
    public async Task<bool> ReadExactOrEndAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read;
            try
            {
                read = await Stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new AdbConnectionLostException("The connection to the adb server dropped.", ex);
            }
            if (read == 0)
            {
                if (total == 0)
                {
                    return false;
                }
                throw new AdbConnectionLostException("The connection to the adb server dropped mid-packet.");
            }
            total += read;
        }
        return true;
    }

    private async Task ReadStatusAsync(CancellationToken ct)
    {
        byte[] status = new byte[4];
        if (!await ReadExactOrEndAsync(status, ct).ConfigureAwait(false))
        {
            throw new AdbConnectionLostException("The adb server closed the connection without answering.");
        }
        string text = Encoding.ASCII.GetString(status);
        if (text == "OKAY")
        {
            return;
        }
        if (text == "FAIL")
        {
            string message = await ReadLengthPrefixedStringAsync(ct).ConfigureAwait(false) ?? "";
            throw new AdbFailException(message.Trim());
        }
        throw new AdbFailException($"unexpected adb response '{text}'");
    }

    internal static int ParseHexLength(ReadOnlySpan<byte> header)
    {
        if (!int.TryParse(Encoding.ASCII.GetString(header), System.Globalization.NumberStyles.HexNumber, null, out int length))
        {
            throw new AdbFailException($"bad adb length prefix '{Encoding.ASCII.GetString(header)}'");
        }
        return length;
    }

    internal static uint ReadUInt32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    public void Dispose() => Stream.Dispose();

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>How a device is addressed on the adb server.</summary>
internal sealed record AdbDeviceRef(string Serial, long? TransportId);
