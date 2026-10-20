using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Backups;

// The bridge shapes of docs/bridge-contract.md, "Backup" (camelCase JSON, enums as camelCase strings).

public enum BackupSource
{
    /// <summary>In DeviceDeck's backup folder: DeviceDeck made it and may delete it after the user confirms.</summary>
    DeviceDeck,
    /// <summary>In iTunes' or Apple Devices' folder: listed for browsing and restoring, never changed.</summary>
    Apple,
}

public enum BackupState
{
    Complete,
    /// <summary>A run that stopped part-way (cable, app closed). Backing up again continues it.</summary>
    Incomplete,
    /// <summary>A backup of this phone is running into it now.</summary>
    InProgress,
}

/// <param name="Id">Opaque; pass it back to the other backup methods.</param>
/// <param name="DeviceId">The phone it came from (UDID or adb serial), connected or not.</param>
/// <param name="SizeBytes">What the folder holds. Copies kept by DeviceDeck share unchanged files, so they use less disk than the sum.</param>
/// <param name="Locked">Encrypted and not unlocked with its password in this session.</param>
public sealed record BackupInfo(
    string Id,
    string DeviceId,
    string DeviceName,
    DevicePlatform Platform,
    string? Model,
    string? MarketingName,
    string? OsVersion,
    DateTimeOffset? CreatedAt,
    long SizeBytes,
    bool Encrypted,
    BackupState State,
    BackupSource Source,
    string Path,
    int Apps,
    bool Locked);

/// <param name="Folder">Where new backups go (Settings → backupFolder).</param>
/// <param name="Keep">How many backups of each phone Settings asks to keep.</param>
/// <param name="KeepsHistory">False when the folder's drive can't hard-link (exFAT, FAT32, network): each backup then replaces the last.</param>
/// <param name="FreeBytes">Free space on the folder's drive.</param>
/// <param name="Encrypted">iPhone: the phone encrypts its backups. Null for Android or when the phone didn't say.</param>
/// <param name="CloudBackupEnabled">iPhone: the phone also backs up to iCloud.</param>
/// <param name="NeedsHelperApp">Android: contacts and messages need a helper app on the phone (not in this version).</param>
public sealed record BackupDeviceInfo(
    string DeviceId,
    DevicePlatform Platform,
    string Folder,
    int Keep,
    bool KeepsHistory,
    long? FreeBytes,
    bool? Encrypted,
    bool? CloudBackupEnabled,
    bool NeedsHelperApp,
    BackupInfo? LastBackup);

public enum BackupJobKind { Backup, Restore, Encryption, Extract, Export }

public enum BackupJobState { Queued, Running, WaitingForDevice, Done, Failed, Cancelled }

/// <summary>Older backups beyond Settings' keep count, offered for deletion when a backup finishes. Nothing goes until the user confirms.</summary>
/// <param name="FreedBytes">What deleting them frees: files no kept backup shares.</param>
public sealed record PrunePlan(string DeviceId, int Keep, IReadOnlyList<BackupInfo> Backups, long FreedBytes);

/// <param name="Percent">0–100: the phone's estimate (iPhone backup and restore) or files done of all files.</param>
/// <param name="BackupId">The backup made, restored or read.</param>
/// <param name="Destination">Where an extract or export writes, on the PC.</param>
/// <param name="Prune">Set on a finished backup when older backups of the phone exceed the keep count.</param>
public sealed record BackupJob(
    string Id,
    string? DeviceId,
    string DeviceName,
    BackupJobKind Kind,
    string Title,
    BackupJobState State,
    BackupPhase? Phase,
    double? Percent,
    long Bytes,
    int Files,
    long? TotalBytes,
    int? TotalFiles,
    long? BytesPerSecond,
    string? BackupId,
    string? Destination,
    PrunePlan? Prune,
    BackupJobError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

public sealed record BackupJobError(string Code, string Message);

public enum BackupDomainKind { App, AppGroup, AppPlugin, System }

/// <param name="Domain">The domain as the backup names it ("HomeDomain", "AppDomain-com.example.app").</param>
/// <param name="AppId">The app's bundle id for app domains.</param>
/// <param name="Name">The app's name when known; otherwise the domain or group id.</param>
public sealed record BackupDomain(string Domain, BackupDomainKind Kind, string? AppId, string Name, int Files, long Bytes);

/// <summary>One file or folder to extract: a domain and a path in it ("" = the whole domain).</summary>
public sealed record BackupItemRef(string Domain, string Path);

public enum ContactsFormat { VCard, Csv }

public enum MessagesFormat { Html, Pdf }

/// <summary>What a delete call removed.</summary>
public sealed record BackupDeleteResult(int Deleted, long FreedBytes);
