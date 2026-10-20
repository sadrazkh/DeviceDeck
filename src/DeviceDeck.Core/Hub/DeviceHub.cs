using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Hub;

/// <summary>
/// Starts every registered <see cref="IDeviceProvider"/> and merges their devices into one list keyed by
/// <see cref="DeviceSummary.Id"/>. When two providers see the same phone (say adb and MTP), the full-mode one wins.
/// </summary>
public sealed class DeviceHub : IAsyncDisposable
{
    private readonly IReadOnlyList<IDeviceProvider> _providers;
    private readonly Lock _gate = new();

    // deviceId -> the providers currently reporting it, in registration order.
    private readonly Dictionary<string, List<(IDeviceProvider Provider, DeviceSummary Summary)>> _sightings = new(StringComparer.Ordinal);

    // deviceId -> what the hub last announced, so duplicate Changed events are dropped.
    private readonly Dictionary<string, DeviceSummary> _announced = new(StringComparer.Ordinal);

    private readonly List<(IDeviceProvider Provider, Exception Error)> _startFailures = [];
    private IReadOnlyList<DriverStatus>? _lastDrivers;
    private CancellationTokenSource? _driverPoll;
    private bool _started;

    public DeviceHub(IEnumerable<IDeviceProvider> providers)
    {
        _providers = providers.ToArray();
        if (_providers.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != _providers.Count)
        {
            throw new ArgumentException("Two providers share one id.", nameof(providers));
        }
    }

    public IReadOnlyList<IDeviceProvider> Providers => _providers;

    /// <summary>Raised on the provider's thread after the merged list changed.</summary>
    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    /// <summary>Raised when a periodic driver check finds a different answer than the last one.</summary>
    public event EventHandler<IReadOnlyList<DriverStatus>>? DriversChanged;

    /// <summary>How often driver status is re-checked for <see cref="DriversChanged"/>. Zero turns polling off.</summary>
    public TimeSpan DriverPollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Starts all providers in parallel. A provider that fails to start is reported through its driver status and does not stop the others.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        foreach (var provider in _providers)
        {
            provider.DeviceChanged += OnProviderDeviceChanged;
        }

        await Task.WhenAll(_providers.Select(p => StartOneAsync(p, ct))).ConfigureAwait(false);

