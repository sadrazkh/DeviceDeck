using System.Diagnostics;
using System.Reflection;
using DeviceDeck.Bridge;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Battery;
using DeviceDeck.Core.Cleanup;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Shell.Hosting;

/// <summary>The composition root: everything below the window, built once per process.</summary>
internal sealed class HostServices : IAsyncDisposable
{
    private HostServices(AppPaths paths, bool devMode, FakeDeviceProvider? fake, DeviceHub hub, SqliteTransferStore store,
        TransferQueue transfers, SettingsStore settings, WicImageTranscoder transcoder, PhotoBackupService photoBackup, BackupService backups)
    {
        PhotoBackup = photoBackup;
        Backups = backups;
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
    public BackupService Backups { get; }

    /// <summary>Photos backup; <c>CountNewAsync(deviceId, ct)</c> gives the "N new photos" count for a notification.</summary>
    public PhotoBackupService PhotoBackup { get; }

    // Clean-up and battery history (round 2).
    public CleanupService Cleanup { get; private init; } = null!;
    public BatteryRecorder Battery { get; private init; } = null!;

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
        hub.ProviderStopFailed += (_, e) => ShellLog.Error($"Provider {e.ProviderId} did not stop cleanly.", e.Error);
        var store = new SqliteTransferStore(paths.TransfersDatabase);
        var transcoder = new WicImageTranscoder();
        var settings = new SettingsStore(paths.SettingsFile);
        var photoBackup = new PhotoBackupService(Path.Combine(paths.Root, "photo-backup.db"), hub, settings, transcoder, PhotosHeicProbe.Check());
        var keepAwake = new WindowsKeepAwake();
        var transfers = new TransferQueue(hub, store, [.. TransferHandlers.All(transcoder), photoBackup.Handler], keepAwake);
        transfers.Start();
        photoBackup.Attach(transfers);
        var backups = new BackupService(hub, settings, new DeviceDeck.Ios.Backup.Format.IosBackupFormat(),
            new BackupServiceOptions { KeepAwake = keepAwake, Transcoder = transcoder });
        backups.Start();

        // Before the hub starts, so the first phones to arrive get their battery snapshot.
        var battery = new BatteryRecorder(hub, new BatteryHistoryStore(BatteryHistoryStore.PathIn(paths)));
        battery.RecordFailed += (_, e) => ShellLog.Error($"Battery snapshot for {e.DeviceId} failed.", e.Error);
        var cleanup = new CleanupService(hub, transfers, settings);
        return new HostServices(paths, devMode, fake, hub, store, transfers, settings, transcoder, photoBackup, backups) { Cleanup = cleanup, Battery = battery };
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
        PhotoBackup = PhotoBackup,
        Cleanup = Cleanup,
        Battery = Battery,
        Backups = Backups,
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

    /// <summary>
    /// Stops the transfer workers (their jobs resume next start), closes the database, then stops every provider
    /// (connections, the private adb server). Each step runs even when the one before it fails.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var clock = Stopwatch.StartNew();
        await StepAsync("clean-up", async () => await Cleanup.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await StepAsync("battery history", async () => await Battery.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await StepAsync("transfer queue", async () => await Transfers.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await StepAsync("backup service", async () => await Backups.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await StepAsync("transfer store", () =>
        {
            Store.Dispose();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await StepAsync("photo backup", () =>
        {
            PhotoBackup.Dispose();
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await StepAsync("device providers", async () => await Hub.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        ShellLog.Info($"DeviceDeck stopped in {clock.Elapsed.TotalSeconds:0.0} s.");
    }

    private static async Task StepAsync(string what, Func<Task> step)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await step().ConfigureAwait(false);
            ShellLog.Info($"Stopped {what} in {clock.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            ShellLog.Error($"Stopping {what} failed after {clock.ElapsedMilliseconds} ms.", ex);
        }
    }
}
