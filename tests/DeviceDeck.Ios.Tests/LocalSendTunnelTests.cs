using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

/// <summary>Round 3: LocalSend over the cable is a usbmux Connect to the phone's own port 53317.</summary>
public sealed class LocalSendTunnelTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly IosDeviceProvider _provider;

    public LocalSendTunnelTests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
    }

    private async Task<IosDevice> AttachAsync(FakePhone phone)
    {
        _mux.Attach(1, phone);
        await _provider.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the phone.");
            await Task.Delay(20, Ct);
        }

        return _provider.Find(Udid)!;
    }

    [Fact]
    public async Task An_apps_port_on_the_phone_is_reached_through_usbmux_and_a_closed_one_says_port_closed()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
        phone.RawPorts[53317] = async (s, ct) =>
        {
            var hello = new byte[5];
            await s.ReadExactlyAsync(hello, ct);
            Array.Reverse(hello);
            await s.WriteAsync(hello, ct);
            await s.FlushAsync(ct);
        };
        var device = await AttachAsync(phone);
        var tunnel = Assert.IsAssignableFrom<IDevicePortTunnel>(device);

        await using (var stream = await tunnel.ConnectPortAsync(53317, Ct))
        {
            await stream.WriteAsync("hello"u8.ToArray(), Ct);
            var reply = new byte[5];
            await stream.ReadExactlyAsync(reply, Ct);
            Assert.Equal("olleh", Encoding.ASCII.GetString(reply));
        }

        // LocalSend not open: usbmux answers "connection refused".
        var closed = await Assert.ThrowsAsync<DeviceException>(() => tunnel.ConnectPortAsync(53318, Ct));
        Assert.Equal(IDevicePortTunnel.PortClosedCode, closed.Code);

        _mux.Detach(1);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid) is not null)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the phone to go.");
            await Task.Delay(20, Ct);
        }

        var gone = await Assert.ThrowsAsync<DeviceException>(() => tunnel.ConnectPortAsync(53317, Ct));
        Assert.Equal(DeviceErrorCodes.Disconnected, gone.Code);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
