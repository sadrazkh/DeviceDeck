using System.Reflection;
using DeviceDeck.Bridge;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Shell.Hosting;

/// <summary>The composition root: everything below the window, built once per process.</summary>
internal sealed class HostServices : IAsyncDisposable
{
    private HostServices(AppPaths paths, bool devMode, FakeDeviceProvider? fake, DeviceHub hub, SqliteTransferStore store,
        TransferQueue transfers, SettingsStore settings, WicImageTranscoder transcoder)
    {
        Paths = paths;
        DevMode = devMode;
        Fake = fake;
        Hub = hub;
        Store = store;
        Transfers = transfers;
        Settings = settings;
        Transcoder = transcoder;
        Screenshots = new ScreenshotStore();
        Thumbnails = new ThumbnailCache(paths.ThumbnailCache);
        Images = new DeviceImageHandler(hub, Thumbnails, Screenshots, transcoder, Path.Combine(paths.Root, "cache", "viewer"));
    }

    public AppPaths Paths { get; }

    /// <summary>DEVICEDECK_DEV=1: the page comes from the Vite dev server and DevTools are on.</summary>
    public bool DevMode { get; }

    public FakeDeviceProvider? Fake { get; }
    public DeviceHub Hub { get; }
    public SqliteTransferStore Store { get; }
    public TransferQueue Transfers { get; }
    public SettingsStore Settings { get; }
    public WicImageTranscoder Transcoder { get; }
    public ScreenshotStore Screenshots { get; }
    public ThumbnailCache Thumbnails { get; }
    public DeviceImageHandler Images { get; }

    public static string Version { get; } =
        typeof(HostServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public static HostServices Create()
    {
        var paths = new AppPaths(Environment.GetEnvironmentVariable("DEVICEDECK_DATA") is { Length: > 0 } data ? data : null);
        Directory.CreateDirectory(paths.Root);
        ShellLog.Initialize(paths.Logs);
        ShellLog.Info($"DeviceDeck {Version} starting; data in {paths.Root}");

        var devMode = Environment.GetEnvironmentVariable("DEVICEDECK_DEV") == "1";
        var providers = ProviderLoader.Load(new FakeOptions(), out var fake);
        var hub = new DeviceHub(providers);
        var store = new SqliteTransferStore(paths.TransfersDatabase);
        var transcoder = new WicImageTranscoder();
        var transfers = new TransferQueue(hub, store, TransferHandlers.All(transcoder), new WindowsKeepAwake());
        transfers.Start();
        var settings = new SettingsStore(paths.SettingsFile);
        return new HostServices(paths, devMode, fake, hub, store, transfers, settings, transcoder);
    }

    public BridgeServices BridgeServices(IShellServices shell) => new()
    {
        Hub = Hub,
        Transfers = Transfers,
        Settings = Settings,
        Shell = shell,
        Screenshots = Screenshots,
        Version = Version,
        DevFake = Fake,
    };

    /// <summary>Starts the providers once the window is up, so the first paint isn't waiting on USB.</summary>
    public async Task StartAsync()
    {
        try
        {
            await Hub.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShellLog.Error("Device hub failed to start.", ex);
        }

        _ = Task.Run(() =>
        {
            try
            {
                Thumbnails.Trim();
            }
            catch (Exception ex)
            {
                ShellLog.Error("Thumbnail cache trim failed.", ex);
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await Transfers.DisposeAsync().ConfigureAwait(false);
        Store.Dispose();
        await Hub.DisposeAsync().ConfigureAwait(false);
        ShellLog.Info("DeviceDeck stopped.");
    }
}
