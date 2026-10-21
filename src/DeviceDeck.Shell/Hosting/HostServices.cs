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
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;
using DeviceDeck.Shell.Desktop.DragOut;

namespace DeviceDeck.Shell.Hosting;

/// <summary>The composition root: everything below the window, built once per process.</summary>
internal sealed class HostServices : IAsyncDisposable
{
    private HostServices(AppPaths paths, bool devMode, FakeDeviceProvider? fake, DeviceHub hub, SqliteTransferStore store,
        TransferQueue transfers, SettingsStore settings, WicImageTranscoder transcoder, DragOutRegistry dragOuts, PhotoBackupService photoBackup,
        BackupService backups)
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
        Videos = new VideoHandler(hub, VideoCache);
        DragOuts = dragOuts;
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

    /// <summary>video/{deviceId}/{assetId}?size=…, with HTTP Range so the viewer can seek.</summary>
    public VideoHandler Videos { get; }

    /// <summary>Drags into File Explorer that are still copying (see DragOutService).</summary>
    public DragOutRegistry DragOuts { get; }

    public string DragOutStaging => Path.Combine(Paths.Root, "cache", "dragout");

    public string VideoCache => Path.Combine(Paths.Root, "cache", "video");

    /// <summary>Photos backup; <c>CountNewAsync(deviceId, ct)</c> gives the "N new photos" count for a notification.</summary>
    public PhotoBackupService PhotoBackup { get; }

    // Clean-up and battery history (round 2).
    public CleanupService Cleanup { get; private init; } = null!;
    public BatteryRecorder Battery { get; private init; } = null!;

    /// <summary>Photos into Photos with LocalSend (round 3).</summary>
    public DeviceDeck.Core.LocalSend.LocalSendService LocalSend { get; private init; } = null!;

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
        var dragOuts = new DragOutRegistry();
        var localSend = new DeviceDeck.Core.LocalSend.LocalSendService(hub, store.LoadItems);
        DeviceDeck.Core.LocalSend.LocalSendService.CancelLeftovers(store);
        var transfers = new TransferQueue(hub, store, [.. TransferHandlers.All(transcoder), photoBackup.Handler, new DragOutHandler(dragOuts), localSend.Handler], keepAwake);
        transfers.Start();
        photoBackup.Attach(transfers);
        localSend.Attach(transfers);
        var backups = new BackupService(hub, settings, new DeviceDeck.Ios.Backup.Format.IosBackupFormat(),
            new BackupServiceOptions { KeepAwake = keepAwake, Transcoder = transcoder });
        backups.Start();

        // Before the hub starts, so the first phones to arrive get their battery snapshot.
        var battery = new BatteryRecorder(hub, new BatteryHistoryStore(BatteryHistoryStore.PathIn(paths)));
        battery.RecordFailed += (_, e) => ShellLog.Error($"Battery snapshot for {e.DeviceId} failed.", e.Error);
        var cleanup = new CleanupService(hub, transfers, settings);
        var host = new HostServices(paths, devMode, fake, hub, store, transfers, settings, transcoder, dragOuts, photoBackup, backups)
        {
            Cleanup = cleanup,
            Battery = battery,
            LocalSend = localSend,
        };

        // A drag into File Explorer doesn't survive a restart: Explorer's end of it is gone.
        foreach (var job in transfers.List().Where(j => j.Kind == TransferKind.DragOut && j.State is not (TransferState.Done or TransferState.Failed or TransferState.Cancelled)))
        {
            transfers.Cancel(job.Id);
        }

        // Left behind by a crash or a sign-out: drag-out staging files and cached videos (both can be gigabytes).
        DragOutService.ClearStaging(host.DragOutStaging);
        DragOutService.ClearStaging(host.VideoCache);
        return host;
    }

    /// <summary>The main window passes its notifications and desktop shell; a second window (mirroring) goes without.</summary>
    public BridgeServices BridgeServices(IShellServices shell, INotificationService? notifications = null, IDesktopShell? desktop = null) => new()
    {
        Hub = Hub,
        Transfers = Transfers,
        Settings = Settings,
        Shell = shell,
        Screenshots = Screenshots,
        Version = Version,
        DevFake = Fake,
        Notifications = notifications ?? NullNotificationService.Instance,
        Desktop = desktop,
        PhotoBackup = PhotoBackup,
        Cleanup = Cleanup,
        Battery = Battery,
        Toolbox = DeviceDeck.Shell.Toolbox.ShellToolbox.Create(this),
        Backups = Backups,
        LocalSend = LocalSend,
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
        await StepAsync("LocalSend", async () => await LocalSend.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await StepAsync("video cache", async () => await Videos.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
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
