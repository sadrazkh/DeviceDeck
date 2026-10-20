using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol.Usbmux;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

public class UsbmuxTests
{
    private static CancellationToken Ct => TestContext.Timeout();

    [Fact]
    public void Header_is_little_endian_length_version_message_tag()
    {
        var frame = UsbmuxClient.Encode(new PlistDict { ["MessageType"] = "ListDevices" }, tag: 7);

        Assert.Equal((uint)frame.Length, BitConverter.ToUInt32(frame, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(frame, 4));   // plist protocol
        Assert.Equal(8u, BitConverter.ToUInt32(frame, 8));   // plist message
        Assert.Equal(7u, BitConverter.ToUInt32(frame, 12));  // tag
        var body = PlistSerializer.ParseDict(frame.AsSpan(16));
        Assert.Equal("ListDevices", body.GetString("MessageType"));

        var (length, version, message, tag) = UsbmuxClient.DecodeHeader(frame);
        Assert.Equal(((uint)frame.Length, 1u, 8u, 7u), (length, version, message, tag));
    }

    [Theory]
    [InlineData("00008110001C7DE622E9801E", "00008110-001C7DE622E9801E")]
    [InlineData("00008110-001c7de622e9801e", "00008110-001C7DE622E9801E")]
    [InlineData("595e0a2a99524881eddabbefd3a93f1b51b27365", "595e0a2a99524881eddabbefd3a93f1b51b27365")]
    public void Udids_are_normalised_to_the_pair_record_form(string serial, string expected) =>
        Assert.Equal(expected, UsbmuxDevice.NormalizeUdid(serial));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListDevices_parses_xml_and_binary_replies(bool binary)
    {
        var mux = new FakeUsbmux { ReplyBinary = binary };
        mux.Attach(5, new FakePhone("00008140-000A1B2C3D4E801E"));
        mux.Attach(9, new FakePhone("00008140-000A1B2C3D4E801E") { Network = true });

        await using var client = await mux.ConnectAsync(Ct);
        var devices = await client.ListDevicesAsync(Ct);

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal("00008140-000A1B2C3D4E801E", d.Udid));
        Assert.Equal(UsbmuxConnectionType.Usb, devices.Single(d => d.DeviceId == 5).ConnectionType);
        Assert.Equal(UsbmuxConnectionType.Network, devices.Single(d => d.DeviceId == 9).ConnectionType);
    }

    [Fact]
    public async Task ReadBuid_and_ReadPairRecord()
    {
        var mux = new FakeUsbmux();
        mux.PairRecords["00008140-000A1B2C3D4E801E"] = [1, 2, 3];

        await using (var client = await mux.ConnectAsync(Ct))
            Assert.Equal(mux.Buid, await client.ReadBuidAsync(Ct));
        await using (var client = await mux.ConnectAsync(Ct))
            Assert.Equal(new byte[] { 1, 2, 3 }, await client.ReadPairRecordAsync("00008140-000A1B2C3D4E801E", Ct));
        await using (var client = await mux.ConnectAsync(Ct))
            Assert.Null(await client.ReadPairRecordAsync("unknown", Ct));
    }

    [Fact]
    public async Task Listen_delivers_attach_detach_and_paired()
    {
        var mux = new FakeUsbmux();
        await using var client = await mux.ConnectAsync(Ct);
        await client.ListenAsync(Ct);

        mux.Attach(3, new FakePhone("00008110-001C7DE622E9801E"));
        var attached = await client.ReadEventAsync(Ct);
        Assert.Equal(UsbmuxEventKind.Attached, attached.Kind);
        Assert.Equal(3, attached.DeviceId);
        Assert.Equal("00008110-001C7DE622E9801E", attached.Device!.Udid);

        mux.Paired(3);
        Assert.Equal(UsbmuxEventKind.Paired, (await client.ReadEventAsync(Ct)).Kind);

        mux.Detach(3);
        var detached = await client.ReadEventAsync(Ct);
        Assert.Equal((UsbmuxEventKind.Detached, 3), (detached.Kind, detached.DeviceId));
    }

    [Fact]
    public async Task Connect_sends_the_port_in_network_byte_order_and_hands_over_the_stream()
    {
        var mux = new FakeUsbmux();
        mux.Attach(4, new FakePhone("00008140-000A1B2C3D4E801E"));

        await using var client = await mux.ConnectAsync(Ct);
        await using var stream = await client.ConnectToDeviceAsync(4, 62078, Ct);

        // The fake only routes 62078 to lockdown if the byte order was right; QueryType proves it.
        await Wire.WritePlistAsync(stream, new PlistDict { ["Request"] = "QueryType", ["Label"] = "t" }, Ct);
        var reply = await Wire.ReadPlistAsync(stream, Ct);
        Assert.Equal("com.apple.mobile.lockdown", reply!.GetString("Type"));
    }

    [Fact]
    public async Task Connect_to_a_gone_device_is_disconnected_and_a_closed_port_is_protocol()
    {
        var mux = new FakeUsbmux();
        mux.Attach(4, new FakePhone("00008140-000A1B2C3D4E801E"));

        await using (var client = await mux.ConnectAsync(Ct))
        {
            var gone = await Assert.ThrowsAsync<DeviceException>(() => client.ConnectToDeviceAsync(99, 62078, Ct));
            Assert.Equal(DeviceErrorCodes.Disconnected, gone.Code);
        }
        await using (var client = await mux.ConnectAsync(Ct))
        {
            var refused = await Assert.ThrowsAsync<DeviceException>(() => client.ConnectToDeviceAsync(4, 1234, Ct));
            Assert.Equal(DeviceErrorCodes.Protocol, refused.Code);
        }
    }

    [Fact]
    public async Task Unreachable_usbmux_is_driver_missing()
    {
        // Port 1 on loopback has nothing listening.
        var ex = await Assert.ThrowsAsync<DeviceException>(() =>
            UsbmuxClient.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1), Ct));
        Assert.Equal(DeviceErrorCodes.DriverMissing, ex.Code);
    }
}

internal static class TestContext
{
    /// <summary>Keeps a broken fake from hanging the run.</summary>
    public static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
