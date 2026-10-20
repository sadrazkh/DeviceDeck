using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Battery;
using DeviceDeck.Core.Cleanup;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Methods;

/// <summary>Everything the methods need; built once by the Shell (or a test).</summary>
public sealed class BridgeServices
{
    public required DeviceHub Hub { get; init; }
    public required TransferQueue Transfers { get; init; }
    public required SettingsStore Settings { get; init; }
    public required IShellServices Shell { get; init; }
    public required ScreenshotStore Screenshots { get; init; }
    public string Version { get; init; } = "0.1.0";

    /// <summary>When set, dev.* methods simulate a pulled cable on the fake phones.</summary>
    public FakeDeviceProvider? DevFake { get; init; }

    /// <summary>Photos backup (photos.newCount, photos.backup*); without it those methods answer not_supported.</summary>
    public PhotoBackupService? PhotoBackup { get; init; }

    // Clean-up and battery history (round 2): cleanup.* and battery.* are registered only when set.
    public CleanupService? Cleanup { get; init; }
    public BatteryRecorder? Battery { get; init; }

    /// <summary>Backups, contacts and messages (round 2); null answers those methods with not_supported.</summary>
    public BackupService? Backups { get; init; }
}

/// <summary>Registers every method of docs/bridge-contract.md on a dispatcher.</summary>
public static class BridgeMethods
{
    private const int MaxPage = 2000;

    public static JsonRpcDispatcher Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        var hub = s.Hub;

        rpc.Register("app.hello", _ => Task.FromResult(new HelloResult(s.Version, IsWebView: true)));
        rpc.Register("drivers.status", hub.GetDriverStatusAsync);

        // Device
        rpc.Register("device.list", _ => Task.FromResult(hub.ListDevices()));
        rpc.Register<DeviceParams, StorageInfo>("device.storage", (p, ct) =>
            Device(hub, p.DeviceId, DeviceCapability.Storage, "show its storage").GetStorageAsync(ct));
        rpc.Register<DeviceParams, BatteryInfo>("device.battery", (p, ct) =>
            Device(hub, p.DeviceId, DeviceCapability.Battery, "show its battery").GetBatteryAsync(ct));
        rpc.Register<DeviceParams, DeviceDetails>("device.details", (p, ct) =>
            Device(hub, p.DeviceId, DeviceCapability.Info, "show its details").GetDetailsAsync(ct));

