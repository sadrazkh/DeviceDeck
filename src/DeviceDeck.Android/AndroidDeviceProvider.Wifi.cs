using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Wifi;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

// Wireless debugging (Android 11+): pairing and connecting go through our adb server's host services, the same
// requests `adb pair` and `adb connect` send. The server keeps mDNS off, so QR pairing finds the phone itself.
public sealed partial class AndroidDeviceProvider : IWirelessDebugging
{
    /// <summary>Where QR pairing looks for the phone; tests point it at a fake responder.</summary>
    internal MdnsBrowser Mdns { get; set; } = new();

    /// <summary>How long QR pairing looks for the phone's connect service after pairing before giving up on connecting.</summary>
    internal TimeSpan ConnectSearch { get; set; } = TimeSpan.FromSeconds(20);

    public const string PairingService = "_adb-tls-pairing._tcp";
    public const string ConnectService = "_adb-tls-connect._tcp";

    public async Task<WirelessResult> PairAsync(string host, int port, string code, CancellationToken ct)
    {
        var address = WirelessReplies.Address(host, port);
        if (!WirelessReplies.IsPairingCode(code))
        {
            throw new ArgumentException("The pairing code is the six digits shown on the phone.");
        }

        return WirelessReplies.Pair(await HostQueryAsync($"host:pair:{code}:{address}", ct).ConfigureAwait(false));
    }

    public async Task<WirelessResult> ConnectAsync(string host, int port, CancellationToken ct) =>
        WirelessReplies.Connect(await HostQueryAsync("host:connect:" + WirelessReplies.Address(host, port), ct).ConfigureAwait(false));

    public async Task DisconnectAsync(string serial, CancellationToken ct)
    {
        if (!Parsing.TrackDevicesParser.IsWifiSerial(serial))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "That phone is connected by USB.");
        }

        await HostQueryAsync("host:disconnect:" + serial, ct).ConfigureAwait(false);
    }

    public async Task<WirelessResult> PairWithQrAsync(string serviceName, string password, IProgress<string>? stage, CancellationToken ct)
    {
        stage?.Report("waiting");
        MdnsService? pairing = null;
        while (pairing is null)
        {
            var services = await Mdns.BrowseAsync(PairingService, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            pairing = services.FirstOrDefault(s => s.Instance == serviceName);
        }

        stage?.Report("pairing");
        var paired = WirelessReplies.Pair(await HostQueryAsync(
            $"host:pair:{password}:{WirelessReplies.Address(pairing.Address.ToString(), pairing.Port)}", ct).ConfigureAwait(false));

        // The phone now announces its connect service under its guid ("adb-<serial>-<random>").
        stage?.Report("connecting");
        var guid = WirelessReplies.Guid(paired.Message);
        var deadline = DateTime.UtcNow + ConnectSearch;
        while (DateTime.UtcNow < deadline)
        {
            var services = await Mdns.BrowseAsync(ConnectService, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            var connect = services.FirstOrDefault(s => guid is not null && s.Instance == guid)
                          ?? services.FirstOrDefault(s => s.Address.Equals(pairing.Address));
            if (connect is not null)
            {
                return await ConnectAsync(connect.Address.ToString(), connect.Port, ct).ConfigureAwait(false);
            }
        }

        return new WirelessResult(null, paired.Message);
    }

    /// <summary>One host request answered with OKAY and a message, like `adb pair` / `adb connect`.</summary>
    private async Task<string> HostQueryAsync(string request, CancellationToken ct)
    {
        if (!Host.CanStart && await Host.GetServerVersionAsync(ct).ConfigureAwait(false) == null)
        {
            throw new DeviceException(DeviceErrorCodes.DriverMissing, "adb isn't installed; run tools/fetch-platform-tools.ps1.");
        }

        try
        {
            await Host.EnsureStartedAsync(ct).ConfigureAwait(false);
            await using var connection = await AdbConnection.ConnectAsync(Host.EndPoint, ct).ConfigureAwait(false);
            await connection.SendRequestAsync(request, ct).ConfigureAwait(false);
            return (await connection.ReadLengthPrefixedStringAsync(ct).ConfigureAwait(false) ?? "").Trim();
        }
        catch (AdbFailException ex)
        {
            throw WirelessReplies.Failure(ex.AdbMessage);
        }
        catch (Exception ex) when (ex is AdbServerUnavailableException or AdbConnectionLostException)
        {
            throw new DeviceException(DeviceErrorCodes.DriverMissing, "The adb server isn't answering.", ex);
        }
    }
}

/// <summary>adb's answers to pair / connect / disconnect, as results or errors.</summary>
internal static partial class WirelessReplies
{
    [GeneratedRegex(@"\[guid=(?<guid>[^\]]+)\]")]
    private static partial Regex GuidPart();

    [GeneratedRegex(@"^[A-Za-z0-9]{4,64}$")]
    private static partial Regex Code();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}$")]
    private static partial Regex HostName();

    public static bool IsPairingCode(string code) => Code().IsMatch(code);

    /// <summary>"192.168.1.5:37099", "[fe80::1]:37099" or "pixel.local:37099"; anything else is a bad request.</summary>
    public static string Address(string host, int port)
    {
        host = host.Trim();
        if (port is < 1 or > 65535)
        {
            throw new ArgumentException("The port is a number between 1 and 65535.");
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{ip}]:{port.ToString(CultureInfo.InvariantCulture)}"
                : $"{ip}:{port.ToString(CultureInfo.InvariantCulture)}";
        }

        if (!HostName().IsMatch(host))
        {
            throw new ArgumentException("That isn't an IP address.");
        }

        return $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string? Guid(string message) => GuidPart().Match(message) is { Success: true } m ? m.Groups["guid"].Value : null;

    public static WirelessResult Pair(string reply)
    {
        if (reply.StartsWith("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            return new WirelessResult(null, reply);
        }

        throw Failure(reply);
    }

    public static WirelessResult Connect(string reply)
    {
        foreach (var prefix in new[] { "already connected to ", "connected to " })
        {
            if (reply.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return new WirelessResult(reply[prefix.Length..].Trim(), reply);
            }
        }

        throw Failure(reply);
    }

    public static DeviceException Failure(string reply)
    {
        var text = reply.Trim();
        if (text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) || text.Contains("failed to authenticate", StringComparison.OrdinalIgnoreCase))
        {
            return Error(DeviceErrorCodes.Unauthorized, text.Contains("authenticate", StringComparison.OrdinalIgnoreCase)
                ? "The phone hasn't been paired with this PC yet. Pair it first."
                : "The pairing code is wrong or has expired. Get a new one on the phone.", text);
        }

        if (text.Contains("Unable to start pairing client", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed to connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable to connect", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no such device", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
        {
            return Error(DeviceErrorCodes.NotFound, "Nothing answered at that address. Check the IP address and port on the phone.", text);
        }

        return Error(DeviceErrorCodes.Protocol, "adb couldn't do that: " + AdbErrors.FirstLine(text.Replace("Failed: ", "", StringComparison.Ordinal)), text);
    }

    private static DeviceException Error(string code, string message, string detail) => new(code, message, new InvalidOperationException(detail));
}
