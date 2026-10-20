using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Protocol.Lockdown;
using DeviceDeck.Ios.Protocol.Services;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace DeviceDeck.Ios.Device;

/// <summary>
/// One iPhone, alive from usbmux Attached to the last Detached. A background loop does the handshake
/// (public values → pair record → session → service probe) and keeps <see cref="Summary"/> current:
/// Connecting → AwaitingTrust / Locked / Ready. Every operation opens its own service connection.
/// </summary>
public sealed partial class IosDevice : IDevice, IAsyncDisposable
{
    public const string MediaRoot = "media";
    public const string AppRootPrefix = "app:";

    private readonly object _gate = new();
    private readonly Dictionary<int, UsbmuxConnectionType> _links = [];
    private readonly IUsbmuxConnector _usbmux;
    private readonly PairRecordStore _store;
    private readonly IosProviderOptions _options;
    private readonly Action<IosDevice> _onChanged;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _pairRecordLock = new(1, 1);
    private PairRecord? _pairRecord;
    private PlistDict _values = new();
    private IosPairing.Attempt? _pendingPairing;
    private bool _pairingDenied;
    private readonly SharedConnection<AfcClient> _mediaAfc;
    private Task _loop = Task.CompletedTask;
    private bool _gone;

    internal IosDevice(string udid, IUsbmuxConnector usbmux, PairRecordStore store, IosProviderOptions options, Action<IosDevice> onChanged)
    {
        Udid = udid;
        _usbmux = usbmux;
        _store = store;
        _options = options;
        _onChanged = onChanged;
        Summary = LockdownMapping.Summary(udid, _values, ConnectionKind.Usb, DeviceState.Connecting);
        _mediaAfc = new SharedConnection<AfcClient>(ct => Connector().StartAfcAsync(ct), afc => afc.IsBroken, serialize: false);
        Photos = new IosPhotoLibrary(this, Path.Combine(options.CacheDirectory, SafeFolderName(udid)));
        Apps = new IosAppManager(this);
        Actions = new IosDeviceActions(this);
    }

    public string Udid { get; }

    public DeviceSummary Summary { get; private set; }

    /// <summary>The last handshake failure, for diagnostics (IosProbe prints it).</summary>
    public Exception? LastError { get; private set; }

    public IPhotoLibrary Photos { get; }

    public IAppManager Apps { get; }

    public IDeviceActions Actions { get; }

    // ---- lifecycle (driven by the provider) ----

    internal void AddLink(int usbmuxId, UsbmuxConnectionType type)
    {
        lock (_gate)
            _links[usbmuxId] = type;
        UpdateSummary(Summary.State);
    }

    /// <summary>Returns true when no link is left (the phone is gone).</summary>
    internal bool RemoveLink(int usbmuxId)
    {
        bool empty;
        lock (_gate)
        {
            _links.Remove(usbmuxId);
            empty = _links.Count == 0;
        }
        if (!empty)
            UpdateSummary(Summary.State);
        return empty;
    }

    internal bool HasLink(int usbmuxId)
    {
        lock (_gate)
            return _links.ContainsKey(usbmuxId);
    }

    internal void Start() => _loop = Task.Run(RunAsync);

    /// <summary>Re-run the handshake now (usbmux said Paired, or an operation hit Locked / TrustRequired).</summary>
    internal void RequestRefresh()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    internal async Task MarkGoneAsync()
    {
        _gone = true;
        await DisposeAsync().ConfigureAwait(false);
        Summary = Summary with { State = DeviceState.Disconnected, Capabilities = DeviceCapability.None };
    }

    public async ValueTask DisposeAsync()
    {
        _gone = true;
        if (!_life.IsCancellationRequested)
            await _life.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await _mediaAfc.DisposeAsync().ConfigureAwait(false);
        if (Apps is IosAppManager apps)
            await apps.DisposeAsync().ConfigureAwait(false);
        _pairRecord?.Dispose();
        _pairRecord = null;
        if (Photos is IosPhotoLibrary photos)
            photos.DeleteCache();
    }

    // ---- handshake loop ----