        // Photos
        rpc.Register<DeviceParams, List<PhotoAlbum>>("photos.albums", async (p, ct) =>
        {
            var device = Device(hub, p.DeviceId, DeviceCapability.PhotosRead, "show photos");
            var albums = new List<PhotoAlbum>();
            await foreach (var album in device.Photos.GetAlbumsAsync(ct).ConfigureAwait(false))
            {
                albums.Add(album);
            }

            return albums;
        });
        rpc.Register<PhotosListParams, Page<PhotoAsset>>("photos.list", (p, ct) =>
        {
            if (p.Offset < 0 || p.Limit < 1 || p.Limit > MaxPage)
            {
                throw new ArgumentException($"offset must be 0 or more and limit between 1 and {MaxPage}.");
            }

            return Device(hub, p.DeviceId, DeviceCapability.PhotosRead, "show photos").Photos.GetAssetsAsync(p.AlbumId, p.Offset, p.Limit, ct);
        });
        rpc.Register<PhotosExportParams, JobIdResult>("photos.export", (p, ct) =>
            Enqueue(s, p.DeviceId, TransferKind.PhotosExport, new PhotosExportRequest(p.AssetIds, p.Destination, p.ConvertHeic, p.FolderPattern), ct));
        rpc.Register<PhotosDeleteParams, DeletedResult>("photos.delete", async (p, ct) =>
        {
            var ids = p.AssetIds.Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0)
            {
                throw new ArgumentException("assetIds is empty.");
            }

            await Device(hub, p.DeviceId, DeviceCapability.PhotosDelete, "delete photos").Photos.DeleteAsync(ids, ct).ConfigureAwait(false);
            return new DeletedResult(ids.Length);
        });
        rpc.Register<DeviceFilesParams, JobIdResult>("photos.import", (p, ct) =>
            Enqueue(s, p.DeviceId, TransferKind.PhotosImport, new PhotosImportRequest(p.Files), ct));

        // Files
        rpc.Register<DeviceParams, IReadOnlyList<FileRoot>>("files.roots", (p, ct) =>
            Device(hub, p.DeviceId, DeviceCapability.None, "").GetFileRootsAsync(ct));
        rpc.Register<FilesListParams, List<FileEntry>>("files.list", async (p, ct) =>
        {
            await using var fs = await OpenAsync(hub, p.DeviceId, p.Root, ct).ConfigureAwait(false);
            var entries = new List<FileEntry>();
            await foreach (var entry in fs.ListAsync(p.Path, ct).ConfigureAwait(false))
            {
                entries.Add(entry);
            }

            return entries;
        });
        rpc.Register<FilesListParams, FileEntry>("files.mkdir", async (p, ct) =>
        {
            await using var fs = await OpenAsync(hub, p.DeviceId, p.Root, ct).ConfigureAwait(false);
            await fs.CreateDirectoryAsync(p.Path, ct).ConfigureAwait(false);
            return await fs.StatAsync(p.Path, ct).ConfigureAwait(false)
                ?? new FileEntry(p.Path, LastSegment(p.Path), true, 0, DateTimeOffset.Now);
        });
        rpc.Register<FilesRenameParams, FileEntry>("files.rename", async (p, ct) =>
        {
            // "to" may be a bare new name (rename in place) or a full path (move).
            var to = p.To.Contains('/') ? p.To : ParentOf(p.From) + p.To;
            await using var fs = await OpenAsync(hub, p.DeviceId, p.Root, ct).ConfigureAwait(false);
            await fs.RenameAsync(p.From, to, ct).ConfigureAwait(false);
            return await fs.StatAsync(to, ct).ConfigureAwait(false)
                ?? throw new DeviceException(DeviceErrorCodes.Protocol, "The phone renamed it but can't find it now.");
        });
        rpc.Register<FilesDeleteParams, DeletedResult>("files.delete", async (p, ct) =>
        {
            if (p.Paths.Count == 0)
            {
                throw new ArgumentException("paths is empty.");
            }

            await using var fs = await OpenAsync(hub, p.DeviceId, p.Root, ct).ConfigureAwait(false);
            var deleted = 0;
            foreach (var path in p.Paths.Distinct(StringComparer.Ordinal))
            {
                await fs.DeleteAsync(path, p.Recursive, ct).ConfigureAwait(false);
                deleted++;
            }

            return new DeletedResult(deleted);
        });
        rpc.Register<FilesCopyToPcParams, JobIdResult>("files.copyToPc", (p, ct) =>
            Enqueue(s, p.DeviceId, TransferKind.CopyToPc, new CopyToPcRequest(p.Root, p.Paths, p.Destination), ct));
        rpc.Register<FilesCopyToDeviceParams, JobIdResult>("files.copyToDevice", (p, ct) =>
            Enqueue(s, p.DeviceId, TransferKind.CopyToDevice, new CopyToDeviceRequest(p.Root, p.Files, p.Path), ct));

        // Apps
        rpc.Register<AppsListParams, IReadOnlyList<AppInfo>>("apps.list", (p, ct) =>
            Device(hub, p.DeviceId, DeviceCapability.AppsList, "list apps").Apps.ListAsync(p.IncludeSystem, ct));
        rpc.Register<DeviceFilesParams, JobIdResult>("apps.install", (p, ct) =>
        {
            Device(hub, p.DeviceId, DeviceCapability.AppsInstall, "install apps");
            return Enqueue(s, p.DeviceId, TransferKind.AppInstall, new AppInstallRequest(p.Files), ct);
        });
        rpc.Register<AppParams, Empty>("apps.uninstall", async (p, ct) =>
        {
            await Device(hub, p.DeviceId, DeviceCapability.AppsUninstall, "remove apps").Apps.UninstallAsync(p.AppId, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        rpc.Register<AppsExportParams, JobIdResult>("apps.export", (p, ct) =>
        {
            Device(hub, p.DeviceId, DeviceCapability.AppsExport, "save apps");
            return Enqueue(s, p.DeviceId, TransferKind.AppExport, new AppExportRequest(p.AppId, p.Destination), ct);
        });

        // Tools
        rpc.Register<DeviceParams, UrlResult>("tools.screenshot", async (p, ct) =>
        {
            var png = await Device(hub, p.DeviceId, DeviceCapability.Screenshot, "take screenshots").Actions.TakeScreenshotAsync(ct).ConfigureAwait(false);
            return new UrlResult(ScreenshotStore.UrlFor(p.DeviceId, s.Screenshots.Add(p.DeviceId, png)));
        });
        rpc.Register<RebootParams, Empty>("tools.reboot", async (p, ct) =>
        {
            var (capability, what) = p.Target switch
            {
                RebootTarget.Recovery => (DeviceCapability.RebootToRecovery, "restart into recovery"),
                RebootTarget.Bootloader => (DeviceCapability.RebootToBootloader, "restart into the bootloader"),
                _ => (DeviceCapability.Reboot, "restart"),
            };
            await Device(hub, p.DeviceId, capability, what).Actions.RebootAsync(p.Target, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        rpc.Register<DeviceParams, Empty>("tools.shutdown", async (p, ct) =>
        {
            await Device(hub, p.DeviceId, DeviceCapability.Shutdown, "shut down").Actions.ShutdownAsync(ct).ConfigureAwait(false);
            return Empty.Value;
        });

        // Transfers
        rpc.Register("transfers.list", _ => Task.FromResult(s.Transfers.List()));
        rpc.Register<JobParams, Empty>("transfers.pause", (p, _) => Done(() => s.Transfers.Pause(p.JobId)));
        rpc.Register<JobParams, Empty>("transfers.resume", (p, _) => Done(() => s.Transfers.Resume(p.JobId)));
        rpc.Register<JobParams, Empty>("transfers.cancel", (p, _) => Done(() => s.Transfers.Cancel(p.JobId)));
        rpc.Register<ResolveConflictParams, Empty>("transfers.resolveConflict", (p, _) =>
            Done(() => s.Transfers.ResolveConflict(p.JobId, p.Choice, p.ApplyToAll)));

        // Shell
        rpc.Register<PickFolderParams, PathResult>("shell.pickFolder", async (p, ct) =>
            new PathResult(await s.Shell.PickFolderAsync(p.Title, ct).ConfigureAwait(false)));
        rpc.Register<PathParams, Empty>("shell.revealInExplorer", (p, _) =>
        {
            if (!Path.IsPathFullyQualified(p.Path))
            {
                throw new ArgumentException("path must be a full path on this PC.");
            }

            return Done(() => s.Shell.RevealInExplorer(p.Path));
        });

        // Settings
        rpc.Register("settings.get", _ => Task.FromResult(s.Settings.Current));
        rpc.RegisterRaw("settings.set", (json, _) =>
        {
            var patch = JsonNode.Parse(json.GetRawText()) as JsonObject
                ?? throw new ArgumentException("params must be an object of settings.");
            return Task.FromResult(s.Settings.Update(patch));
        });

        // Photos backup and adding photos to an iPhone app (round 2).
        PhotosBackupMethods.Register(rpc, s);

        // Clean-up and battery history (round 2).
        CleanupMethods.Register(rpc, s);
        BatteryMethods.Register(rpc, s);

        // Backups, contacts and messages (round 2).
        BackupMethods.Register(rpc, s);

        if (s.DevFake is { } fake)
        {
            // Not in the contract: lets the page (or a person in DevTools) play a pulled cable with the fake phones.
            rpc.Register<DeviceParams, Empty>("dev.disconnect", (p, _) => Done(() => fake.Disconnect(p.DeviceId)));
            rpc.Register<DeviceParams, Empty>("dev.reconnect", (p, _) => Done(() => fake.Reconnect(p.DeviceId)));
            rpc.Register<DevStateParams, Empty>("dev.setState", (p, _) => Done(() => fake.SetState(p.DeviceId, p.State)));
        }

        // Round 2 features, one line each.
        AppFilesMethods.Register(rpc, s);

        return rpc;
    }

    /// <summary>The device, ready for work and able to do <paramref name="capability"/>.</summary>
    private static IDevice Device(DeviceHub hub, string deviceId, DeviceCapability capability, string what)
    {
        var device = hub.GetUsableDevice(deviceId);
        if (capability != DeviceCapability.None && (device.Summary.Capabilities & capability) != capability)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't {what}.");
        }

        return device;
    }

    private static async Task<IDeviceFileSystem> OpenAsync(DeviceHub hub, string deviceId, string root, CancellationToken ct)
    {
        var device = hub.GetUsableDevice(deviceId);
        var needs = root.StartsWith("app:", StringComparison.Ordinal) ? DeviceCapability.AppDocuments : DeviceCapability.Files;
        if ((device.Summary.Capabilities & needs) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't share that folder.");
        }

        return await device.OpenFileSystemAsync(root, ct).ConfigureAwait(false);
    }

    private static async Task<JobIdResult> Enqueue(BridgeServices s, string deviceId, TransferKind kind, object request, CancellationToken ct)
    {
        var job = await s.Transfers.EnqueueAsync(deviceId, kind, request, s.Settings.Current.ConflictRule, ct).ConfigureAwait(false);
        return new JobIdResult(job.Id);
    }

    private static Task<Empty> Done(Action action)
    {
        action();
        return Task.FromResult(Empty.Value);
    }

    private static string ParentOf(string path)
    {
        var cut = path.TrimEnd('/').LastIndexOf('/');
        return cut < 0 ? "/" : path[..(cut + 1)];
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }
}
