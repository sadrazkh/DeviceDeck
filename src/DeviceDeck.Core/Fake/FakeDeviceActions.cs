using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

internal sealed class FakeDeviceActions(FakeDevice device) : IDeviceActions
{
    private int _shots;

    public Task RebootAsync(RebootTarget target, CancellationToken ct)
    {
        device.EnsureConnected();
        var (capability, what) = target switch
        {
            RebootTarget.Recovery => (DeviceCapability.RebootToRecovery, "restart into recovery"),
            RebootTarget.Bootloader => (DeviceCapability.RebootToBootloader, "restart into the bootloader"),
            _ => (DeviceCapability.Reboot, "restart"),
        };
        device.Require(capability, what);
        device.Provider.SimulateAbsence(device, device.Options.RebootDelay);
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken ct)
    {
        device.EnsureConnected();
        device.Require(DeviceCapability.Shutdown, "shut down");
        device.Provider.SimulateAbsence(device, device.Options.RebootDelay * 2);
        return Task.CompletedTask;
    }

    public Task<byte[]> TakeScreenshotAsync(CancellationToken ct)
    {
        device.EnsureConnected();
        device.Require(DeviceCapability.Screenshot, "take screenshots");
        var (w, h) = device.Spec.Screen;
        var seed = $"{device.Summary.Id}/{Interlocked.Increment(ref _shots)}";
        return Task.Run(() => FakeArt.Screenshot(seed, w, h), ct);
    }
}
