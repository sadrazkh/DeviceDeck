using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

/// <summary>One simulated phone. It survives a simulated unplug, so deletions and installs are still there when it returns.</summary>
public sealed partial class FakeDevice : IDevice
{
    private readonly FakeDeviceProvider _provider;
    private DeviceSummary _summary;
    private volatile bool _present;

    internal FakeDevice(FakeDeviceProvider provider, FakeDeviceSpec spec, FakeOptions options)
    {
        _provider = provider;
        Spec = spec;
        Options = options;
        _summary = WithRename(spec.Summary);
        Photos = new FakePhotoLibrary(this);
        Apps = new FakeAppManager(this);
        Actions = new FakeDeviceActions(this);
        Roots = spec.Roots.ToDictionary(r => r.Root.Key, StringComparer.Ordinal);
    }

    internal FakeDeviceSpec Spec { get; }
    internal FakeOptions Options { get; }
    internal FakeDeviceProvider Provider => _provider;
    internal Lock Gate { get; } = new();
    internal Dictionary<string, FakeRoot> Roots { get; }

    public DeviceSummary Summary => Volatile.Read(ref _summary);

    public IPhotoLibrary Photos { get; }
    public IAppManager Apps { get; }
    public IDeviceActions Actions { get; }

    internal bool Present
    {
        get => _present;
        set => _present = value;
    }

    internal void SetSummary(DeviceSummary summary) => Volatile.Write(ref _summary, summary);

    /// <summary>Throws what a real provider throws once the cable is out or the phone is not ready.</summary>
    internal void EnsureConnected()
    {
        if (!_present || Summary.State == DeviceState.Disconnected)
        {
            throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone was disconnected.");
        }

        switch (Summary.State)
        {
            case DeviceState.AwaitingTrust:
                throw new DeviceException(DeviceErrorCodes.TrustRequired, "Unlock the iPhone and tap Trust.");
            case DeviceState.Unauthorized:
                throw new DeviceException(DeviceErrorCodes.Unauthorized, "Tap Allow on the phone to allow USB debugging.");
            case DeviceState.Connecting:
                throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone is still connecting.");
        }
    }

    internal void Require(DeviceCapability capability, string what)
    {
        if ((Summary.Capabilities & capability) != capability)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{Summary.Name} can't {what}.");
        }
    }

    public Task<StorageInfo> GetStorageAsync(CancellationToken ct)
    {
        EnsureConnected();
        return Task.FromResult(Spec.Storage);
    }

    public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct)
    {
        EnsureConnected();
        return Task.FromResult(Spec.Battery);
    }

    public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct)
    {
        EnsureConnected();
        return Task.FromResult(Spec.Details);
    }

    public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct)
    {
        EnsureConnected();
        IReadOnlyList<FileRoot> roots = Spec.Roots.Select(r => r.Root).ToArray();
        return Task.FromResult(roots);
    }

    public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct)
    {
        EnsureConnected();
        if (!Roots.TryGetValue(rootKey, out var root) && !TryAppFilesRoot(rootKey, out root))
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That folder isn't on the phone.", new KeyNotFoundException(rootKey));
        }

        return Task.FromResult<IDeviceFileSystem>(new FakeDeviceFileSystem(this, root));
    }
}