    private async Task RunAsync()
    {
        var ct = _life.Token;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            DeviceState state;
            try
            {
                state = await RefreshOnceAsync(ct).ConfigureAwait(false);
                LastError = null;
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = IosErrors.Translate(ex);
                failures++;
                state = Summary.State == DeviceState.Ready ? DeviceState.Ready : DeviceState.Connecting;
            }

            UpdateSummary(state);
            var delay = state == DeviceState.Ready && failures == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromTicks(_options.RetryInterval.Ticks * Math.Min(1 << Math.Min(failures, 4), 10));
            try
            {
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<DeviceState> RefreshOnceAsync(CancellationToken ct)
    {
        var connector = Connector();

        await using (var lockdown = await connector.OpenLockdownAsync(ct).ConfigureAwait(false))
        {
            var publicValues = await lockdown.GetAllValuesAsync(null, ct).ConfigureAwait(false);
            if (_values.Count == 0)
                _values = publicValues;
        }

        if (await GetPairRecordAsync(ct).ConfigureAwait(false) is null)
            return await PairAsync(connector, ct).ConfigureAwait(false);

        try
        {
            return await OpenSessionAndProbeAsync(connector, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.TrustRequired)
        {
            // The phone no longer knows our HostID (the user reset trust): pair again.
            await ForgetPairRecordAsync().ConfigureAwait(false);
            return await PairAsync(connector, ct).ConfigureAwait(false);
        }
    }

    private async Task<DeviceState> OpenSessionAndProbeAsync(DeviceConnector connector, CancellationToken ct)
    {
        try
        {
            await using var lockdown = await connector.OpenSessionAsync(ct).ConfigureAwait(false);
            _values = await ReadDeviceValuesAsync(lockdown, ct).ConfigureAwait(false);
            // A phone that has not been unlocked since boot accepts the session but refuses services.
            // Connect to the started service and close it: a service started and never connected to is
            // reaped by the phone later, and that was seen taking a live AFC connection down with it.
            var probe = await lockdown.StartServiceAsync(ServiceNames.Afc, ct).ConfigureAwait(false);
            await (await connector.ConnectPortAsync(probe.Port, ct).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
            return DeviceState.Ready;
        }
        catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.Locked)
        {
            return DeviceState.Locked;
        }
    }

    /// <summary>Every top-level value, plus the colour keys that recent iOS leaves out of the full dump.</summary>
    private static async Task<PlistDict> ReadDeviceValuesAsync(LockdownClient lockdown, CancellationToken ct)
    {
        var values = await lockdown.GetAllValuesAsync(null, ct).ConfigureAwait(false);
        foreach (var key in new[] { "DeviceEnclosureColor", "DeviceColor" })
        {
            if (!values.ContainsKey(key) && await lockdown.GetValueAsync(null, key, ct).ConfigureAwait(false) is { } value)
                values[key] = value;
        }
        return values;
    }

    private async Task<DeviceState> PairAsync(DeviceConnector connector, CancellationToken ct)
    {
        if (!_options.AutoPair || _pairingDenied)
            return DeviceState.AwaitingTrust;

        var (outcome, attempt) = await IosPairing.TryPairAsync(connector, _usbmux, _store, _pendingPairing, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case IosPairing.Outcome.Paired:
                _pendingPairing = null;
                await ForgetPairRecordAsync().ConfigureAwait(false);
                return await OpenSessionAndProbeAsync(connector, ct).ConfigureAwait(false);
            case IosPairing.Outcome.Denied:
                // The phone will not ask again until it is plugged in again.
                _pendingPairing = null;
                _pairingDenied = true;
                return DeviceState.AwaitingTrust;
            case IosPairing.Outcome.Locked:
                _pendingPairing = attempt;
                return DeviceState.Locked;
            default:
                _pendingPairing = attempt;
                return DeviceState.AwaitingTrust;
        }
    }

    private async Task<PairRecord?> GetPairRecordAsync(CancellationToken ct)
    {
        await _pairRecordLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _pairRecord ??= await _store.LoadAsync(Udid, ct).ConfigureAwait(false);
        }
        finally
        {
            _pairRecordLock.Release();
        }
    }

    private async Task ForgetPairRecordAsync()
    {
        await _pairRecordLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _pairRecord?.Dispose();
            _pairRecord = null;
        }
        finally
        {
            _pairRecordLock.Release();
        }
    }

    private void UpdateSummary(DeviceState state)
    {
        ConnectionKind connection;
        lock (_gate)
            connection = _links.Count == 0 || _links.ContainsValue(UsbmuxConnectionType.Usb) ? ConnectionKind.Usb : ConnectionKind.Wifi;
        var next = LockdownMapping.Summary(Udid, _values, connection, state);
        if (next == Summary || _gone)
            return;
        Summary = next;
        _onChanged(this);
    }

    // ---- operations ----

    /// <summary>A connector on the preferred link (USB over Wi-Fi).</summary>
    internal DeviceConnector Connector()
    {
        int id;
        lock (_gate)
        {
            if (_links.Count == 0)
                throw new DeviceException(DeviceErrorCodes.Disconnected, "The iPhone is not connected.");
            id = _links.OrderBy(l => l.Value == UsbmuxConnectionType.Usb ? 0 : 1).ThenBy(l => l.Key).First().Key;
        }
        return new DeviceConnector(_usbmux, id, Udid, GetPairRecordAsync);
    }

    /// <summary>Runs an operation with translated errors; Locked / TrustRequired also re-check the phone's state.</summary>
    internal async Task<T> UseAsync<T>(Func<DeviceConnector, Task<T>> operation)
    {
        if (_gone)
            throw new DeviceException(DeviceErrorCodes.Disconnected, "The iPhone was disconnected.");
        if (Summary.State == DeviceState.AwaitingTrust)
            throw new DeviceException(DeviceErrorCodes.TrustRequired, "Tap Trust on the iPhone to let this PC in.");
        try
        {
            return await IosErrors.Guard(() => operation(Connector())).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.Locked)
        {
            UpdateSummary(DeviceState.Locked);
            RequestRefresh();
            throw;
        }
        catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.TrustRequired)
        {
            RequestRefresh();
            throw;
        }
    }

