using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace DeviceDeck.Android.Wifi;

/// <param name="Instance">The service instance's own label ("adb-R5CT20ABCDE-a1B2c3", or the QR code's name).</param>
internal sealed record MdnsService(string Instance, IPAddress Address, int Port);

/// <summary>
/// A one-shot DNS-SD browser: sends a PTR query to 224.0.0.251:5353 from an ephemeral port on every IPv4 interface
/// ("legacy unicast", RFC 6762 §6.7), so phones answer straight to us. Nothing binds port 5353, which is what made
/// Windows Firewall ask about adb's own mDNS.
/// </summary>
internal sealed class MdnsBrowser(IPEndPoint? target = null)
{
    public static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeSrv = 33;
    private const int SioUdpConnReset = -1744830452; // 0x9800000C

    private readonly IPEndPoint _target = target ?? Group;

    /// <summary>Instances of <paramref name="serviceType"/> ("_adb-tls-pairing._tcp") heard within <paramref name="window"/>.</summary>
    public async Task<List<MdnsService>> BrowseAsync(string serviceType, TimeSpan window, CancellationToken ct)
    {
        var name = serviceType.TrimEnd('.') + ".local";
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
        {
            // An ICMP "port unreachable" would otherwise fail the next receive with ConnectionReset.
            socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }

        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        var query = BuildQuery(name, TypePtr);
        var found = new Dictionary<string, Partial>(StringComparer.OrdinalIgnoreCase);
        using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
        until.CancelAfter(window);
        var buffer = new byte[9000];
        var nextSend = DateTime.UtcNow;
        while (!until.IsCancellationRequested)
        {
            if (DateTime.UtcNow >= nextSend)
            {
                await SendAsync(socket, query, until.Token).ConfigureAwait(false);
                nextSend = DateTime.UtcNow.AddSeconds(1);
            }

            using var slice = CancellationTokenSource.CreateLinkedTokenSource(until.Token);
            slice.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), slice.Token).ConfigureAwait(false);
                Read(buffer.AsSpan(0, result.ReceivedBytes), ((IPEndPoint)result.RemoteEndPoint).Address, name, found);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }
        }

        ct.ThrowIfCancellationRequested();
        return found.Values
            .Where(p => p.Port > 0 && (p.Address ?? p.Sender) is not null)
            .Select(p => new MdnsService(p.Instance, p.Address ?? p.Sender!, p.Port))
            .ToList();
    }

    private async Task SendAsync(Socket socket, byte[] query, CancellationToken ct)
    {
        if (!_target.Address.Equals(Group.Address))
        {
            await TrySendAsync(socket, query, ct).ConfigureAwait(false);
            return;
        }

        foreach (var address in MulticastInterfaces())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
            }
            catch (SocketException)
            {
                continue;
            }

            await TrySendAsync(socket, query, ct).ConfigureAwait(false);
        }
    }

    private async Task TrySendAsync(Socket socket, byte[] query, CancellationToken ct)
    {
        try
        {
            await socket.SendToAsync(query, SocketFlags.None, _target, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            // A network that can't send multicast; the others may still answer.
        }
    }

    /// <summary>IPv4 addresses of interfaces that are up and do multicast (Wi-Fi, Ethernet), loopback and tunnels left out.</summary>
    private static IEnumerable<IPAddress> MulticastInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    // ---- DNS messages ----

    public static byte[] BuildQuery(string name, ushort type)
    {
        using var message = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1); // one question
        message.Write(header);
        WriteName(message, name);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 0x8001); // class IN, unicast response wanted
        message.Write(tail);
        return message.ToArray();
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
    }

    private sealed class Partial(string instance)
    {
        public string Instance { get; } = instance;
        public string? Host { get; set; }
        public int Port { get; set; }
        public IPAddress? Address { get; set; }
        public IPAddress? Sender { get; set; }
    }

    /// <summary>Collects PTR, SRV and A records for <paramref name="serviceName"/> from one response.</summary>
    private static void Read(ReadOnlySpan<byte> message, IPAddress sender, string serviceName, Dictionary<string, Partial> found)
    {
        try
        {
            ReadUnchecked(message, sender, serviceName, found);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Someone else's malformed packet.
        }
    }

    private static void ReadUnchecked(ReadOnlySpan<byte> message, IPAddress sender, string serviceName, Dictionary<string, Partial> found)
    {
        if (message.Length < 12 || (message[2] & 0x80) == 0)
        {
            return; // not a response
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var records = BinaryPrimitives.ReadUInt16BigEndian(message[6..]) + BinaryPrimitives.ReadUInt16BigEndian(message[8..]) + BinaryPrimitives.ReadUInt16BigEndian(message[10..]);
        var at = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadName(message, ref at);
            at += 4;
        }

        var srv = new List<(string Owner, string Host, int Port)>();
        var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < records; i++)
        {
            var owner = ReadName(message, ref at);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[at..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[(at + 8)..]);
            var data = at + 10;
            at = data + length;
            switch (type)
            {
                case TypePtr when owner.Equals(serviceName, StringComparison.OrdinalIgnoreCase):
                {
                    var p = data;
                    var instance = ReadName(message, ref p);
                    var label = FirstLabel(message, data);
                    if (!found.ContainsKey(instance))
                    {
                        found[instance] = new Partial(label) { Sender = sender };
                    }

                    break;
                }
                case TypeSrv:
                {
                    var p = data + 6;
                    srv.Add((owner, ReadName(message, ref p), BinaryPrimitives.ReadUInt16BigEndian(message[(data + 4)..])));
                    break;
                }
                case TypeA when length == 4:
                    addresses[owner] = new IPAddress(message.Slice(data, 4));
                    break;
            }
        }

        foreach (var (owner, host, port) in srv)
        {
            if (!found.TryGetValue(owner, out var partial))
            {
                // An SRV without the PTR in the same packet still counts when it is for this service type.
                if (!owner.EndsWith("." + serviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                partial = found[owner] = new Partial(owner[..^(serviceName.Length + 1)]) { Sender = sender };
            }

            partial.Host = host;
            partial.Port = port;
        }

        foreach (var partial in found.Values)
        {
            if (partial.Host is not null && addresses.TryGetValue(partial.Host, out var address))
            {
                partial.Address = address;
            }
        }
    }

    /// <summary>The first label of a name, as it is (an instance label may contain dots and spaces).</summary>
    private static string FirstLabel(ReadOnlySpan<byte> message, int at)
    {
        for (var hops = 0; hops < 32; hops++)
        {
            var length = message[at];
            if ((length & 0xC0) == 0xC0)
            {
                at = ((length & 0x3F) << 8) | message[at + 1];
                continue;
            }

            return Encoding.UTF8.GetString(message.Slice(at + 1, length));
        }

        throw new FormatException("Name pointer loop.");
    }

    private static string ReadName(ReadOnlySpan<byte> message, ref int at)
    {
        var labels = new List<string>();
        var position = at;
        var jumped = false;
        for (var hops = 0; hops < 64; hops++)
        {
            var length = message[position];
            if (length == 0)
            {
                if (!jumped)
                {
                    at = position + 1;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (!jumped)
                {
                    at = position + 2;
                }

                jumped = true;
                position = ((length & 0x3F) << 8) | message[position + 1];
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(message.Slice(position + 1, length)));
            position += 1 + length;
        }

        throw new FormatException("Name too long or looping.");
    }
}
