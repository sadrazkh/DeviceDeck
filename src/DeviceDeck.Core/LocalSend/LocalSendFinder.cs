using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.LocalSend;

public sealed class LocalSendFinderOptions
{
    /// <summary>LocalSend's port, on the phone (USB) and on the network.</summary>
    public int Port { get; init; } = LocalSendProtocol.DefaultPort;

    /// <summary>Announce on 224.0.0.167 and listen for the answers. Off in tests.</summary>
    public bool Multicast { get; init; } = true;

    /// <summary>The hosts the HTTP sweep asks with /register; null = every host of this PC's private /24 networks.</summary>
    public Func<IReadOnlyList<IPAddress>>? SweepAddresses { get; init; }

    /// <summary>How long one Wi-Fi search runs (multicast answers and the sweep together).</summary>
    public TimeSpan SearchTime { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>A receiver not seen again for this long drops out of the list.</summary>
    public TimeSpan Remember { get; init; } = TimeSpan.FromMinutes(2);

    public int SweepParallelism { get; init; } = 64;

    public LocalSendTimeouts ProbeTimeouts { get; init; } = new() { Connect = TimeSpan.FromSeconds(4), Short = TimeSpan.FromSeconds(4) };

    public LocalSendTimeouts SweepTimeouts { get; init; } = new() { Connect = TimeSpan.FromMilliseconds(800), Short = TimeSpan.FromMilliseconds(1500) };
}

/// <summary>
/// Finds LocalSend for a phone. Over the cable: usbmux Connect to the phone's port 53317, then /info over HTTPS (HTTP
/// when LocalSend's encryption is off); the certificate's fingerprint must be the one /info states. Over Wi-Fi: a
/// multicast announcement on 224.0.0.167:53317 (receivers that can't reach our missing server answer by multicast too),
/// and, since multicast is often blocked, POST /register to every host of the PC's private /24 networks.
/// </summary>
public sealed class LocalSendFinder : IAsyncDisposable
{
    private readonly LocalSendIdentity _me;
    private readonly LocalSendFinderOptions _options;
    private readonly ConcurrentDictionary<string, (LocalSendReceiver Receiver, DateTimeOffset Seen)> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _gate = new();
    private Task _search = Task.CompletedTask;
    private DateTimeOffset? _searchedAt;

    public LocalSendFinder(LocalSendIdentity me, LocalSendFinderOptions? options = null)
    {
        _me = me;
        _options = options ?? new LocalSendFinderOptions();
    }

    /// <summary>Is LocalSend answering on the phone, through its cable?</summary>
    public async Task<(LocalSendUsbState State, string? Detail, LocalSendReceiver? Receiver)> ProbeUsbAsync(IDevice device, CancellationToken ct)
    {
        if (device is not IDevicePortTunnel tunnel)
        {
            return (LocalSendUsbState.Unavailable, $"{device.Summary.Name} can't be reached over its cable.", null);
        }

        var clock = Stopwatch.StartNew();
        var first = await ProbeAsync(tunnel, https: true, ct).ConfigureAwait(false);
        if (first.State is LocalSendUsbState.Found or LocalSendUsbState.NotListening or LocalSendUsbState.Unavailable)
        {
            return first;
        }

        // Something answered but not with TLS, quickly: LocalSend with encryption off speaks plain HTTP.
        if (clock.Elapsed < _options.ProbeTimeouts.Connect / 2)
        {
            var plain = await ProbeAsync(tunnel, https: false, ct).ConfigureAwait(false);
            if (plain.State == LocalSendUsbState.Found)
            {
                return plain;
            }
        }

        return first;
    }