    /// <summary>Runs <paramref name="operation"/> on the device's shared Media AFC connection (photos, thumbnails).</summary>
    internal Task<T> UseMediaAfcAsync<T>(Func<AfcClient, Task<T>> operation, CancellationToken ct) =>
        UseAsync(_ => _mediaAfc.UseAsync(operation, ct));

    internal Task UseAsync(Func<DeviceConnector, Task> operation) =>
        UseAsync<bool>(async c =>
        {
            await operation(c).ConfigureAwait(false);
            return true;
        });

    /// <summary>
    /// Capacity, free space and categories in under a second (0.8 s measured on an iPhone 16, iOS 27): the
    /// disk-usage domain, a few keys asked for by name, and, in parallel, a stat of the camera roll's videos for
    /// the photos/videos split. The ".factory" domain is not read: the phone spends ~1.5 s computing it.
    /// </summary>
    public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => UseAsync(async c =>
    {
        var videos = ((IosPhotoLibrary)Photos).GetVideoBytesAsync(ct);
        PlistDict usage;
        await using (var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false))
            usage = await ReadDiskUsageAsync(lockdown, ct).ConfigureAwait(false);

        long? videoBytes = null;
        try
        {
            videoBytes = await videos.ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.Protocol or DeviceErrorCodes.PathNotFound)
        {
            // Without it, videos stay inside "photos".
        }
        return LockdownMapping.Storage(usage, videoBytes);
    });

    /// <summary>Cheap keys iOS 27 leaves out of the domain's full dump but answers when asked for by name.</summary>
    private static readonly string[] DiskUsageKeysByName = ["MobileApplicationUsage", "MediaCacheUsage", "VoicemailUsage", "WebAppCacheUsage"];

    private static async Task<PlistDict> ReadDiskUsageAsync(LockdownClient lockdown, CancellationToken ct)
    {
        const string domain = "com.apple.disk_usage";
        var usage = await lockdown.GetAllValuesAsync(domain, ct).ConfigureAwait(false);
        foreach (var key in DiskUsageKeysByName)
        {
            if (!usage.ContainsKey(key) && await lockdown.GetValueAsync(domain, key, ct).ConfigureAwait(false) is { } value)
                usage[key] = value;
        }
        // The camera roll's size lives in the ".factory" domain; the phone computes it on request (~0.7 s).
        // PhotoUsage carries the same figure on iOS 27, so it is not asked for separately.
        if (!usage.ContainsKey("CameraUsage") && !usage.ContainsKey("PhotoUsage")
            && await lockdown.GetValueAsync(domain + ".factory", "CameraUsage", ct).ConfigureAwait(false) is { } camera)
            usage["CameraUsage"] = camera;
        return usage;
    }

    public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => UseAsync(async c =>
    {
        PlistDict domain;
        await using (var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false))
            domain = await lockdown.GetAllValuesAsync("com.apple.mobile.battery", ct).ConfigureAwait(false);

        PlistDict? powerSource = null;
        try
        {
            await using var relay = await c.StartServiceAsync(ServiceNames.DiagnosticsRelay, ct).ConfigureAwait(false);
            powerSource = await DiagnosticsRelayClient.IORegistryAsync(relay, "IOPMPowerSource", ct).ConfigureAwait(false);
            await DiagnosticsRelayClient.GoodbyeAsync(relay, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported or DeviceErrorCodes.Protocol)
        {
            // Level and charging still come from the battery domain.
        }
        return LockdownMapping.Battery(powerSource, domain);
    });

    /// <summary>Raw lockdown values of one domain (null = the top domain), for diagnostics.</summary>
    public Task<PlistDict> GetLockdownValuesAsync(string? domain, CancellationToken ct) => UseAsync(async c =>
    {
        await using var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false);
        return await lockdown.GetAllValuesAsync(domain, ct).ConfigureAwait(false);
    });

    /// <summary>AFC's GetDeviceInfo for the Media volume (FSTotalBytes, FSFreeBytes, FSBlockSize, Model), for diagnostics.</summary>
    public Task<IReadOnlyDictionary<string, string>> GetAfcDeviceInfoAsync(CancellationToken ct) =>
        UseMediaAfcAsync(afc => afc.GetDeviceInfoAsync(ct), ct);

    /// <summary>One lockdown value by key (some keys only come back when asked for by name), for diagnostics.</summary>
    public Task<object?> GetLockdownValueAsync(string? domain, string key, CancellationToken ct) => UseAsync(async c =>
    {
        await using var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false);
        return await lockdown.GetValueAsync(domain, key, ct).ConfigureAwait(false);
    });

    /// <summary>Raw IORegistry properties for an entry class (e.g. IOPMPowerSource), for diagnostics.</summary>
    public Task<PlistDict?> GetIORegistryAsync(string? entryClass, CancellationToken ct, string? entryName = null, string? plane = null) => UseAsync(async c =>
    {
        await using var relay = await c.StartServiceAsync(ServiceNames.DiagnosticsRelay, ct).ConfigureAwait(false);
        var values = await DiagnosticsRelayClient.IORegistryAsync(relay, entryClass, ct, entryName, plane).ConfigureAwait(false);
        await DiagnosticsRelayClient.GoodbyeAsync(relay, ct).ConfigureAwait(false);
        return values;
    });

    /// <summary>A diagnostics_relay report such as "GasGauge", for diagnostics.</summary>
    public Task<PlistDict?> GetDiagnosticsAsync(string request, CancellationToken ct) => UseAsync(async c =>
    {
        await using var relay = await c.StartServiceAsync(ServiceNames.DiagnosticsRelay, ct).ConfigureAwait(false);
        var values = await DiagnosticsRelayClient.DiagnosticsAsync(relay, request, ct).ConfigureAwait(false);
        await DiagnosticsRelayClient.GoodbyeAsync(relay, ct).ConfigureAwait(false);
        return values;
    });

    public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => UseAsync(async c =>
    {
        await using var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false);
        _values = await ReadDeviceValuesAsync(lockdown, ct).ConfigureAwait(false);
        return LockdownMapping.Details(Udid, _values);
    });

    public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => UseAsync<IReadOnlyList<FileRoot>>(async c =>
    {
        var roots = new List<FileRoot> { new(MediaRoot, "Media", FileRootKind.Media) };
        await using var proxy = await c.StartServiceAsync(ServiceNames.InstallationProxy, ct).ConfigureAwait(false);
        var apps = await InstallationProxyClient.BrowseAsync(proxy, "User", ct, InstallationProxyClient.LightAttributes).ConfigureAwait(false);
        roots.AddRange(apps
            .Where(a => a.GetBool("UIFileSharingEnabled") == true && a.GetString("CFBundleIdentifier") is not null)
            .Select(a => new FileRoot(
                AppRootPrefix + a.GetString("CFBundleIdentifier"),
                IosAppManager.DisplayName(a),
                FileRootKind.AppDocuments,
                a.GetString("CFBundleIdentifier")))
            .OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase));
        return roots;
    });

    public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => UseAsync<IDeviceFileSystem>(async c =>
    {
        if (rootKey == MediaRoot)
            return new IosFileSystem(await c.StartAfcAsync(ct).ConfigureAwait(false));
        if (rootKey.StartsWith(AppRootPrefix, StringComparison.Ordinal) && rootKey.Length > AppRootPrefix.Length)
        {
            var bundleId = rootKey[AppRootPrefix.Length..];
            var connection = await c.StartServiceAsync(ServiceNames.HouseArrest, ct).ConfigureAwait(false);
            try
            {
                var afc = await HouseArrestClient.VendDocumentsAsync(connection, bundleId, ct).ConfigureAwait(false);
                return new IosFileSystem(afc, "/Documents");
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        throw new DeviceException(DeviceErrorCodes.PathNotFound, $"No file root '{rootKey}' on this iPhone.");
    });

    private static string SafeFolderName(string udid) =>
        string.Concat(udid.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));
}
