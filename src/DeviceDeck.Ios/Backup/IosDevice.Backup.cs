using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Backup;
using DeviceDeck.Ios.Protocol;

namespace DeviceDeck.Ios.Device;

/// <summary>Backups to the PC and restores, over mobilebackup2 (see <see cref="IosBackupRunner"/>).</summary>
public sealed partial class IosDevice : IDeviceBackup
{
    /// <summary>Tests shorten the sync-lock wait.</summary>
    internal IosBackupOptions BackupOptions { get; set; } = new();

    private IosBackupRunner BackupRunner(DeviceConnector connector) => new(connector, Udid, BackupOptions);

    public Task<BackupDeviceState> GetBackupStateAsync(CancellationToken ct) =>
        UseAsync(c => BackupRunner(c).GetStateAsync(ct));

    public Task BackupAsync(string folder, BackupRequest request, IProgress<BackupProgress> progress, CancellationToken ct) =>
        UseAsync(c => BackupRunner(c).BackupAsync(folder, request, progress, ct));

    public Task RestoreAsync(string backupFolder, RestoreRequest request, IProgress<BackupProgress> progress, CancellationToken ct) =>
        UseAsync(c => BackupRunner(c).RestoreAsync(backupFolder, request, progress, ct));

    public Task ChangeEncryptionAsync(string? oldPassword, string? newPassword, IProgress<BackupProgress> progress, CancellationToken ct) =>
        UseAsync(c => BackupRunner(c).ChangeEncryptionAsync(oldPassword, newPassword, progress, ct));
}
