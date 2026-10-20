using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;

namespace DeviceDeck.Core.Battery;

public sealed class BatteryRecorderOptions
{
    /// <summary>How long a phone stays Ready before it is read (an iPhone finishes its handshake, an Android loads its profile).</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A reconnect sooner than this after the last snapshot adds nothing (a wobbly cable, a reboot).</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long one reading may take.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Records a battery snapshot each time a phone connects (becomes Ready), at most once per <see cref="BatteryRecorderOptions.MinInterval"/>.
/// Owns the <see cref="BatteryHistoryStore"/> it writes to.
/// </summary>
public sealed class BatteryRecorder : IAsyncDisposable
{
    private readonly DeviceHub _hub;
    private readonly BatteryRecorderOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _life = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DeviceState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _pending = [];
    private readonly SemaphoreSlim _writes = new(1, 1);
    private bool _disposed;

    public BatteryRecorder(DeviceHub hub, BatteryHistoryStore store, BatteryRecorderOptions? options = null, TimeProvider? time = null)
    {
        _hub = hub;
        Store = store;
        _options = options ?? new BatteryRecorderOptions();
        _time = time ?? TimeProvider.System;
        _hub.DeviceChanged += OnDeviceChanged;
        foreach (var device in _hub.ListDevices())
        {
            Observe(device);
        }
    }

    public BatteryHistoryStore Store { get; }

    /// <summary>Raised on a pool thread after a snapshot was stored.</summary>
    public event EventHandler<BatterySnapshot>? Recorded;

    /// <summary>Raised when an automatic reading failed (logged by the host; the next connect tries again).</summary>
    public event EventHandler<(string DeviceId, Exception Error)>? RecordFailed;

    /// <summary>Reads the phone now and stores the snapshot, whatever the interval ("Record now" in the page).</summary>
    public async Task<BatterySnapshot> RecordNowAsync(string deviceId, CancellationToken ct)
    {
        var device = _hub.GetUsableDevice(deviceId);
        if (!device.Summary.Capabilities.HasFlag(DeviceCapability.Battery))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't report its battery.");
        }

        return await ReadAndStoreAsync(device, force: true, ct).ConfigureAwait(false)
            ?? throw new DeviceException(DeviceErrorCodes.Protocol, "The phone answered without any battery figures.");
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        if (e.Change == DeviceChange.Removed)
        {
            lock (_gate)
            {
                _states.Remove(e.Summary.Id);
            }

            return;
        }

        Observe(e.Summary);
    }

    private void Observe(DeviceSummary summary)
    {
        bool connected;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var before = _states.GetValueOrDefault(summary.Id, DeviceState.Disconnected);
            _states[summary.Id] = summary.State;
            connected = summary.State == DeviceState.Ready && before != DeviceState.Ready;
        }

        if (connected && summary.Capabilities.HasFlag(DeviceCapability.Battery))
        {
            Track(RecordOnConnectAsync(summary.Id));
        }
    }

    private async Task RecordOnConnectAsync(string deviceId)
    {
        var ct = _life.Token;
        try
        {
            await Task.Delay(_options.SettleDelay, _time, ct).ConfigureAwait(false);
            if (!_hub.TryGetDevice(deviceId, out var device) || device.Summary.State != DeviceState.Ready)
            {
                return; // gone or locked again; the next Ready tries again
            }

            await ReadAndStoreAsync(device, force: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RecordFailed?.Invoke(this, (deviceId, ex));
        }
    }

    private async Task<BatterySnapshot?> ReadAndStoreAsync(IDevice device, bool force, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        timeout.CancelAfter(_options.ReadTimeout);
        var summary = device.Summary;
        if (!force && Store.Latest(summary.Id) is { } last && _time.GetUtcNow() - last.At < _options.MinInterval)
        {
            return null;
        }

        var info = await device.GetBatteryAsync(timeout.Token).ConfigureAwait(false);
        var snapshot = BatterySnapshot.From(summary, info, _time.GetLocalNow());
        if (snapshot.IsEmpty)
        {
            return null;
        }

        // One write at a time, so a "Record now" and a connect can't both pass the interval check.
        await _writes.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (!force && Store.Latest(summary.Id) is { } again && _time.GetUtcNow() - again.At < _options.MinInterval)
            {
                return null;
            }

            snapshot = Store.Add(snapshot);
        }
        finally
        {
            _writes.Release();
        }

        Recorded?.Invoke(this, snapshot);
        return snapshot;
    }

    private void Track(Task task)
    {
        lock (_gate)
        {
            _pending.Add(task);
        }

        _ = task.ContinueWith(t =>
        {
            lock (_gate)
            {
                _pending.Remove(t);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Stops listening, waits for readings in flight, then closes the store.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _pending];
        }

        _hub.DeviceChanged -= OnDeviceChanged;
        await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A phone that won't answer must not hold up the exit.
        }

        Store.Dispose();
        _life.Dispose();
    }
}
