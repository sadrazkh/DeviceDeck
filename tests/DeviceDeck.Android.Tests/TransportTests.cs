using System.Collections.Concurrent;
using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

/// <summary>The real adb wire code and the library's sync service against <see cref="FakeAdbServer"/>.</summary>
public class TransportTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Server_version_and_no_server()
    {
        await using var server = new FakeAdbServer();
        Assert.Equal(41, await new AdbServerHost(null, server.Port).GetServerVersionAsync(None));

        int unused = server.Port;
        await server.DisposeAsync();
        Assert.Null(await new AdbServerHost(null, unused).GetServerVersionAsync(None));
    }

    [Fact]
    public async Task Shell_v2_separates_stdout_stderr_and_exit_code()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        string big = string.Concat(Enumerable.Range(0, 20000).Select(i => $"line {i} عکس\n"));
        server.Shell = command => command switch
        {
            "big" => FakeShellReply.Text(big),
            "fail" => FakeShellReply.Text("partial", 2, "boom: No such file or directory"),
            _ => FakeShellReply.Text("", 127, "not found"),
        };
        var channel = new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3));

        var result = await channel.ShellAsync("big", None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(big, result.Output);

        var failed = await channel.ShellAsync("fail", None);
        Assert.Equal(2, failed.ExitCode);
        Assert.Equal("partial", failed.Output);
        Assert.Equal("boom: No such file or directory", failed.Error);

        Assert.Contains("host:transport-id:3", server.Requests);
        Assert.Contains("shell,v2,raw:big", server.Requests);
    }

    [Fact]
    public async Task Binary_stdout_is_untouched()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x0D, 0x0A, 0x00, 0xFF];
        server.Shell = _ => new FakeShellReply(png, "", 0);
        var actions = new AndroidDeviceActions(new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3)));
        Assert.Equal(png, await actions.TakeScreenshotAsync(None));
    }

    [Fact]
    public async Task Unauthorized_and_missing_devices_map_to_device_codes()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "unauthorized", 3));
        var unauthorized = await Assert.ThrowsAsync<DeviceException>(() => new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3)).ShellAsync("id", None));
        Assert.Equal(DeviceErrorCodes.Unauthorized, unauthorized.Code);

        var missing = await Assert.ThrowsAsync<DeviceException>(() => new AdbChannel(server.EndPoint, new AdbDeviceRef("GONE", null)).ShellAsync("id", None));
        Assert.Equal(DeviceErrorCodes.NotFound, missing.Code);
        Assert.Contains("host:transport:GONE", server.Requests);
    }

    [Fact]
    public async Task No_server_is_driver_missing()
    {
        int port;
        await using (var server = new FakeAdbServer())
        {
            port = server.Port;
        }
        var channel = new AdbChannel(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), new AdbDeviceRef("x", 1));
        var ex = await Assert.ThrowsAsync<DeviceException>(() => channel.ShellAsync("id", None));
        Assert.Equal(DeviceErrorCodes.DriverMissing, ex.Code);
    }

    [Fact]
    public async Task Exec_streams_stdin_and_returns_the_answer()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        byte[]? received = null;
        server.Exec = (command, input) =>
        {
            received = input;
            return "Success\n";
        };
        var channel = new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3));
        byte[] apk = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        long progress = 0;
        string output = await channel.ExecAsync($"cmd package install -r -S {apk.Length}", new MemoryStream(apk), new SyncProgress(v => progress = v), None);
        Assert.Equal("Success\n", output);
        Assert.Equal(apk, received);
        Assert.Equal(apk.Length, progress);
    }

    [Fact]
    public async Task Sync_push_and_pull_through_the_library_with_unicode_paths()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        var channel = new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3));
        byte[] content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
        const string path = "/sdcard/DCIM/عکس های سفر/photo 1.jpg";

        await using (var sync = await channel.OpenSyncAsync(None))
        {
            long pushed = 0;
            await sync.PushAsync(new MemoryStream(content), path, DateTimeOffset.UtcNow, new SyncProgress(v => pushed = v), None);
            Assert.Equal(content.Length, pushed);
            Assert.Equal(content, server.Files[path]);

            var copy = new MemoryStream();
            long pulled = 0;
            await sync.PullAsync(path, copy, new SyncProgress(v => pulled = v), None);
            Assert.Equal(content, copy.ToArray());
            Assert.Equal(content.Length, pulled);

            var missing = await Assert.ThrowsAsync<DeviceException>(() => sync.PullAsync("/sdcard/nope.txt", new MemoryStream(), null, None));
            Assert.Equal(DeviceErrorCodes.PathNotFound, missing.Code);
        }
    }

    [Fact]
    public async Task Reboot_targets()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R5CT", "device", 3));
        var actions = new AndroidDeviceActions(new AdbChannel(server.EndPoint, new AdbDeviceRef("R5CT", 3)));
        await actions.RebootAsync(RebootTarget.Bootloader, None);
        await actions.RebootAsync(RebootTarget.System, None);
        Assert.Contains("reboot:bootloader", server.Requests);
        Assert.Contains("reboot:", server.Requests);
    }

    [Fact]
    public async Task Provider_follows_a_phone_from_unauthorized_to_ready_to_unplugged()
    {
        await using var server = new FakeAdbServer();
        var fixture = FakeAdbChannel.FromFixture(FixtureSet.Synthetic("android-12-xiaomi-2201117tg"));
        server.Shell = command =>
        {
            var reply = fixture.Answer(command);
            return new FakeShellReply(reply.Stdout, reply.Stderr, reply.ExitCode);
        };
        await server.SetDevicesAsync(new FakeDevice("a1b2c3d4", "unauthorized", 1));

        var options = new AndroidProviderOptions { AdbPath = Path.Combine(Path.GetTempPath(), "no-such-adb.exe"), ServerPort = server.Port, CacheDirectory = null };
        await using var provider = new AndroidDeviceProvider(options);
        var events = new BlockingCollection<DeviceChangedEventArgs>();
        provider.DeviceChanged += (_, e) => events.Add(e);

        await provider.StartAsync(None);
        var added = Next(events);
        Assert.Equal(DeviceChange.Added, added.Change);
        Assert.Equal(DeviceState.Unauthorized, added.Summary.State);
        Assert.Equal("a1b2c3d4", added.Summary.Id);
        Assert.Equal(DeviceState.Unauthorized, Assert.Single(provider.Devices).Summary.State);

        // The user taps Allow: adb reports "device", the provider reads getprop and announces Ready.
        await server.SetDevicesAsync(new FakeDevice("a1b2c3d4", "device", 1, "2201117TG", "spes_global"));
        var connecting = Next(events);
        Assert.Equal((DeviceChange.Changed, DeviceState.Connecting), (connecting.Change, connecting.Summary.State));
        var ready = Next(events);
        Assert.Equal(DeviceChange.Changed, ready.Change);
        Assert.Equal(DeviceState.Ready, ready.Summary.State);
        Assert.Equal("Redmi Note 11", ready.Summary.Name);
        Assert.Equal("2201117TG", ready.Summary.Model);
        Assert.Equal("12", ready.Summary.OsVersion);
        Assert.Equal(ConnectionKind.Usb, ready.Summary.Connection);

        var device = Assert.Single(provider.Devices);
        var battery = await device.GetBatteryAsync(None);
        Assert.Equal(78, battery.LevelPercent);

        var status = await provider.GetDriverStatusAsync(None);
        Assert.Equal(DriverState.Ready, status.State);

        await server.SetDevicesAsync();
        var removed = Next(events);
        Assert.Equal(DeviceChange.Removed, removed.Change);
        Assert.Equal(DeviceState.Disconnected, removed.Summary.State);
        Assert.Empty(provider.Devices);
    }

    [Fact]
    public async Task Provider_without_adb_or_server_reports_missing()
    {
        int port;
        await using (var server = new FakeAdbServer())
        {
            port = server.Port;
        }
        var options = new AndroidProviderOptions { AdbPath = Path.Combine(Path.GetTempPath(), "no-such-adb.exe"), ServerPort = port, CacheDirectory = null };
        await using var provider = new AndroidDeviceProvider(options);
        await provider.StartAsync(None);
        var status = await provider.GetDriverStatusAsync(None);
        Assert.Equal(DriverState.Missing, status.State);
        Assert.Contains("no-such-adb.exe", status.Detail, StringComparison.Ordinal);
        Assert.Empty(provider.Devices);
    }

    [Fact]
    public void Tools_version_from_adb_version_output()
    {
        Assert.Equal("37.0.1", AdbServerHost.ParseToolsVersion("Android Debug Bridge version 1.0.41\nVersion 37.0.1-15733141\nInstalled as C:\\adb.exe\n"));
        Assert.Null(AdbServerHost.ParseToolsVersion("garbage"));
    }

    [Fact]
    public void Default_port_is_ours()
    {
        Assert.Equal(5137, new AndroidProviderOptions().ServerPort);
        Assert.NotEqual(5037, AndroidProviderOptions.DefaultServerPort);
    }

    private static DeviceChangedEventArgs Next(BlockingCollection<DeviceChangedEventArgs> events)
    {
        Assert.True(events.TryTake(out var change, TimeSpan.FromSeconds(10)), "timed out waiting for a device event");
        return change!;
    }

    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
