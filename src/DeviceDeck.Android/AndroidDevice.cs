using System.Text.RegularExpressions;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Apps;
using DeviceDeck.Android.Files;
using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

/// <summary>One phone on our adb server. Created from its adb line; <see cref="LoadProfileAsync"/> fills it in once adb says "device".</summary>
internal sealed partial class AndroidDevice : IDevice, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private DeviceProfile? _profile;
    private DeviceSummary _summary;

    public AndroidDevice(IAdbChannel channel, AdbDeviceEntry entry, AppMetadataCache cache, AndroidProviderOptions options)
    {
        Channel = channel;
        Entry = entry;
        _summary = SummaryBuilder.FromEntry(entry);
        Photos = new AndroidPhotoLibrary(channel, () => RequireProfile().Sdk);
        Apps = new AndroidAppManager(channel, RequireProfile, cache, options, _lifetime.Token);
        Actions = new AndroidDeviceActions(channel);
    }

    internal IAdbChannel Channel { get; }

    internal AdbDeviceEntry Entry { get; set; }

    internal DeviceProfile? Profile => _profile;

    internal CancellationToken Lifetime => _lifetime.Token;

    public DeviceSummary Summary => Volatile.Read(ref _summary);

    internal void SetSummary(DeviceSummary summary) => Volatile.Write(ref _summary, summary);

    public IPhotoLibrary Photos { get; }

    public IAppManager Apps { get; }

    public IDeviceActions Actions { get; }

    /// <summary>getprop + device name + the phone's UTC offset; sets <see cref="Profile"/>.</summary>
    internal async Task<DeviceProfile> LoadProfileAsync(CancellationToken ct)
    {
        string getprop = await Channel.ShellCheckedAsync("getprop", ct).ConfigureAwait(false);
        var extra = ShellScript.Split((await Channel.ShellAsync(ShellScript.Build(
            ("name", "settings get global device_name"),
            ("zone", "date +%z")), ct).ConfigureAwait(false)).Output);

        string? name = extra.GetValueOrDefault("name")?.Trim();
        if (string.IsNullOrEmpty(name) || name == "null")
        {
            name = null;
        }
        FileListingParser.TryParseZone(extra.GetValueOrDefault("zone") ?? "", out var offset);
        var props = GetPropParser.Parse(getprop);
        if (props.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, "The phone returned no system properties.");
        }
        var profile = new DeviceProfile(props, name, offset);
        Volatile.Write(ref _profile, profile);
        return profile;
    }

    public async Task<StorageInfo> GetStorageAsync(CancellationToken ct)
    {
        EnsureReady();
        var sections = ShellScript.Split((await Channel.ShellAsync(StorageBuilder.Script(), ct).ConfigureAwait(false)).Output);
        var volumes = StorageBuilder.ParseVolumes(sections.GetValueOrDefault("storage"), sections.GetValueOrDefault("volumes"));
        var df = DfParser.Parse(sections.GetValueOrDefault("df") ?? "");
        var stats = DiskStatsParser.Parse(sections.GetValueOrDefault("diskstats") ?? "");
        return StorageBuilder.Build(df, stats, volumes);
    }

    public async Task<BatteryInfo> GetBatteryAsync(CancellationToken ct)
    {
        var profile = RequireProfile();
        var sections = ShellScript.Split((await Channel.ShellAsync(ShellScript.Build(
            ("battery", "dumpsys battery"),
            ("sysfs", BatteryParser.SysfsCommand),
            ("sticky", profile.Sdk >= 34 ? BatteryParser.StickyCycleCountCommand : "true")), ct).ConfigureAwait(false)).Output);
        string battery = sections.GetValueOrDefault("battery") ?? "";
        if (!battery.Contains("level:", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, "The phone didn't report its battery state.");
        }
        return BatteryParser.Parse(battery, sections.GetValueOrDefault("sysfs"), sections.GetValueOrDefault("sticky"));
    }

    public async Task<DeviceDetails> GetDetailsAsync(CancellationToken ct)
    {
        var profile = RequireProfile();
        var sections = ShellScript.Split((await Channel.ShellAsync(DetailsBuilder.Script(), ct).ConfigureAwait(false)).Output);
        return DetailsBuilder.Build(profile, Entry.Serial, sections);
    }

    public async Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct)
    {
        EnsureReady();
        var sections = ShellScript.Split((await Channel.ShellAsync(ShellScript.Build(
            ("storage", "ls -1 /storage"),
            ("volumes", "sm list-volumes public")), ct).ConfigureAwait(false)).Output);
        var roots = new List<FileRoot> { new("internal", "Internal storage", FileRootKind.InternalStorage) };
        foreach (var volume in StorageBuilder.ParseVolumes(sections.GetValueOrDefault("storage"), sections.GetValueOrDefault("volumes")))
        {
            roots.Add(new FileRoot(volume.Key, volume.Label, FileRootKind.SdCard));
        }
        return roots;
    }

    [GeneratedRegex(@"^sd:(?<id>[A-Za-z0-9][A-Za-z0-9_-]*)$")]
    private static partial Regex SdRootKey();

    public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct)
    {
        var profile = RequireProfile();
        string basePath;
        if (rootKey == "internal")
        {
            basePath = "/sdcard";
        }
        else if (SdRootKey().Match(rootKey) is { Success: true } match)
        {
            basePath = "/storage/" + match.Groups["id"].Value;
        }
        else if (TryAppFilesBase(rootKey, out var appFolder))
        {
            basePath = appFolder;
        }
        else
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, $"There's no storage called '{rootKey}' on this phone.");
        }
        return Task.FromResult<IDeviceFileSystem>(new AndroidFileSystem(Channel, basePath, profile.Sdk, profile.UtcOffset));
    }

    internal DeviceProfile RequireProfile()
    {
        EnsureReady();
        return _profile ?? throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone is still connecting; try again in a moment.");
    }

    private void EnsureReady()
    {
        switch (Summary.State)
        {
            case DeviceState.Ready:
                return;
            case DeviceState.Unauthorized:
                throw new DeviceException(DeviceErrorCodes.Unauthorized, AdbErrors.UnauthorizedMessage);
            case DeviceState.Disconnected:
                throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone was disconnected.");
            default:
                throw new DeviceException(DeviceErrorCodes.Disconnected, "The phone is still connecting; try again in a moment.");
        }
    }

    /// <summary>Stops background work for this phone (label loading). The token source isn't disposed: late readers may still hold its token.</summary>
    public void Dispose() => _lifetime.Cancel();
}