        if (DriverPollInterval > TimeSpan.Zero)
        {
            _driverPoll = new CancellationTokenSource();
            _ = PollDriversAsync(_driverPoll.Token);
        }
    }

    private async Task StartOneAsync(IDeviceProvider provider, CancellationToken ct)
    {
        try
        {
            await provider.StartAsync(ct).ConfigureAwait(false);
            foreach (var device in provider.Devices)
            {
                Apply(provider, DeviceChange.Added, device.Summary);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _startFailures.Add((provider, ex));
            }
        }
    }

    public IReadOnlyList<DeviceSummary> ListDevices()
    {
        lock (_gate)
        {
            return _announced.Values.OrderBy(s => s.Name, StringComparer.CurrentCulture).ToArray();
        }
    }

    public bool TryGetDevice(string deviceId, out IDevice device)
    {
        IDeviceProvider? provider;
        lock (_gate)
        {
            provider = _sightings.TryGetValue(deviceId, out var list) ? Pick(list).Provider : null;
        }

        device = provider?.Devices.FirstOrDefault(d => d.Summary.Id == deviceId)!;
        return device is not null;
    }

    /// <summary>The device, or <see cref="DeviceErrorCodes.NotFound"/>.</summary>
    public IDevice GetDevice(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || !TryGetDevice(deviceId, out var device))
        {
            throw new DeviceException(DeviceErrorCodes.NotFound, "That phone isn't connected.");
        }

        return device;
    }

    /// <summary>
    /// The device when it can take work now; otherwise the error that says why
    /// (disconnected, waiting for Trust, waiting for "Allow USB debugging").
    /// A locked iPhone passes: some services still work, the provider decides.
    /// </summary>
    public IDevice GetUsableDevice(string deviceId)
    {
        var device = GetDevice(deviceId);
        ThrowIfUnusable(device.Summary);
        return device;

        static void ThrowIfUnusable(DeviceSummary summary)
        {
            switch (summary.State)
            {
                case DeviceState.Disconnected:
                    throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone was disconnected.");
                case DeviceState.Connecting:
                    throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone is still connecting.");
                case DeviceState.AwaitingTrust:
                    throw new DeviceException(DeviceErrorCodes.TrustRequired, "Unlock the iPhone and tap Trust.");
                case DeviceState.Unauthorized:
                    throw new DeviceException(DeviceErrorCodes.Unauthorized, "Tap Allow on the phone to allow USB debugging.");
            }
        }
    }

    public async Task<IReadOnlyList<DriverStatus>> GetDriverStatusAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(_providers.Select(async p =>
        {
            Exception? startError;
            lock (_gate)
            {
                startError = _startFailures.FirstOrDefault(f => f.Provider == p).Error;
            }

            try
            {
                var status = await p.GetDriverStatusAsync(ct).ConfigureAwait(false);
                return startError is not null && status.State == DriverState.Ready
                    ? status with { State = DriverState.Error, Detail = startError.Message }
                    : status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DriverStatus(p.Id, DriverState.Error, null, ex.Message);
            }
        })).ConfigureAwait(false);

        return results;
    }

    private async Task PollDriversAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DriverPollInterval);
        try
        {
            do
            {
                var now = await GetDriverStatusAsync(ct).ConfigureAwait(false);
                var changed = _lastDrivers is not null && !_lastDrivers.SequenceEqual(now);
                _lastDrivers = now;
                if (changed)
                {
                    DriversChanged?.Invoke(this, now);
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnProviderDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        if (sender is IDeviceProvider provider)
        {
            Apply(provider, e.Change, e.Summary);
        }
    }

    private void Apply(IDeviceProvider provider, DeviceChange change, DeviceSummary summary)
    {
        DeviceChangedEventArgs? announce = null;
        lock (_gate)
        {
            if (!_sightings.TryGetValue(summary.Id, out var list))
            {
                list = [];
                _sightings[summary.Id] = list;
            }

            list.RemoveAll(s => s.Provider == provider);
            if (change != DeviceChange.Removed)
            {
                list.Add((provider, summary));
                list.Sort((a, b) => IndexOf(a.Provider).CompareTo(IndexOf(b.Provider)));
            }

            if (list.Count == 0)
            {
                _sightings.Remove(summary.Id);
                if (_announced.Remove(summary.Id, out var last))
                {
                    announce = new DeviceChangedEventArgs(DeviceChange.Removed, last with { State = DeviceState.Disconnected });
                }
            }
            else
            {
                var effective = Pick(list).Summary;
                if (!_announced.TryGetValue(summary.Id, out var last))
                {
                    announce = new DeviceChangedEventArgs(DeviceChange.Added, effective);
                }
                else if (!SameSummary(last, effective))
                {
                    announce = new DeviceChangedEventArgs(DeviceChange.Changed, effective);
                }

                _announced[summary.Id] = effective;
            }
        }

        if (announce is not null)
        {
            DeviceChanged?.Invoke(this, announce);
        }
    }

    private int IndexOf(IDeviceProvider provider)
    {
        for (var i = 0; i < _providers.Count; i++)
        {
            if (_providers[i] == provider)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static (IDeviceProvider Provider, DeviceSummary Summary) Pick(List<(IDeviceProvider Provider, DeviceSummary Summary)> list)
    {
        foreach (var sighting in list)
        {
            if (sighting.Summary.Mode == DeviceMode.Full)
            {
                return sighting;
            }
        }

        return list[0];
    }

    // Records compare by value, but Capabilities etc. are value types, so record equality is exactly what we want.
    private static bool SameSummary(DeviceSummary a, DeviceSummary b) => a == b;

    public async ValueTask DisposeAsync()
    {
        if (_driverPoll is not null)
        {
            await _driverPoll.CancelAsync().ConfigureAwait(false);
            _driverPoll.Dispose();
        }

        foreach (var provider in _providers)
        {
            provider.DeviceChanged -= OnProviderDeviceChanged;
        }

        foreach (var provider in _providers)
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down: one provider failing to stop must not keep the others running.
            }
        }
    }
}
