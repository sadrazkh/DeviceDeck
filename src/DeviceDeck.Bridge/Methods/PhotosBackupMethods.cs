using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "Photos backup and adding photos (round 2)".

/// <param name="IncludeHidden">The old switch, still taken (on: hidden photos with the others); <paramref name="Hidden"/> replaces it.</param>
public sealed record PhotosBackupConfigureParams(
    string DeviceId,
    string? Destination = null,
    string? FolderPattern = null,
    bool? ConvertHeic = null,
    bool? IncludeHidden = null,
    bool UseDefaults = false,
    PhotoBackupRange? Range = null,
    PhotoBackupOnConnect? OnConnect = null,
    bool? SaveEdited = null,
    PhotoBackupHidden? Hidden = null,
    PhotoBackupHiddenLayout? HiddenLayout = null,
    string? HiddenFolder = null);

public sealed record PhotosImportToAppParams(string DeviceId, string AppId, IReadOnlyList<string> Files);

public sealed record ForgottenResult(int Forgotten);

public sealed record PhotosBackupSpaceParams(string Folder);

/// <summary>photos.newCount, photos.backup*, photos.importToApp.</summary>
public static class PhotosBackupMethods
{
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        rpc.Register<DeviceParams, PhotoBackupCount>("photos.newCount", (p, ct) => Backup(s).CountNewAsync(p.DeviceId, ct));
        rpc.Register<DeviceParams, PhotoBackupStatus>("photos.backupStatus", (p, ct) => Backup(s).GetStatusAsync(p.DeviceId, ct));
        rpc.Register<PhotosBackupConfigureParams, PhotoBackupStatus>("photos.backupConfigure", (p, ct) =>
        {
            var backup = Backup(s);
            backup.Configure(p.DeviceId, p.Destination, p.FolderPattern, p.ConvertHeic, p.IncludeHidden, p.UseDefaults, p.Range, p.OnConnect, p.SaveEdited,
                p.Hidden, p.HiddenLayout, p.HiddenFolder);
            return backup.GetStatusAsync(p.DeviceId, ct);
        });
        rpc.Register<DeviceParams, JobIdResult>("photos.backup", async (p, ct) =>
            new JobIdResult((await Backup(s).StartAsync(p.DeviceId, ct).ConfigureAwait(false)).Id));
        rpc.Register<DeviceParams, ForgottenResult>("photos.backupForget", (p, _) =>
        {
            s.Hub.GetUsableDevice(p.DeviceId);
            return Task.FromResult(new ForgottenResult(Backup(s).Forget(p.DeviceId)));
        });
        rpc.Register<PhotosBackupSpaceParams, PhotoBackupSpace>("photos.backupSpace", (p, _) => Task.FromResult(Backup(s).CheckSpace(p.Folder)));
        // Round 3 (localsend): a dated folder per send, split into parts of 200; the result says where they are.
        rpc.Register<PhotosImportToAppParams, Core.Transfers.Handlers.PhotosToAppStarted>("photos.importToApp", (p, ct) =>
            Core.Transfers.Handlers.PhotosToAppHandler.EnqueueAsync(s.Hub, s.Transfers, p.DeviceId, p.AppId, p.Files, ct));
    }

    private static PhotoBackupService Backup(BridgeServices s) =>
        s.PhotoBackup ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "Photo backup isn't available in this build.");
}