    private async Task<(LocalSendUsbState State, string? Detail, LocalSendReceiver? Receiver)> ProbeAsync(IDevicePortTunnel tunnel, bool https, CancellationToken ct)
    {
        var port = _options.Port;
        using var client = new LocalSendClient(
            new LocalSendEndpoint("127.0.0.1", port, https, token => new ValueTask<Stream>(tunnel.ConnectPortAsync(port, token))),
            pinnedFingerprint: null, _options.ProbeTimeouts);
        try
        {
            LocalSendInfo info;
            try
            {
                info = await client.GetInfoAsync(ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code == LocalSendErrors.Failed)
            {
                // /info is kept "for debugging" by the spec; /register is the call that must stay.
                info = await client.RegisterAsync(_me.Info(), ct).ConfigureAwait(false);
            }

            var fingerprint = https ? client.PeerFingerprint : info.Fingerprint;
            if (https && !LocalSendProtocol.SameFingerprint(fingerprint, info.Fingerprint))
            {
                return (LocalSendUsbState.NotAnswering, "The certificate on port 53317 isn't the one LocalSend states.", null);
            }

            var receiver = new LocalSendReceiver("usb:" + fingerprint, LocalSendRoute.Usb, info.Alias, info.DeviceModel, info.DeviceType,
                null, port, https ? "https" : "http", fingerprint!, Likely: true);
            return (LocalSendUsbState.Found, null, receiver);
        }
        catch (DeviceException ex) when (ex.Code == LocalSendErrors.NotListening && ex.InnerException is DeviceException { Code: IDevicePortTunnel.PortClosedCode })
        {
            return (LocalSendUsbState.NotListening, "Nothing listens on the iPhone's port 53317: LocalSend isn't open.", null);
        }
        catch (DeviceException ex) when (ex.Code.StartsWith("localsend_", StringComparison.Ordinal))
        {
            return (LocalSendUsbState.NotAnswering, ex.Message, null);
        }
        catch (DeviceException ex) when (!ct.IsCancellationRequested)
        {
            return (LocalSendUsbState.Unavailable, ex.Message, null);
        }
    }

    /// <summary>Starts a Wi-Fi search unless one is running; read the results with <see cref="WifiSnapshot"/>.</summary>
    public void StartWifiSearch()
    {
        lock (_gate)
        {
            if (!_search.IsCompleted || _life.IsCancellationRequested)
            {
                return;
            }

            _search = Task.Run(() => SearchAsync(_life.Token));
        }
    }

    public bool WifiSearching
    {
        get
        {
            lock (_gate)
            {
                return !_search.IsCompleted;
            }
        }
    }

    /// <summary>Receivers seen lately, the ones that look like <paramref name="phone"/> first.</summary>
    public (IReadOnlyList<LocalSendReceiver> Receivers, bool Searching, DateTimeOffset? SearchedAt) WifiSnapshot(DeviceSummary phone)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in _seen.Where(s => now - s.Value.Seen > _options.Remember).Select(s => s.Key).ToList())
        {
            _seen.TryRemove(stale, out _);
        }

        var all = _seen.Values.Select(v => v.Receiver).ToList();
        var sameKind = all.Where(r => LooksLikePlatform(r, phone)).ToList();
        var list = all
            .Select(r => r with { Likely = string.Equals(r.Alias, phone.Name, StringComparison.OrdinalIgnoreCase) || (sameKind.Count == 1 && sameKind[0].Id == r.Id) })
            .OrderByDescending(r => r.Likely)
            .ThenByDescending(r => LooksLikePlatform(r, phone))
            .ThenBy(r => r.Alias, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (list, WifiSearching, _searchedAt);
    }

