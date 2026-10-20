using System.Diagnostics;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Services;

namespace DeviceDeck.Ios.Backup;

/// <summary>
/// One backup, restore or password change on one iPhone: lockdown values, Info.plist, the sync lock and
/// notifications iTunes uses, then mobilebackup2 with <see cref="BackupFolderHost"/> serving the files.
/// </summary>
internal sealed class IosBackupRunner(DeviceConnector connector, string udid, IosBackupOptions options)
{
    public const string BackupDomain = "com.apple.mobile.backup";

    public async Task<BackupDeviceState> GetStateAsync(CancellationToken ct)
    {
        await using var lockdown = await connector.OpenSessionAsync(ct).ConfigureAwait(false);
        var values = await lockdown.GetAllValuesAsync(BackupDomain, ct).ConfigureAwait(false);
        var encrypt = values.GetBool("WillEncrypt") ?? await lockdown.GetValueAsync(BackupDomain, "WillEncrypt", ct).ConfigureAwait(false) as bool?;
        return new BackupDeviceState(encrypt == true, values.GetBool("CloudBackupEnabled") == true);
    }

    public async Task BackupAsync(string folder, BackupRequest request, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        var deviceFolder = Path.Combine(folder, udid);
        progress.Report(new BackupProgress(BackupPhase.Starting, null, 0, 0));

        PlistDict values;
        await using (var lockdown = await connector.OpenSessionAsync(ct).ConfigureAwait(false))
            values = await lockdown.GetAllValuesAsync(null, ct).ConfigureAwait(false);
        var apps = await ListAppsAsync(ct).ConfigureAwait(false);

        LocalDisk(() =>
        {
            Directory.CreateDirectory(deviceFolder);
            BackupFiles.DeleteLeftovers(deviceFolder);
            BackupFiles.WriteAtomically(Path.Combine(deviceFolder, "Info.plist"), PlistSerializer.ToXml(InfoPlist.Build(udid, values, apps)));
            BackupFiles.PrepareStatus(deviceFolder, request.Full);
        });

        await RunWithLockAsync("backup", progress, async (client, token) =>
        {
            var backupOptions = request.Full ? new PlistDict { ["ForceFullBackup"] = true } : null;
            await client.SendRequestAsync("Backup", udid, udid, backupOptions, token).ConfigureAwait(false);
            progress.Report(new BackupProgress(BackupPhase.Preparing, null, 0, 0));
            var host = new BackupFolderHost(folder);
            await ServeAsync(client, host, "backup", progress, received: true, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task RestoreAsync(string backupFolder, RestoreRequest request, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        var full = Path.GetFullPath(backupFolder);
        var source = BackupFiles.SourceIdentifier(full)
                     ?? throw new DeviceException(BackupErrors.Corrupt, "That folder doesn't hold a complete iPhone backup (no Info.plist or Manifest.plist).");
        var restoreOptions = new PlistDict
        {
            ["RestoreSystemFiles"] = true,
            ["RestoreShouldReboot"] = request.Reboot,
            ["RestoreDontCopyBackup"] = true,
            ["RestorePreserveSettings"] = !request.RestoreSettings,
            ["RemoveItemsNotRestored"] = false,
        };
        if (request.Password is { Length: > 0 } password)
            restoreOptions["Password"] = password;

        progress.Report(new BackupProgress(BackupPhase.Starting, null, 0, 0));
        await RunWithLockAsync("restore", progress, async (client, token) =>
        {
            await client.SendRequestAsync("Restore", udid, source, restoreOptions, token).ConfigureAwait(false);
            progress.Report(new BackupProgress(BackupPhase.Preparing, null, 0, 0));
            // The phone asks for "{source}/…"; map that onto the chosen folder, which may be a dated copy.
            var host = new BackupFolderHost(Path.GetDirectoryName(full)!, new Dictionary<string, string>(StringComparer.Ordinal) { [source] = full });
            await ServeAsync(client, host, "restore", progress, received: false, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task ChangeEncryptionAsync(string? oldPassword, string? newPassword, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(oldPassword) && string.IsNullOrEmpty(newPassword))
            throw new ArgumentException("Give the new password, the old one, or both.");
        progress.Report(new BackupProgress(BackupPhase.Starting, null, 0, 0));
        await using var client = await MobileBackup2Client.ConnectAsync(connector, ct).ConfigureAwait(false);
        var fields = new PlistDict { ["TargetIdentifier"] = udid };
        if (!string.IsNullOrEmpty(oldPassword))
            fields["OldPassword"] = oldPassword;
        if (!string.IsNullOrEmpty(newPassword))
            fields["NewPassword"] = newPassword;
        await client.SendMessageAsync("ChangePassword", fields, ct).ConfigureAwait(false);
        // iOS 13 and later ask for the phone's passcode before changing anything.
        progress.Report(new BackupProgress(BackupPhase.WaitingForPhone, null, 0, 0));
        var scratch = Path.Combine(Path.GetTempPath(), "DeviceDeck", "mb2-" + Guid.NewGuid().ToString("N"));
        try
        {
            await client.RunAsync(new BackupFolderHost(scratch), "password change", ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                    Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The sync lock, the phone's cancel button and mobilebackup2 around one backup or restore.</summary>
    private async Task RunWithLockAsync(string operation, IProgress<BackupProgress> progress,
        Func<MobileBackup2Client, CancellationToken, Task> run, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancelledOnPhone = false;
        NotificationProxyClient? notifications = null;
        try
        {
            notifications = await NotificationProxyClient.StartAsync(connector, ct).ConfigureAwait(false);
            notifications.Notified += name =>
            {
                if (name != NotificationProxyClient.SyncCancelRequest)
                    return;
                cancelledOnPhone = true;
                linked.Cancel();
            };
            await notifications.ObserveAsync(NotificationProxyClient.SyncCancelRequest, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported or DeviceErrorCodes.Protocol)
        {
            // Without notifications the backup still works; only the phone's Cancel button goes unnoticed.
            if (notifications is not null)
                await notifications.DisposeAsync().ConfigureAwait(false);
            notifications = null;
        }

        try
        {
            await using var syncLock = await BackupSyncLock.AcquireAsync(connector, notifications,
                () => progress.Report(new BackupProgress(BackupPhase.WaitingForPhone, null, 0, 0)), options.SyncLockTimeout, linked.Token).ConfigureAwait(false);
            await using var client = await MobileBackup2Client.ConnectAsync(connector, linked.Token).ConfigureAwait(false);
            await run(client, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancelledOnPhone && !ct.IsCancellationRequested)
        {
            throw new DeviceException(BackupErrors.Refused, $"The {operation} was cancelled on the iPhone.");
        }
        finally
        {
            if (notifications is not null)
                await notifications.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ServeAsync(MobileBackup2Client client, BackupFolderHost host, string operation, IProgress<BackupProgress> progress,
        bool received, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long? last = null;
        host.Progress += () =>
        {
            var now = clock.ElapsedMilliseconds;
            if (last is { } previous && now - previous < 100)
                return;
            last = now;
            progress.Report(Snapshot(BackupPhase.Transferring));
        };
        await client.RunAsync(host, operation, ct).ConfigureAwait(false);
        progress.Report(Snapshot(BackupPhase.Finishing));

        BackupProgress Snapshot(BackupPhase phase) => received
            ? new BackupProgress(phase, host.Percent, host.BytesReceived, host.FilesReceived)
            : new BackupProgress(phase, host.Percent, host.BytesSent, host.FilesSent);
    }

    /// <summary>The user's apps for Info.plist; a phone that won't list them is backed up anyway.</summary>
    private async Task<IReadOnlyList<PlistDict>> ListAppsAsync(CancellationToken ct)
    {
        try
        {
            await using var proxy = await connector.StartServiceAsync(ServiceNames.InstallationProxy, ct).ConfigureAwait(false);
            return await InstallationProxyClient.BrowseAsync(proxy, "User", ct, InstallationProxyClient.LightAttributes).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported or DeviceErrorCodes.Protocol)
        {
            return [];
        }
    }

    /// <summary>Runs PC-side file work; its failures name the folder instead of looking like a dropped cable.</summary>
    private static void LocalDisk(Action action)
    {
        try
        {
            action();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "DeviceDeck can't write to the backup folder: " + ex.Message, ex);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 0x70 or 0x27)
        {
            throw new DeviceException(BackupErrors.NoSpace, "The backup folder's drive is full.", ex);
        }
        catch (IOException ex)
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "DeviceDeck can't write to the backup folder: " + ex.Message, ex);
        }
    }
}

/// <summary>Settings for iPhone backups that tests shorten.</summary>
public sealed class IosBackupOptions
{
    /// <summary>How long to wait for another app's sync to end before giving up.</summary>
    public TimeSpan SyncLockTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
