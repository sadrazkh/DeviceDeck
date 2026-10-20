namespace DeviceDeck.Core.Abstractions;

// Round 2, backup: an optional capability a device implements next to IDevice.
// iPhone: mobilebackup2 (Apple's DeviceLink protocol) into a folder on the PC, in Apple's own layout,
// so iTunes, Apple Devices and other tools can read what DeviceDeck writes.

/// <summary>
/// Backup and restore through the phone's own backup service. The host checks <c>device is IDeviceBackup</c>
/// together with <see cref="DeviceCapability.Backup"/> and <see cref="DeviceCapability.Restore"/>.
/// </summary>
public interface IDeviceBackup
{
    /// <summary>What the phone says about its backups: is encryption on, does it also back up to iCloud.</summary>
    Task<BackupDeviceState> GetBackupStateAsync(CancellationToken ct);

    /// <summary>
    /// Backs the phone up into <c>{folder}/{device id}</c>. The phone decides what it sends: everything the first time
    /// (or with <see cref="BackupRequest.Full"/>), afterwards what changed since the last complete backup in that folder.
    /// A pulled cable throws <see cref="DeviceErrorCodes.Disconnected"/>; running it again continues from what is on the PC.
    /// </summary>
    Task BackupAsync(string folder, BackupRequest request, IProgress<BackupProgress> progress, CancellationToken ct);

    /// <summary>
    /// Restores one backup onto this phone; the phone restarts at the end. <paramref name="backupFolder"/> is the backup's
    /// own folder (the one holding Manifest.db), which may be a dated copy or come from another phone.
    /// </summary>
    Task RestoreAsync(string backupFolder, RestoreRequest request, IProgress<BackupProgress> progress, CancellationToken ct);

    /// <summary>
    /// Turns backup encryption on (only <paramref name="newPassword"/>), off (only <paramref name="oldPassword"/>) or
    /// changes the password (both). The phone asks for its passcode before it agrees.
    /// </summary>
    Task ChangeEncryptionAsync(string? oldPassword, string? newPassword, IProgress<BackupProgress> progress, CancellationToken ct);
}

/// <param name="Encrypted">Backups to a computer are encrypted with a password kept on the phone.</param>
/// <param name="CloudBackupEnabled">The phone also backs up to iCloud.</param>
public sealed record BackupDeviceState(bool Encrypted, bool CloudBackupEnabled);

/// <param name="Full">Ask the phone for everything, not only what changed.</param>
public sealed record BackupRequest(bool Full = false);

/// <param name="Password">The backup's password, when it is encrypted.</param>
/// <param name="RestoreSettings">Take the phone's settings from the backup too, as iTunes does.</param>
/// <param name="Reboot">Let the phone restart when the restore is done (it needs to, to use the restored data).</param>
public sealed record RestoreRequest(string? Password, bool RestoreSettings = true, bool Reboot = true);

public enum BackupPhase
{
    /// <summary>Connecting to the phone's backup service.</summary>
    Starting,
    /// <summary>The phone shows a prompt (its passcode, to confirm encryption) or another app holds its sync lock.</summary>
    WaitingForPhone,
    /// <summary>The phone is working out what to send.</summary>
    Preparing,
    Transferring,
    Finishing,
}

/// <param name="Percent">The phone's own estimate for the whole run, 0–100, when it gives one.</param>
/// <param name="Bytes">Bytes received from the phone (backup) or sent to it (restore) so far in this run.</param>
/// <param name="Files">Files received or sent so far in this run.</param>
public sealed record BackupProgress(BackupPhase Phase, double? Percent, long Bytes, int Files);
