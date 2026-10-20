namespace DeviceDeck.Core.Backups;

// Reading backups. Apple's format (Info.plist, Manifest.plist, Manifest.db, files named by SHA-1) needs a plist
// reader, which lives in DeviceDeck.Ios; the host hands its implementation to BackupService. DeviceDeck's own
// Android backups are plain folders, read by FolderArchive in this assembly.

/// <summary>Reads backups in Apple's layout: the folder an iPhone backup writes (iTunes, Apple Devices, DeviceDeck).</summary>
public interface IIosBackupFormat
{
    /// <summary>Info.plist, Manifest.plist and Status.plist of one folder; null when it isn't an iPhone backup.</summary>
    IosBackupHeader? ReadHeader(string folder);

    /// <summary>
    /// Opens a backup for browsing. An encrypted one needs its password: a wrong one throws
    /// <see cref="BackupErrors.WrongPassword"/>. Nothing decrypted is written to disk.
    /// </summary>
    Task<IBackupArchive> OpenAsync(string folder, string? password, CancellationToken ct);

    /// <summary>Checks an encrypted backup's password without reading its manifest; a wrong one throws <see cref="BackupErrors.WrongPassword"/>.</summary>
    Task VerifyPasswordAsync(string folder, string password, CancellationToken ct);
}

/// <param name="DeviceId">The phone the backup came from (its UDID).</param>
/// <param name="Date">When the backup finished (Status.plist), else Info.plist's "Last Backup Date".</param>
/// <param name="Complete">The phone finished it (SnapshotState "finished"); false for an interrupted run.</param>
/// <param name="SnapshotId">Status.plist's UUID: the same in a folder and its hard-linked copy.</param>
/// <param name="AppNames">Bundle id → name, from what the PC wrote into Info.plist when it made the backup.</param>
public sealed record IosBackupHeader(
    string DeviceId,
    string DeviceName,
    string? ProductType,
    string? MarketingName,
    string? ProductVersion,
    bool Encrypted,
    DateTimeOffset? Date,
    bool Complete,
    string? SnapshotId,
    int AppCount,
    IReadOnlyDictionary<string, string> AppNames);

/// <summary>A backup opened for browsing: its files by domain, each readable (decrypted) on its own.</summary>
public interface IBackupArchive : IDisposable
{
    bool Encrypted { get; }

    /// <summary>Every domain with its file count and size, in no particular order.</summary>
    IReadOnlyList<BackupDomainInfo> Domains { get; }

    /// <summary>The direct children of a folder ("" is the domain's top). Folders first, then files, by name.</summary>
    IReadOnlyList<BackupEntry> List(string domain, string path);

    /// <summary>Every file under <paramref name="path"/> (or the file itself), for extraction.</summary>
    IReadOnlyList<BackupEntry> ListFiles(string domain, string path);

    /// <summary>One file or folder, or null.</summary>
    BackupEntry? Find(string domain, string path);

    /// <summary>Copies one file's content out, decrypted. Throws <see cref="BackupErrors.Missing"/> when the backup lacks it.</summary>
    Task CopyToAsync(BackupEntry file, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Where the file sits on disk when it is stored as is (an unencrypted backup), so SQLite can open it in place.</summary>
    string? PlainPath(BackupEntry file);
}

public sealed record BackupDomainInfo(string Domain, int Files, long Bytes);

/// <param name="Path">Inside the domain, '/' separated, no leading slash; "" is the domain itself.</param>
public sealed record BackupEntry(string Domain, string Path, string Name, bool IsDirectory, long Size, DateTimeOffset? Modified);

/// <summary>Error codes a backup operation adds to <see cref="Abstractions.DeviceErrorCodes"/>; data.code on the bridge.</summary>
public static class BackupErrors
{
    public const string WrongPassword = "wrong_password";
    public const string PasswordRequired = "password_required";
    public const string Busy = "busy";
    public const string Missing = "backup_missing";
    public const string Corrupt = "backup_corrupt";
    public const string NoSpace = "no_space";
    public const string Refused = "refused";
}
