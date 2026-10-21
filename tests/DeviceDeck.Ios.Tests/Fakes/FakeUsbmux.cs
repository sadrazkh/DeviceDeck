using System.Threading.Channels;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace DeviceDeck.Ios.Tests.Fakes;

/// <summary>usbmuxd in memory: ListDevices, ReadBUID, ReadPairRecord, Listen and Connect into <see cref="FakePhone"/>s.</summary>
internal sealed class FakeUsbmux : IUsbmuxConnector
{
    private readonly List<Channel<PlistDict>> _listeners = [];

    public Dictionary<int, FakePhone> Phones { get; } = [];

    public Dictionary<string, byte[]> PairRecords { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Buid { get; set; } = "A1B2C3D4-0000-4000-8000-00000000BEEF";

    public List<string> Requests { get; } = [];

    public int Connections { get; private set; }

    public bool ReplyBinary { get; set; }

    /// <summary>How long Listen waits before replaying Attached for phones already there (AMDS can take over a second).</summary>
    public TimeSpan ListenReplayDelay { get; set; }

    /// <summary>PhotoBench: how long opening a connection to usbmuxd takes.</summary>
    public TimeSpan ConnectDelay { get; set; }

    /// <summary>PhotoBench: makes each connection's two ends (default: an in-memory pipe, no latency).</summary>
    public Func<(Stream Client, Stream Server)>? CreatePair { get; set; }

    public async Task<UsbmuxClient> ConnectAsync(CancellationToken ct)
    {
        lock (_listeners)
            Connections++;
        if (ConnectDelay > TimeSpan.Zero)
            await Task.Delay(ConnectDelay, ct);
        var (client, server) = (CreatePair ?? DuplexStream.CreatePair)();
        _ = Task.Run(() => ServeAsync(server, CancellationToken.None), CancellationToken.None);
        return new UsbmuxClient(client);
    }

    public FakePhone Attach(int deviceId, FakePhone phone)
    {
        Phones[deviceId] = phone;
        Broadcast(new PlistDict { ["MessageType"] = "Attached", ["DeviceID"] = (long)deviceId, ["Properties"] = Properties(deviceId, phone) });
        return phone;
    }

    public void Detach(int deviceId)
    {
        Phones.Remove(deviceId);
        Broadcast(new PlistDict { ["MessageType"] = "Detached", ["DeviceID"] = (long)deviceId });
    }

    public void Paired(int deviceId) =>
        Broadcast(new PlistDict { ["MessageType"] = "Paired", ["DeviceID"] = (long)deviceId });

    /// <summary>Ends every Listen connection, as when the service restarts.</summary>
    public void DropListeners()
    {
        lock (_listeners)
        {
            foreach (var l in _listeners)
                l.Writer.TryComplete();
            _listeners.Clear();
        }
    }

    private void Broadcast(PlistDict message)
    {
        lock (_listeners)
        {
            foreach (var l in _listeners)
                l.Writer.TryWrite(message);
        }
    }

    private static PlistDict Properties(int deviceId, FakePhone phone) => new()
    {
        ["ConnectionType"] = phone.Network ? "Network" : "USB",
        ["DeviceID"] = (long)deviceId,
        ["LocationID"] = 0x14200000L,
        ["ProductID"] = 0x12A8L,
        // usbmux may report new-style UDIDs without the dash.
        ["SerialNumber"] = phone.Udid.Replace("-", "", StringComparison.Ordinal),
    };

    private async Task ServeAsync(Stream s, CancellationToken ct)
    {
        try
        {
            while (await Wire.ReadUsbmuxAsync(s, ct) is { } request)
            {
                var (tag, msg) = request;
                var type = msg.GetString("MessageType") ?? "";
                lock (Requests)
                    Requests.Add(type);
                switch (type)
                {
                    case "ListDevices":
                        var list = Phones.Select(p => (object)new PlistDict
                        {
                            ["DeviceID"] = (long)p.Key,
                            ["MessageType"] = "Attached",
                            ["Properties"] = Properties(p.Key, p.Value),
                        }).ToList();
                        await Wire.WriteUsbmuxAsync(s, tag, new PlistDict { ["DeviceList"] = list }, ct, ReplyBinary);
                        break;
                    case "ReadBUID":
                        await Wire.WriteUsbmuxAsync(s, tag, new PlistDict { ["BUID"] = Buid }, ct, ReplyBinary);
                        break;
                    case "ReadPairRecord":
                        var id = msg.GetString("PairRecordID") ?? "";
                        await Wire.WriteUsbmuxAsync(s, tag, PairRecords.TryGetValue(id, out var rec)
                            ? new PlistDict { ["PairRecordData"] = rec }
                            : Result(2), ct, ReplyBinary);
                        break;
                    case "SavePairRecord":
                        PairRecords[msg.GetString("PairRecordID") ?? ""] = msg.GetData("PairRecordData") ?? [];
                        await Wire.WriteUsbmuxAsync(s, tag, Result(0), ct, ReplyBinary);
                        break;
                    case "Listen":
                        var channel = Channel.CreateUnbounded<PlistDict>();
                        lock (_listeners)
                            _listeners.Add(channel);
                        await Wire.WriteUsbmuxAsync(s, tag, Result(0), ct, ReplyBinary);
                        if (ListenReplayDelay > TimeSpan.Zero)
                            await Task.Delay(ListenReplayDelay, ct);
                        foreach (var p in Phones)
                            channel.Writer.TryWrite(new PlistDict { ["MessageType"] = "Attached", ["DeviceID"] = (long)p.Key, ["Properties"] = Properties(p.Key, p.Value) });
                        await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                            await Wire.WriteUsbmuxAsync(s, 0, evt, ct, ReplyBinary);
                        return;
                    case "Connect":
                        var deviceId = (int)(msg.GetLong("DeviceID") ?? -1);
                        var raw = (ushort)(msg.GetLong("PortNumber") ?? 0);
                        var port = (ushort)((raw >> 8) | (raw << 8));
                        if (!Phones.TryGetValue(deviceId, out var phone))
                        {
                            await Wire.WriteUsbmuxAsync(s, tag, Result(2), ct);
                            break;
                        }
                        if (!phone.HasPort(port))
                        {
                            await Wire.WriteUsbmuxAsync(s, tag, Result(3), ct);
                            break;
                        }
                        await Wire.WriteUsbmuxAsync(s, tag, Result(0), ct);
                        await phone.ServePortAsync(port, s, ct);
                        return;
                    default:
                        await Wire.WriteUsbmuxAsync(s, tag, Result(1), ct);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            await s.DisposeAsync();
        }
    }

    private static PlistDict Result(long number) => new() { ["MessageType"] = "Result", ["Number"] = number };
}
