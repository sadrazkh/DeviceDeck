using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

/// <summary>
/// Simulated phones, so the whole app runs with nothing plugged in. Also the test double for the hub, the bridge
/// and the transfer queue: <see cref="Disconnect"/>, <see cref="Reconnect"/> and <see cref="SetState"/> play a pulled
/// cable, the same phone returning and a Trust prompt.
/// </summary>
public sealed class FakeDeviceProvider : IDeviceProvider
{
    public const string ProviderId = "fake";

    private readonly FakeOptions _options;
    private readonly List<FakeDevice> _all = [];
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _gate = new();

    public FakeDeviceProvider(FakeOptions? options = null)
    {
        _options = options ?? new FakeOptions();
    }

    public string Id => ProviderId;

    public IReadOnlyList<IDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _all.Where(d => d.Present).ToArray();
            }
        }
    }

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_all.Count > 0)
            {
                return Task.CompletedTask;
            }

            foreach (var spec in _options.Devices ?? FakeSampleData.Default())
            {
                _all.Add(new FakeDevice(this, spec, _options));
            }
        }

        foreach (var device in _all)
        {
            if (_options.ArrivalDelay <= TimeSpan.Zero)
            {
                device.Present = true;
                Raise(DeviceChange.Added, device.Summary);
                _ = SimulateIndexingAsync(device);
            }
            else
            {
                device.Present = true;
                var ready = device.Summary;
                device.SetSummary(ready with { State = DeviceState.Connecting });
                Raise(DeviceChange.Added, device.Summary);
                _ = ArriveAsync(device, ready.State);
            }
        }

        return Task.CompletedTask;
    }

    public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) =>
        Task.FromResult(new DriverStatus(ProviderId, DriverState.Ready, "sample data", null));

    public FakeDevice? Find(string deviceId)
    {
        lock (_gate)
        {
            return _all.FirstOrDefault(d => d.Summary.Id == deviceId);
        }
    }

    /// <summary>The cable is pulled: the phone leaves the list and every call on it fails with device_disconnected.</summary>
    public void Disconnect(string deviceId)
    {
        var device = Find(deviceId) ?? throw new ArgumentException($"No fake device '{deviceId}'.", nameof(deviceId));
        if (!device.Present)
        {
            return;
        }

        device.Present = false;
        Raise(DeviceChange.Removed, device.Summary with { State = DeviceState.Disconnected });
    }

    /// <summary>The same phone (same id) is plugged back in, ready.</summary>
    public void Reconnect(string deviceId)
    {
        var device = Find(deviceId) ?? throw new ArgumentException($"No fake device '{deviceId}'.", nameof(deviceId));
        if (device.Present)
        {
            return;
        }

        device.SetSummary(device.Summary with { State = DeviceState.Ready });
        device.Present = true;
        Raise(DeviceChange.Added, device.Summary);
    }

    /// <summary>Changes the phone's state in place (Locked, AwaitingTrust, Unauthorized, Ready).</summary>
    public void SetState(string deviceId, DeviceState state)
    {
        var device = Find(deviceId) ?? throw new ArgumentException($"No fake device '{deviceId}'.", nameof(deviceId));
        device.SetSummary(device.Summary with { State = state });
        if (device.Present)
        {
            Raise(DeviceChange.Changed, device.Summary);
        }
    }

    internal void SimulateAbsence(FakeDevice device, TimeSpan away)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Let the reply to reboot/shutdown reach the page before the phone vanishes.
                await Task.Delay(150, _life.Token).ConfigureAwait(false);
                Disconnect(device.Summary.Id);
                await Task.Delay(away, _life.Token).ConfigureAwait(false);
                Reconnect(device.Summary.Id);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task ArriveAsync(FakeDevice device, DeviceState finalState)
    {
        try
        {
            await Task.Delay(_options.ArrivalDelay, _life.Token).ConfigureAwait(false);
            device.SetSummary(device.Summary with { State = finalState });
            Raise(DeviceChange.Changed, device.Summary);
            await SimulateIndexingAsync(device).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SimulateIndexingAsync(FakeDevice device)
    {
        if (device.Photos is not FakePhotoLibrary library)
        {
            return;
        }

        var total = library.Count;
        if (_options.IndexDuration <= TimeSpan.Zero)
        {
            library.RaiseIndexProgress(total, total);
            return;
        }

        const int steps = 12;
        try
        {
            for (var i = 0; i <= steps; i++)
            {
                library.RaiseIndexProgress(total * i / steps, total);
                await Task.Delay(_options.IndexDuration / steps, _life.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Raise(DeviceChange change, DeviceSummary summary) =>
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(change, summary));

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync().ConfigureAwait(false);
        _life.Dispose();
    }
}
