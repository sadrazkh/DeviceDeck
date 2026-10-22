using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Protocol.Lockdown;
using DeviceDeck.Ios.Protocol.Usbmux;
using DeviceDeck.Ios.Wpd;

namespace DeviceDeck.Ios;

/// <summary>
/// iPhones through Apple Mobile Device Service's usbmux. Listens for Attached / Detached / Paired,
/// keeps one <see cref="IosDevice"/> per UDID (USB and Wi-Fi links merged) and reconnects when the
/// service restarts.
/// </summary>
public sealed class IosDeviceProvider : IDeviceProvider, IDriverControl
{
    private readonly IUsbmuxConnector _usbmux;
    private readonly IosProviderOptions _options;
    private readonly PairRecordStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, IosDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private Task _watch = Task.CompletedTask;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool? _listening;

    public IosDeviceProvider() : this(new TcpUsbmuxConnector(), new IosProviderOptions { Wpd = WpdBackends.ForThisPc() })
    {
    }

    public IosDeviceProvider(IUsbmuxConnector usbmux, IosProviderOptions options)
    {
        _usbmux = usbmux;
        _options = options;
        _store = new PairRecordStore(usbmux, options.ApplePairRecordDirectory, options.LocalPairRecordDirectory);
    }

    public string Id => "ios";

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    public IReadOnlyList<IDevice> Devices
    {
        get
        {
            lock (_gate)
                return _devices.Values.Cast<IDevice>().ToList();
        }
    }

    /// <summary>The last time listening failed, for diagnostics.</summary>
    public Exception? LastListenError { get; private set; }

    /// <summary>
    /// Starts listening. Returns once usbmux has answered and the phones already plugged in are in
    /// <see cref="Devices"/> (state Connecting until their handshake finishes), or once usbmux could not be reached.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch = Task.Run(() => WatchAsync(firstAttempt, _stop.Token), CancellationToken.None);
        // Return once the first Listen has been answered (or failed), so Devices is meaningful.
        await firstAttempt.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => AppleMobileDeviceService.GetStatusAsync(_usbmux, ct);

    /// <summary>Raised when listening to Apple Mobile Device Service starts or stops working (Drivers card, v0.6).</summary>
    public event EventHandler? DriverStatusChanged;

    /// <summary>
    /// "Start Apple Mobile Device Service": `sc start` through Windows' administrator prompt, then listening again at once
    /// instead of after <see cref="IosProviderOptions.ReconnectInterval"/>.
    /// </summary>
    public async Task StartDriverAsync(CancellationToken ct)
    {
        var starter = new AmdsStarter(_usbmux, _options.ElevatedRunner, _options.FindAmds ?? AppleMobileDeviceService.FindService, _options.AmdsStartWait);
        await starter.StartAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        RaiseDriverStatusChanged();
    }

    private void SetListening(bool listening)
    {
        bool changed;
        lock (_gate)
        {
            changed = _listening != listening;
            _listening = listening;
        }
        if (changed)
            RaiseDriverStatusChanged();
    }

    private void RaiseDriverStatusChanged()
    {
        try
        {
            DriverStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A subscriber's bug must not stop listening.
        }
    }

    /// <summary>The device with this UDID, if attached.</summary>
    public IosDevice? Find(string udid)
    {
        lock (_gate)
            return _devices.GetValueOrDefault(udid);
    }

