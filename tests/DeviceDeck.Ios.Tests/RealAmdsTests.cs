using System.Net.Sockets;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Usbmux;
using Xunit.Abstractions;

namespace DeviceDeck.Ios.Tests;

/// <summary>
/// Read-only checks against the real Apple Mobile Device Service on this PC: they talk to usbmux only,
/// never to a phone. Skipped where nothing listens on 127.0.0.1:27015.
/// </summary>
public class RealAmdsTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Timeout();

    [RealAmdsFact]
    public async Task ListDevices_answers()
    {
        await using var client = await UsbmuxClient.ConnectAsync(null, Ct);
        var devices = await client.ListDevicesAsync(Ct);

        output.WriteLine($"usbmux lists {devices.Count} device(s)");
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Udid)));
    }

    [RealAmdsFact]
    public async Task ReadBuid_answers()
    {
        await using var client = await UsbmuxClient.ConnectAsync(null, Ct);
        var buid = await client.ReadBuidAsync(Ct);
        output.WriteLine($"SystemBUID: {buid ?? "(not answered)"}");
    }

    [RealAmdsFact]
    public async Task Driver_status_is_ready_with_a_version()
    {
        var status = await AppleMobileDeviceService.GetStatusAsync(new TcpUsbmuxConnector(), Ct);
        output.WriteLine($"{status.State} {status.Version} {status.Detail}");
        Assert.Equal(DriverState.Ready, status.State);
    }

    [RealAmdsFact]
    public async Task Provider_starts_listening_and_stops_cleanly()
    {
        int expected;
        await using (var client = await UsbmuxClient.ConnectAsync(null, Ct))
            expected = (await client.ListDevicesAsync(Ct)).Select(d => d.Udid).Distinct().Count();

        await using var provider = new IosDeviceProvider(new TcpUsbmuxConnector(), new IosProviderOptions
        {
            // Do not send Pair to a phone that happens to be plugged in while the tests run.
            AutoPair = false,
            CacheDirectory = Path.Combine(Path.GetTempPath(), "devicedeck-test-cache"),
        });
        await provider.StartAsync(Ct);

        Assert.Null(provider.LastListenError);
        Assert.Equal(expected, provider.Devices.Count);
    }
}

/// <summary>A fact that runs only when Apple Mobile Device Service is listening.</summary>
public sealed class RealAmdsFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Listening = new(() =>
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var connect = socket.ConnectAsync(UsbmuxClient.DefaultEndpoint());
            return connect.Wait(TimeSpan.FromSeconds(1)) && socket.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    });

    public RealAmdsFactAttribute()
    {
        if (!Listening.Value)
            Skip = "Apple Mobile Device Service is not listening on " + UsbmuxClient.DefaultEndpoint();
    }
}
