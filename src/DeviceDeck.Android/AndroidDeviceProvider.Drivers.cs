using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.PlatformTools;
using DeviceDeck.Android.Usb;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

// The Drivers card (v0.6): the adb row's status (never "Stopped" while DeviceDeck is still starting its server), Start /
// Restart, downloading Google's platform-tools when adb.exe is missing, and the phones Windows sees that adb can't use.
public sealed partial class AndroidDeviceProvider : IDriverControl, IDriverDownload
{
    private readonly object _downloadGate = new();
    private readonly Dictionary<string, DateTimeOffset> _adbNodesSince = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AdbDeviceEntry> _lastEntries = [];
    private DriverDownloadStatus? _download;
    private CancellationTokenSource? _downloadStop;
    private Task _downloadRun = Task.CompletedTask;
    private long _lastProgressTicks;

    public event EventHandler? DriverStatusChanged;

    public event EventHandler<DriverDownloadStatus>? DownloadChanged;

    /// <summary>Where adb.exe is looked for now: configured, bundled, downloaded, then DEVICEDECK_ADB_PATH.</summary>
    internal AdbLocation LocateAdb() => AdbServerHost.Locate(
        _options.AdbPath,
        _options.BaseDirectory ?? AppContext.BaseDirectory,
        _options.ToolsDirectory,
        _options.EnvironmentAdbPath is { } environment ? environment() : Environment.GetEnvironmentVariable("DEVICEDECK_ADB_PATH"));

    private AdbServerHost CreateHost()
    {
        var found = LocateAdb();
        return new AdbServerHost(found.Path, _options.ServerPort, _options.ServerStarter, _options.EnableMdns, found.Source, _options.PathServerStarter);
    }

    /// <summary>
    /// The host to use: when the current one has no adb.exe and none of its own server starter, and adb.exe is there now
    /// (downloaded, or put next to the app by hand), a new host with it replaces the old one (nothing ran on the old one).
    /// </summary>
    private AdbServerHost UseToolsIfFound()
    {
        var host = Host;
        if (host.CanStart || _options.AdbPath != null)
        {
            return host;
        }
        var found = LocateAdb();
        if (found.Path == null)
        {
            return host;
        }
        lock (_gate)
        {
            if (_tracker == null && ReferenceEquals(_host, host))
            {
                _host = new AdbServerHost(found.Path, _options.ServerPort, _options.ServerStarter, _options.EnableMdns, found.Source, _options.PathServerStarter);
            }
            return _host!;
        }
    }