    private static bool LooksLikePlatform(LocalSendReceiver r, DeviceSummary phone)
    {
        var model = r.DeviceModel ?? "";
        var iphone = model.Contains("iPhone", StringComparison.OrdinalIgnoreCase);
        return phone.Platform == DevicePlatform.Ios
            ? iphone
            : string.Equals(r.DeviceType, "mobile", StringComparison.OrdinalIgnoreCase) && !iphone && !model.Contains("iPad", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SearchAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_options.SearchTime);
        try
        {
            await Task.WhenAll(
                _options.Multicast ? MulticastAsync(limit.Token) : Task.CompletedTask,
                SweepAsync(limit.Token)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A search that broke has simply found what it found.
        }
        finally
        {
            _searchedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <param name="port">The port it answered on (the sweep), or null to take the one it states (multicast).</param>
    private void Saw(LocalSendInfo info, IPAddress address, int? port, bool https, string fingerprint)
    {
        if (string.Equals(info.Fingerprint, _me.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var receiver = new LocalSendReceiver("wifi:" + fingerprint, LocalSendRoute.Wifi, info.Alias, info.DeviceModel, info.DeviceType,
            address.ToString(), port ?? (info.Port is > 0 and <= 65535 ? info.Port.Value : _options.Port), https ? "https" : "http", fingerprint, Likely: false);
        _seen[receiver.Id] = (receiver, DateTimeOffset.UtcNow);
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var targets = _options.SweepAddresses?.Invoke() ?? LocalNetworkHosts();
        try
        {
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = _options.SweepParallelism, CancellationToken = ct },
                async (address, token) =>
                {
                    var clock = Stopwatch.StartNew();
                    if (!await TryRegisterAsync(address, https: true, token).ConfigureAwait(false) && clock.Elapsed < _options.SweepTimeouts.Connect)
                    {
                        // Refused or not TLS, at once: try plain HTTP (LocalSend with encryption off).
                        await TryRegisterAsync(address, https: false, token).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> TryRegisterAsync(IPAddress address, bool https, CancellationToken ct)
    {
        try
        {
            using var client = new LocalSendClient(new LocalSendEndpoint(address.ToString(), _options.Port, https), pinnedFingerprint: null, _options.SweepTimeouts);
            var info = await client.RegisterAsync(_me.Info(), ct).ConfigureAwait(false);
            var fingerprint = https ? client.PeerFingerprint : info.Fingerprint;
            if (https && !LocalSendProtocol.SameFingerprint(fingerprint, info.Fingerprint))
            {
                return false;
            }

            Saw(info, address, _options.Port, https, fingerprint!);
            return true;
        }
        catch (Exception ex) when (ex is DeviceException or ArgumentException)
        {
            return false;
        }
    }

    private async Task MulticastAsync(CancellationToken ct)
    {
        var group = IPAddress.Parse(LocalSendProtocol.MulticastGroup);
        var locals = LocalAddresses().ToList();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Shared with a LocalSend running on this PC, which listens on the same port.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, _options.Port));
            foreach (var local in locals)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, local.Address));
                }
                catch (SocketException)
                {
                    // An adapter without multicast.
                }
            }

            var announcement = JsonSerializer.SerializeToUtf8Bytes(_me.Info(announce: true), LocalSendProtocol.Json);
            foreach (var local in locals)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.Address.GetAddressBytes());
                    await socket.SendToAsync(announcement, SocketFlags.None, new IPEndPoint(group, _options.Port), ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                }
            }

            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested)
            {
                var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
                if (received.RemoteEndPoint is not IPEndPoint from)
                {
                    continue;
                }

                LocalSendInfo? info;
                try
                {
                    info = JsonSerializer.Deserialize<LocalSendInfo>(buffer.AsSpan(0, received.ReceivedBytes), LocalSendProtocol.Json);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (info is null || string.IsNullOrEmpty(info.Fingerprint) || string.IsNullOrEmpty(info.Alias))
                {
                    continue;
                }

                // Multicast carries no certificate: the fingerprint it states is pinned when the job connects.
                Saw(info, from.Address, null, !string.Equals(info.Protocol, "http", StringComparison.OrdinalIgnoreCase), info.Fingerprint);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // No multicast here (port taken, adapter refuses, firewall): the sweep still runs.
        }
    }

    private static IEnumerable<UnicastIPAddressInformation> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a.Address));

    /// <summary>The other hosts of each private /24 this PC is on, networks with a gateway (the real LAN) first.</summary>
    public static IReadOnlyList<IPAddress> LocalNetworkHosts()
    {
        var hosts = new List<IPAddress>();
        var networks = new HashSet<uint>();
        foreach (var local in LocalAddresses())
        {
            var bytes = local.Address.GetAddressBytes();
            var network = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8);
            if (!networks.Add(network) || networks.Count > 4)
            {
                continue;
            }

            for (var host = 1; host < 255; host++)
            {
                if (host != bytes[3])
                {
                    hosts.Add(new IPAddress([bytes[0], bytes[1], bytes[2], (byte)host]));
                }
            }
        }

        return hosts;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public async ValueTask DisposeAsync()
    {
        Task search;
        lock (_gate)
        {
            search = _search;
        }

        await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            await search.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _life.Dispose();
    }
}
