using System.Text.Json;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Serialization;

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

    // Drivers card (v0.6): the driver statuses last given to anyone, as JSON: a page's drivers.status answer or a
    // drivers.changed. A later check that differs is always published. (v0.5.3 compared polls only with each other, so a
    // page that asked while adb was still starting kept "Stopped" for good.)
    private string? _lastDriversJson;
    private bool _driverCheckPending;
    private bool _driverCheckRunning;
    private readonly CancellationTokenSource _driverChecks = new();
    private CancellationTokenSource? _driverPoll;
    private Task? _disposing;
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

    /// <summary>
    /// Check drivers again (and raise <see cref="DriversChanged"/> when the answer changed) after a provider started, a
    /// phone came, changed or went, or a provider said its driver changed (<see cref="IDriverControl.DriverStatusChanged"/>).
    /// </summary>
    public bool CheckDriversOnEvents { get; init; } = true;

    /// <summary>How long such a check waits, so a burst of events costs one check.</summary>
    public TimeSpan DriverCheckDelay { get; init; } = TimeSpan.FromMilliseconds(300);

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
            if (provider is IDriverControl control)
            {
                control.DriverStatusChanged += OnDriverStatusChanged;
            }
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
        finally
        {
            // A provider that has started (or failed to) has a new answer for the Drivers card.
            RequestDriverCheck();
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

    /// <summary>
    /// Every driver's status now, for drivers.status. The answer is remembered as what the page shows, so any later check
    /// that differs raises <see cref="DriversChanged"/>.
    /// </summary>
    public async Task<IReadOnlyList<DriverStatus>> GetDriverStatusAsync(CancellationToken ct)
    {
        var now = await CheckDriversAsync(ct).ConfigureAwait(false);
        RememberDrivers(now);
        return now;
    }

    /// <summary>Checks every driver now and raises <see cref="DriversChanged"/> when the answer differs from the last one given out.</summary>
    public async Task<IReadOnlyList<DriverStatus>> RefreshDriversAsync(CancellationToken ct)
    {
        var now = await CheckDriversAsync(ct).ConfigureAwait(false);
        if (RememberDrivers(now))
        {
            DriversChanged?.Invoke(this, now);
        }

        return now;
    }

    /// <summary>
    /// drivers.start: starts (or restarts) one provider's driver (<see cref="IDriverControl"/>), then checks every driver
    /// again. Throws <see cref="DeviceException"/> from the provider when it can't.
    /// </summary>
    public async Task<IReadOnlyList<DriverStatus>> StartDriverAsync(string providerId, CancellationToken ct)
    {
        var provider = _providers.FirstOrDefault(p => p.Id == providerId)
            ?? throw new ArgumentException($"There is no driver called \"{providerId}\".");
        if (provider is not IDriverControl control)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "DeviceDeck can't start this driver.");
        }

        await control.StartDriverAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            // Started by hand now: the failure from when the app started no longer says anything.
            _startFailures.RemoveAll(f => f.Provider == provider);
        }

        return await RefreshDriversAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Asks for a driver check soon; a burst of requests costs one check (<see cref="DriverCheckDelay"/>).</summary>
    public void RequestDriverCheck()
    {
        if (!CheckDriversOnEvents)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposing is not null)
            {
                return;
            }

            _driverCheckPending = true;
            if (_driverCheckRunning)
            {
                return;
            }

            _driverCheckRunning = true;
        }

        _ = Task.Run(RunDriverChecksAsync);
    }

    private async Task RunDriverChecksAsync()
    {
        var ct = _driverChecks.Token;
        while (true)
        {
            try
            {
                if (DriverCheckDelay > TimeSpan.Zero)
                {
                    await Task.Delay(DriverCheckDelay, ct).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (!_driverCheckPending || _disposing is not null)
                    {
                        _driverCheckRunning = false;
                        return;
                    }

                    _driverCheckPending = false;
                }

                await RefreshDriversAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_gate)
                {
                    _driverCheckRunning = false;
                }

                return;
            }
            catch (Exception)
            {
                // CheckDriversAsync turns a provider's failure into its row; a subscriber's bug must not stop the checks.
            }
        }
    }

    private bool RememberDrivers(IReadOnlyList<DriverStatus> now)
    {
        var json = JsonSerializer.Serialize(now, DeviceDeckJson.Options);
        lock (_gate)
        {
            var changed = _lastDriversJson is not null && !string.Equals(_lastDriversJson, json, StringComparison.Ordinal);
            _lastDriversJson = json;
            return changed;
        }
    }

    private async Task<IReadOnlyList<DriverStatus>> CheckDriversAsync(CancellationToken ct)
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
                    ? status with { State = DriverState.Error, Detail = startError.Message, Reason = DriverReasons.StartFailed }
                    : status;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DriverStatus(p.Id, DriverState.Error, null, ex.Message) { Reason = DriverReasons.CheckFailed };
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
                await RefreshDriversAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnDriverStatusChanged(object? sender, EventArgs e) => RequestDriverCheck();

    private void OnProviderDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        if (sender is IDeviceProvider provider)
        {
            Apply(provider, e.Change, e.Summary);
            // A phone came, went or changed (unauthorized to ready): the Drivers card's phone hints may have changed.
            RequestDriverCheck();
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

    /// <summary>How long one provider may take to stop (close its connections, stop its adb server) before the hub gives up on it.</summary>
    public TimeSpan ProviderStopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Raised during <see cref="DisposeAsync"/> for a provider that threw or ran past <see cref="ProviderStopTimeout"/> (a <see cref="TimeoutException"/>).</summary>
    public event EventHandler<(string ProviderId, Exception Error)>? ProviderStopFailed;

    /// <summary>
    /// Stops every provider, all at once, and returns only when each has finished its DisposeAsync,
    /// failed, or run out of time. Safe to call twice.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task stopping;
        lock (_gate)
        {
            _disposing ??= StopAllAsync();
            stopping = _disposing;
        }

        await stopping.ConfigureAwait(false);
    }

    private async Task StopAllAsync()
    {
        if (_driverPoll is not null)
        {
            await _driverPoll.CancelAsync().ConfigureAwait(false);
            _driverPoll.Dispose();
        }

        await _driverChecks.CancelAsync().ConfigureAwait(false);
        foreach (var provider in _providers)
        {
            provider.DeviceChanged -= OnProviderDeviceChanged;
            if (provider is IDriverControl control)
            {
                control.DriverStatusChanged -= OnDriverStatusChanged;
            }
        }

        await Task.WhenAll(_providers.Select(StopOneAsync)).ConfigureAwait(false);
    }

    private async Task StopOneAsync(IDeviceProvider provider)
    {
        try
        {
            // On a thread of its own, not the pool: a provider that blocks inside DisposeAsync can't hold up
            // the others, even on a 1–2 core machine where the pool has no spare thread (seen on CI runners).
            await Task.Factory.StartNew(
                    () => provider.DisposeAsync().AsTask(),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap()
                .WaitAsync(ProviderStopTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Shutting down: one provider failing to stop must not keep the others running.
            ProviderStopFailed?.Invoke(this, (provider.Id, ex));
        }
    }
}