    public async Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct)
    {
        var host = UseToolsIfFound();
        DeviceTracker? tracker = _tracker;
        if (tracker == null && host.CanStart && !_disposed && StartRequested)
        {
            // adb.exe turned up after the provider started without it: start it now; the card shows Starting meanwhile.
            _ = Task.Run(() => EnsureTrackingAsync(CancellationToken.None));
        }

        int? protocol = await host.GetServerVersionAsync(ct).ConfigureAwait(false);
        string? version = await host.GetToolsVersionCachedAsync(ct).ConfigureAwait(false);
        var phones = PhoneHints(protocol != null);
        tracker = _tracker;
        DriverStatus status;
        if (protocol != null)
        {
            status = new DriverStatus(Id, DriverState.Ready, version ?? $"adb protocol {protocol}", null);
        }
        else if (!host.CanStart)
        {
            status = new DriverStatus(Id, DriverState.Missing, null,
                _options.AdbPath != null
                    ? $"adb wasn't found at {_options.AdbPath}."
                    : "Android tools (Google's platform-tools) aren't on this PC; download them from the Drivers card.")
            {
                Reason = DriverReasons.AdbMissing,
            };
        }
        else if (tracker == null)
        {
            status = new DriverStatus(Id, StartRequested ? DriverState.Starting : DriverState.Idle, version,
                StartRequested ? $"Starting the adb server on port {_options.ServerPort}." : "DeviceDeck starts its adb server by itself.")
            {
                Reason = StartRequested ? DriverReasons.AdbStarting : DriverReasons.AdbNotStarted,
            };
        }
        else if (tracker.LastError is { } error)
        {
            status = new DriverStatus(Id, DriverState.Error, version,
                $"The adb server on port {_options.ServerPort} won't start: {AdbErrors.FirstLine(error.Message)}")
            {
                Reason = DriverReasons.AdbFailed,
            };
        }
        else
        {
            // The tracker is starting the server (or starting it again after it went away): never "Stopped" (v0.5.3).
            status = new DriverStatus(Id, DriverState.Starting, version, $"Starting the adb server on port {_options.ServerPort}.")
            {
                Reason = DriverReasons.AdbStarting,
            };
        }

        return status with
        {
            Source = host.AdbPath != null ? host.Source : null,
            Phones = phones.Count > 0 ? phones : null,
        };
    }

    // Set once StartAsync (or Start on the card) ran: before that the server is "not started" (grey), after it "starting".
    private bool StartRequested => Volatile.Read(ref _startCalls) > 0;
    private int _startCalls;

    /// <summary>Start or Restart on the card: starts tracking (and the server); when it failed, tries again now and says why if it still can't.</summary>
    public async Task StartDriverAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _startCalls);
        var host = UseToolsIfFound();
        if (!host.CanStart && await host.GetServerVersionAsync(ct).ConfigureAwait(false) == null)
        {
            throw new DeviceException(DeviceErrorCodes.DriverMissing, "Android tools aren't on this PC; download them first.");
        }

        if (_tracker is { } tracker)
        {
            try
            {
                await host.EnsureStartedAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RaiseDriverStatusChanged();
                throw new DeviceException(DeviceErrorCodes.DriverMissing,
                    $"The adb server on port {_options.ServerPort} won't start: {AdbErrors.FirstLine(ex.Message)}", ex);
            }

            tracker.RetryNow();
            await WaitForTrackingAsync(tracker, ct).ConfigureAwait(false);
        }
        else
        {
            await EnsureTrackingAsync(ct).ConfigureAwait(false);
            if (_tracker?.LastError is { } error)
            {
                throw new DeviceException(DeviceErrorCodes.DriverMissing,
                    $"The adb server on port {_options.ServerPort} won't start: {AdbErrors.FirstLine(error.Message)}", error);
            }
        }

        RaiseDriverStatusChanged();
    }

    private static async Task WaitForTrackingAsync(DeviceTracker tracker, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (!tracker.IsTracking)
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The server answers; tracking catches up by itself and the card follows.
        }
    }

    private void RaiseDriverStatusChanged()
    {
        try
        {
            DriverStatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A subscriber's bug must not stop the tracker.
        }
    }

    // ---------- Phones Windows (or adb) sees that can't be used yet ----------

    private List<DriverPhoneHint> PhoneHints(bool serverUp)
    {
        var hints = new List<DriverPhoneHint>();
        IReadOnlyList<AdbDeviceEntry> entries;
        lock (_gate)
        {
            entries = _lastEntries;
        }

        foreach (var entry in entries)
        {
            string name = entry.Model?.Replace('_', ' ') ?? "Android phone";
            string? problem = entry.State switch
            {
                "unauthorized" or "authorizing" => DriverPhoneProblems.Unauthorized,
                "offline" => DriverPhoneProblems.Offline,
                _ => null,
            };
            if (problem != null)
            {
                hints.Add(new DriverPhoneHint(name, problem));
            }
        }

        IReadOnlyList<UsbNode> nodes;
        try
        {
            nodes = (_options.UsbScan ?? WindowsUsbScan.Scan)();
        }
        catch (Exception)
        {
            return hints; // Windows' device list is a hint only
        }

        var now = DateTimeOffset.UtcNow;
        var usbSerials = entries.Where(e => !TrackDevicesParser.IsWifiSerial(e.Serial)).Select(e => e.Serial).ToList();
        lock (_adbNodesSince)
        {
            var present = UsbPhoneClassifier.AdbDeviceKeys(nodes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _adbNodesSince.Keys.Where(k => !present.Contains(k)).ToList())
            {
                _adbNodesSince.Remove(gone);
            }
            foreach (var key in present)
            {
                _adbNodesSince.TryAdd(key, now);
            }

            hints.AddRange(UsbPhoneClassifier.Classify(nodes, usbSerials, serverUp,
                key => _adbNodesSince.TryGetValue(key, out var since) && now - since >= _options.NotSeenByAdbAfter));
        }

        return hints;
    }

    // ---------- Downloading Google's platform-tools ----------

    public DriverDownloadStatus DownloadStatus
    {
        get
        {
            lock (_downloadGate)
            {
                return _download ?? new DriverDownloadStatus(Id, DriverDownloadState.Idle);
            }
        }
    }

    public DriverDownloadStatus StartDownload()
    {
        DriverDownloadStatus status;
        lock (_downloadGate)
        {
            if (_download is { State: DriverDownloadState.Downloading or DriverDownloadState.Verifying or DriverDownloadState.Installing } running)
            {
                return running;
            }
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AndroidDeviceProvider));
            }
            if (_options.ToolsDirectory is null)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, "This copy of DeviceDeck has nowhere to put downloaded tools.");
            }

            _downloadStop = new CancellationTokenSource();
            status = _download = new DriverDownloadStatus(Id, DriverDownloadState.Downloading);
            var stop = _downloadStop.Token;
            _downloadRun = Task.Run(() => RunDownloadAsync(stop));
        }

        DownloadChanged?.Invoke(this, status);
        return status;
    }

    public void CancelDownload()
    {
        lock (_downloadGate)
        {
            _downloadStop?.Cancel();
        }
    }

    private async Task StopDownloadAsync()
    {
        Task run;
        lock (_downloadGate)
        {
            _downloadStop?.Cancel();
            run = _downloadRun;
        }

        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Exiting: a download that won't stop in time is abandoned; its temp files go with the next one.
        }
    }

    private async Task RunDownloadAsync(CancellationToken ct)
    {
        var installer = new PlatformToolsInstaller(
            _options.ToolsDirectory!,
            _options.PlatformToolsSource,
            _options.SignerOf ?? AuthenticodeSigner.OrganizationOf,
            _options.ToolsVersionOf ?? PlatformToolsInstaller.ReadVersionAsync);
        try
        {
            string version = await installer.InstallAsync(
                (received, total) => Progress(received, total),
                stage => Update(s => s with { State = stage }),
                ct).ConfigureAwait(false);

            Update(s => s with { State = DriverDownloadState.Done, Version = version, Error = null, ErrorCode = null });
            RaiseDriverStatusChanged();
            // Installed: the private server starts from the downloaded tools now (bundled ones would still come first).
            Interlocked.Increment(ref _startCalls);
            await EnsureTrackingAsync(CancellationToken.None).ConfigureAwait(false);
            RaiseDriverStatusChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Update(s => s with { State = DriverDownloadState.Cancelled, Error = null, ErrorCode = null });
        }
        catch (PlatformToolsException ex)
        {
            Update(s => s with { State = DriverDownloadState.Failed, Error = ex.Message, ErrorCode = ex.Code });
        }
        catch (Exception ex)
        {
            Update(s => s with { State = DriverDownloadState.Failed, Error = ex.Message, ErrorCode = DriverDownloadErrors.Network });
        }
    }

    /// <summary>Download progress, at most every 100 ms (and always the first and last byte).</summary>
    private void Progress(long received, long? total)
    {
        long now = Environment.TickCount64;
        bool last = total is { } t && received >= t;
        if (received != 0 && !last && now - Interlocked.Read(ref _lastProgressTicks) < 100)
        {
            lock (_downloadGate)
            {
                // Kept current for DownloadStatus, without an event.
                if (_download is { State: DriverDownloadState.Downloading } d)
                {
                    _download = d with { ReceivedBytes = received, TotalBytes = total };
                }
            }
            return;
        }

        Interlocked.Exchange(ref _lastProgressTicks, now);
        Update(s => s with { ReceivedBytes = received, TotalBytes = total });
    }

    private void Update(Func<DriverDownloadStatus, DriverDownloadStatus> change)
    {
        DriverDownloadStatus status;
        lock (_downloadGate)
        {
            status = _download = change(_download ?? new DriverDownloadStatus(Id, DriverDownloadState.Downloading));
        }

        try
        {
            DownloadChanged?.Invoke(this, status);
        }
        catch (Exception)
        {
            // A subscriber's bug must not stop the download.
        }
    }
}