    private async Task WatchAsync(TaskCompletionSource firstAttempt, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wake = Volatile.Read(ref _wake);
            try
            {
                await using var client = await _usbmux.ConnectAsync(ct).ConfigureAwait(false);
                await client.ListenAsync(ct).ConfigureAwait(false);
                LastListenError = null;
                // usbmux replays Attached for phones already connected, but Apple Mobile Device Service can take
                // over a second to do it. Seed the list from ListDevices (after Listen, so nothing is missed) so
                // Devices is complete when StartAsync returns; the replayed Attached then finds them tracked.
                await using (var lister = await _usbmux.ConnectAsync(ct).ConfigureAwait(false))
                {
                    foreach (var d in await lister.ListDevicesAsync(ct).ConfigureAwait(false))
                        await HandleAsync(new UsbmuxEvent(UsbmuxEventKind.Attached, d.DeviceId, d)).ConfigureAwait(false);
                }
                firstAttempt.TrySetResult();
                SetListening(true);
                while (true)
                {
                    var evt = await client.ReadEventAsync(ct).ConfigureAwait(false);
                    await HandleAsync(evt).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastListenError = ex;
                firstAttempt.TrySetResult();
                await RemoveAllAsync().ConfigureAwait(false);
                SetListening(false);
            }
            try
            {
                // Woken early by "Start Apple Mobile Device Service" on the Drivers card.
                await Task.WhenAny(Task.Delay(_options.ReconnectInterval, ct), wake.Task).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        firstAttempt.TrySetResult();
    }

    private async Task HandleAsync(UsbmuxEvent evt)
    {
        switch (evt.Kind)
        {
            case UsbmuxEventKind.Attached when evt.Device is { } d:
            {
                IosDevice? existing;
                lock (_gate)
                    existing = _devices.GetValueOrDefault(d.Udid);
                if (existing is not null)
                {
                    if (existing.HasLink(d.DeviceId))
                        break; // the Listen replay of a phone seeded from ListDevices
                    // Second link (USB + Wi-Fi) for a phone we already track.
                    existing.AddLink(d.DeviceId, d.ConnectionType);
                    existing.RequestRefresh();
                    break;
                }
                // Linked before it is listed, so the first event about it is Added.
                var device = new IosDevice(d.Udid, _usbmux, _store, _options, OnDeviceChanged);
                device.AddLink(d.DeviceId, d.ConnectionType);
                lock (_gate)
                    _devices[d.Udid] = device;
                Raise(DeviceChange.Added, device.Summary);
                device.Start();
                break;
            }
            case UsbmuxEventKind.Detached:
            {
                IosDevice? gone = null;
                lock (_gate)
                {
                    var device = _devices.Values.FirstOrDefault(x => x.HasLink(evt.DeviceId));
                    if (device is not null && device.RemoveLink(evt.DeviceId))
                    {
                        _devices.Remove(device.Udid);
                        gone = device;
                    }
                }
                if (gone is not null)
                {
                    await gone.MarkGoneAsync().ConfigureAwait(false);
                    Raise(DeviceChange.Removed, gone.Summary);
                }
                break;
            }
            case UsbmuxEventKind.Paired:
            {
                IosDevice? device;
                lock (_gate)
                    device = _devices.Values.FirstOrDefault(x => x.HasLink(evt.DeviceId));
                device?.RequestRefresh();
                break;
            }
        }
    }

    private void OnDeviceChanged(IosDevice device)
    {
        bool present;
        lock (_gate)
            present = _devices.TryGetValue(device.Udid, out var current) && ReferenceEquals(current, device);
        if (present)
            Raise(DeviceChange.Changed, device.Summary);
    }

    private async Task RemoveAllAsync()
    {
        List<IosDevice> all;
        lock (_gate)
        {
            all = [.. _devices.Values];
            _devices.Clear();
        }
        foreach (var device in all)
        {
            await device.MarkGoneAsync().ConfigureAwait(false);
            Raise(DeviceChange.Removed, device.Summary);
        }
    }

    private void Raise(DeviceChange change, DeviceSummary summary)
    {
        try
        {
            DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(change, summary));
        }
        catch (Exception)
        {
            // A subscriber's bug must not stop device tracking.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested)
            await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _watch.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        List<IosDevice> all;
        lock (_gate)
        {
            all = [.. _devices.Values];
            _devices.Clear();
        }
        foreach (var device in all)
            await device.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
