using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Apps;
using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

/// <summary>
/// Android phones over adb. Runs DeviceDeck's own adb server on port 5137 (never the default 5037), follows
/// host:track-devices-l, and turns each line into an <see cref="IDevice"/>: Unauthorized while the phone shows
/// "Allow USB debugging?", Connecting while getprop is read, then Ready.
/// </summary>
public sealed class AndroidDeviceProvider : IDeviceProvider
{
    private readonly AndroidProviderOptions _options;
    private readonly AppMetadataCache _cache;
    private readonly object _gate = new();
    private readonly Dictionary<string, AndroidDevice> _devices = new(StringComparer.Ordinal);
    private AdbServerHost? _host;
    private DeviceTracker? _tracker;

    public AndroidDeviceProvider(AndroidProviderOptions? options = null)
    {
        _options = options ?? new AndroidProviderOptions();
        _cache = new AppMetadataCache(_options.CacheDirectory);
    }

    public string Id => "android";

    public IReadOnlyList<IDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values.ToList<IDevice>();
            }
        }
    }

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    /// <summary>adb.exe in use; null when none was found.</summary>
    public string? AdbPath => Host.AdbPath;

    public int ServerPort => _options.ServerPort;

    internal AdbServerHost Host => _host ??= new AdbServerHost(AdbServerHost.Locate(_options.AdbPath), _options.ServerPort);

    /// <summary>
    /// Starts the adb server (if adb is present) and device tracking, and waits up to a few seconds for the first
    /// device list so <see cref="Devices"/> is filled for callers that look right away.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_tracker != null)
        {
            return;
        }
        if (Host.AdbPath == null)
        {
            return; // GetDriverStatusAsync reports Missing
        }
        _tracker = new DeviceTracker(Host, OnSnapshotAsync);
        _tracker.Start();
        await Task.WhenAny(_tracker.FirstSnapshot, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
    }

    public async Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct)
    {
        if (Host.AdbPath == null)
        {
            return new DriverStatus(Id, DriverState.Missing, null,
                _options.AdbPath != null
                    ? $"adb wasn't found at {_options.AdbPath}."
                    : "adb isn't installed; run tools/fetch-platform-tools.ps1 to download Google's platform-tools.");
        }
        string? version = await Host.GetToolsVersionAsync(ct).ConfigureAwait(false);
        if (await Host.GetServerVersionAsync(ct).ConfigureAwait(false) != null)
        {
            return new DriverStatus(Id, DriverState.Ready, version, null);
        }
        if (_tracker?.LastError is { } error)
        {
            return new DriverStatus(Id, DriverState.Error, version, $"The adb server on port {_options.ServerPort} won't start: {AdbErrors.FirstLine(error.Message)}");
        }
        return new DriverStatus(Id, DriverState.Stopped, version, $"The adb server on port {_options.ServerPort} isn't running yet.");
    }

    internal IAdbChannel CreateChannel(AdbDeviceEntry entry) =>
        new AdbChannel(Host.EndPoint, new AdbDeviceRef(entry.Serial, entry.TransportId));

    private Task OnSnapshotAsync(IReadOnlyList<AdbDeviceEntry> entries)
    {
        var events = new List<DeviceChangedEventArgs>();
        var toLoad = new List<AndroidDevice>();
        lock (_gate)
        {
            var current = entries.Where(e => e.State is not "host" and not "unknown")
                .GroupBy(e => e.Serial).Select(g => g.First()).ToDictionary(e => e.Serial, StringComparer.Ordinal);

            foreach (var (serial, device) in _devices.ToList())
            {
                // Gone, or reconnected under a new transport (the channel addresses the old one): drop it.
                if (!current.TryGetValue(serial, out var entry) || entry.TransportId != device.Entry.TransportId)
                {
                    _devices.Remove(serial);
                    device.Dispose();
                    events.Add(new DeviceChangedEventArgs(DeviceChange.Removed, device.Summary with { State = DeviceState.Disconnected, Capabilities = DeviceCapability.None }));
                }
            }

            foreach (var entry in current.Values)
            {
                if (!_devices.TryGetValue(entry.Serial, out var device))
                {
                    device = new AndroidDevice(CreateChannel(entry), entry, _cache, _options);
                    _devices[entry.Serial] = device;
                    events.Add(new DeviceChangedEventArgs(DeviceChange.Added, device.Summary));
                    if (entry.State == SummaryBuilder.OnlineState)
                    {
                        toLoad.Add(device);
                    }
                }
                else if (device.Entry.State != entry.State)
                {
                    device.Entry = entry;
                    device.SetSummary(SummaryBuilder.FromEntry(entry));
                    events.Add(new DeviceChangedEventArgs(DeviceChange.Changed, device.Summary));
                    if (entry.State == SummaryBuilder.OnlineState)
                    {
                        toLoad.Add(device);
                    }
                }
            }
        }

        foreach (var change in events)
        {
            Raise(change);
        }
        foreach (var device in toLoad)
        {
            _ = Task.Run(() => LoadAsync(device));
        }
        return Task.CompletedTask;
    }

    /// <summary>Reads getprop (retrying while adbd settles right after authorisation) and announces the phone as Ready.</summary>
    private async Task LoadAsync(AndroidDevice device)
    {
        var entry = device.Entry;
        for (int attempt = 0; attempt < 5 && !device.Lifetime.IsCancellationRequested; attempt++)
        {
            try
            {
                var profile = await device.LoadProfileAsync(device.Lifetime).ConfigureAwait(false);
                DeviceSummary summary;
                lock (_gate)
                {
                    // The phone may have gone or changed state while we read.
                    if (!_devices.TryGetValue(entry.Serial, out var stillThere) || stillThere != device || device.Entry != entry)
                    {
                        return;
                    }
                    summary = SummaryBuilder.Ready(entry, profile);
                    device.SetSummary(summary);
                }
                Raise(new DeviceChangedEventArgs(DeviceChange.Changed, summary));
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (DeviceException)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), device.Lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Raise(DeviceChangedEventArgs change)
    {
        try
        {
            DeviceChanged?.Invoke(this, change);
        }
        catch (Exception)
        {
            // A subscriber's bug must not stop device tracking.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_tracker != null)
        {
            await _tracker.DisposeAsync().ConfigureAwait(false);
            _tracker = null;
        }
        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                device.Dispose();
            }
            _devices.Clear();
        }
        await _cache.SaveAsync(CancellationToken.None).ConfigureAwait(false);
        // The adb server keeps running on its port, like adb's own; another DeviceDeck process may be using it.
    }
}
