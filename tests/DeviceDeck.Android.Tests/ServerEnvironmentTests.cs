using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Tests.Infrastructure;

namespace DeviceDeck.Android.Tests;

/// <summary>
/// The adb server must not open network listeners (Windows Firewall asks about adb.exe when it does):
/// mDNS off unless asked for, and nothing in the environment that moves the listener off loopback.
/// </summary>
public class ServerEnvironmentTests
{
    [Fact]
    public void Mdns_is_off_by_default_and_wider_listen_settings_are_dropped()
    {
        var environment = AdbServerHost.ServerEnvironment(5137, enableMdns: false);
        Assert.Equal("5137", environment["ANDROID_ADB_SERVER_PORT"]);
        Assert.Equal("0", environment["ADB_MDNS"]);
        Assert.True(environment.ContainsKey("ADB_SERVER_SOCKET"));
        Assert.Null(environment["ADB_SERVER_SOCKET"]);
        Assert.True(environment.ContainsKey("ANDROID_ADB_SERVER_ADDRESS"));
        Assert.Null(environment["ANDROID_ADB_SERVER_ADDRESS"]);
    }

    [Fact]
    public void Mdns_can_be_turned_on_for_wireless_debugging()
    {
        Assert.Equal("1", AdbServerHost.ServerEnvironment(5137, enableMdns: true)["ADB_MDNS"]);
        Assert.False(new AndroidProviderOptions().EnableMdns);
    }

    [Fact]
    public async Task Adb_is_started_with_that_environment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // the stand-in adb is a .cmd script
        }

        // A free port nobody answers on, so the host really runs "adb start-server".
        int port;
        await using (var server = new FakeAdbServer())
        {
            port = server.Port;
        }
        string folder = Path.Combine(Path.GetTempPath(), "dd-fake-adb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string adb = Path.Combine(folder, "adb.cmd");
        File.WriteAllText(adb, """
            @echo off
            > "%~dp0env.txt" echo MDNS=[%ADB_MDNS%] PORT=[%ANDROID_ADB_SERVER_PORT%] SOCKET=[%ADB_SERVER_SOCKET%] ADDRESS=[%ANDROID_ADB_SERVER_ADDRESS%] ARGS=[%*]
            exit /b 0
            """);

        // Values a user or another tool may have set globally; they must not reach adb.
        string? oldSocket = Environment.GetEnvironmentVariable("ADB_SERVER_SOCKET");
        string? oldAddress = Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_ADDRESS");
        string? oldMdns = Environment.GetEnvironmentVariable("ADB_MDNS");
        Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", "tcp:any:5037");
        Environment.SetEnvironmentVariable("ANDROID_ADB_SERVER_ADDRESS", "0.0.0.0");
        Environment.SetEnvironmentVariable("ADB_MDNS", "1");
        try
        {
            var host = new AdbServerHost(adb, port);
            // The stand-in never opens the port, so the host reports that the server didn't come up.
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.EnsureStartedAsync(CancellationToken.None));
            Assert.False(host.StartedServer);
            string seen = File.ReadAllText(Path.Combine(folder, "env.txt")).Trim();
            Assert.Equal($"MDNS=[0] PORT=[{port}] SOCKET=[] ADDRESS=[] ARGS=[-P {port} start-server]", seen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ADB_SERVER_SOCKET", oldSocket);
            Environment.SetEnvironmentVariable("ANDROID_ADB_SERVER_ADDRESS", oldAddress);
            Environment.SetEnvironmentVariable("ADB_MDNS", oldMdns);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Mdns_state_is_read_from_the_server()
    {
        await using var server = new FakeAdbServer();
        var host = new AdbServerHost(null, server.Port);
        Assert.Equal("ERROR: mdns discovery disabled", await host.GetMdnsCheckAsync(CancellationToken.None));
        server.MdnsCheck = "mdns daemon version [Openscreen discovery 0.0.0]";
        Assert.Equal("mdns daemon version [Openscreen discovery 0.0.0]", await host.GetMdnsCheckAsync(CancellationToken.None));
        Assert.Contains("host:mdns:check", server.Requests);

        server.Running = false;
        Assert.Null(await host.GetMdnsCheckAsync(CancellationToken.None));
    }
}
