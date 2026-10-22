using System.ComponentModel;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Usbmux;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

/// <summary>
/// "Start Apple Mobile Device Service" on the Drivers card (v0.6): `sc start` through Windows' administrator prompt, only
/// when asked, with a fake elevation runner (no prompt, no service is touched).
/// </summary>
public sealed class AmdsStarterTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>usbmux that refuses connections until the "service" starts, like Apple Mobile Device Service stopped.</summary>
    private sealed class SwitchUsbmux : IUsbmuxConnector
    {
        private readonly FakeUsbmux _inner = new();

        public volatile bool Running;

        public Task<UsbmuxClient> ConnectAsync(CancellationToken ct) => Running
            ? _inner.ConnectAsync(ct)
            : throw new DeviceException(DeviceErrorCodes.DriverMissing, "Apple Mobile Device Service is not running.");
    }

    /// <summary>Records what would run elevated; does what the test says instead of showing UAC.</summary>
    private sealed class FakeElevation(Func<int> run) : IElevatedRunner
    {
        public List<(string File, string Arguments)> Calls { get; } = [];

        public Task<int> RunElevatedAsync(string fileName, string arguments, CancellationToken ct)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(run());
        }
    }

    private static AmdsStarter Starter(SwitchUsbmux usbmux, FakeElevation runner, bool installed = true) =>
        new(usbmux, runner, () => (installed, installed ? @"C:\Program Files\Common Files\Apple\Mobile Device Support\AppleMobileDeviceService.exe" : null),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task Start_runs_sc_start_for_the_service_elevated_and_waits_until_usbmux_answers()
    {
        var usbmux = new SwitchUsbmux();
        var runner = new FakeElevation(() =>
        {
            // Windows starts the service; usbmux answers a moment later.
            _ = Task.Delay(150).ContinueWith(_ => usbmux.Running = true, TaskScheduler.Default);
            return 0;
        });

        await Starter(usbmux, runner).StartAsync(None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"), call.File);
        Assert.Equal("start \"Apple Mobile Device Service\"", call.Arguments);
        Assert.True(usbmux.Running);
    }

    [Fact]
    public async Task A_service_that_already_answers_is_not_started_again()
    {
        var usbmux = new SwitchUsbmux { Running = true };
        var runner = new FakeElevation(() => 0);
        await Starter(usbmux, runner).StartAsync(None);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Declining_windows_prompt_is_permission_denied_in_plain_words()
    {
        var runner = new FakeElevation(() => throw new Win32Exception(1223));
        var refused = await Assert.ThrowsAsync<DeviceException>(() => Starter(new SwitchUsbmux(), runner).StartAsync(None));
        Assert.Equal(DeviceErrorCodes.PermissionDenied, refused.Code);
        Assert.Contains("permission", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_service_says_where_to_turn_it_back_on()
    {
        var runner = new FakeElevation(() => 1058);
        var refused = await Assert.ThrowsAsync<DeviceException>(() => Starter(new SwitchUsbmux(), runner).StartAsync(None));
        Assert.Contains("Automatic", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_apple_mobile_device_support_nothing_is_run()
    {
        var runner = new FakeElevation(() => 0);
        var refused = await Assert.ThrowsAsync<DeviceException>(() => Starter(new SwitchUsbmux(), runner, installed: false).StartAsync(None));
        Assert.Equal(DeviceErrorCodes.DriverMissing, refused.Code);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Already_running_counts_as_started()
    {
        var usbmux = new SwitchUsbmux();
        var runner = new FakeElevation(() =>
        {
            usbmux.Running = true;
            return 1056; // ERROR_SERVICE_ALREADY_RUNNING
        });
        await Starter(usbmux, runner).StartAsync(None);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task The_provider_starts_the_service_and_listens_again_at_once()
    {
        var usbmux = new SwitchUsbmux();
        var runner = new FakeElevation(() =>
        {
            usbmux.Running = true;
            return 0;
        });
        var options = new IosProviderOptions
        {
            ElevatedRunner = runner,
            FindAmds = () => (true, null),
            // Long enough that only the wake-up from Start can explain listening again within the test.
            ReconnectInterval = TimeSpan.FromMinutes(10),
            LocalPairRecordDirectory = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N")),
        };
        await using var provider = new IosDeviceProvider(usbmux, options);
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int changes = 0;
        provider.DriverStatusChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref changes) >= 1 && provider.LastListenError is null && usbmux.Running)
            {
                listening.TrySetResult();
            }
        };

        await provider.StartAsync(None);
        Assert.NotNull(provider.LastListenError);

        await ((IDriverControl)provider).StartDriverAsync(None);
        await listening.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(runner.Calls);
    }
}
