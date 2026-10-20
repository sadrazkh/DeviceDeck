using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Settings;

public enum AppLanguage { En, Fa }

public enum AppTheme { System, Light, Dark }

public enum UiDensity { Comfortable, Compact }

/// <summary>The <c>Settings</c> shape of docs/bridge-contract.md, persisted as settings.json.</summary>
public sealed record AppSettings
{
    public const string PatternYearMonth = "yyyy/MM";
    public const string PatternNone = "";

    public AppLanguage Language { get; init; } = AppLanguage.En;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public UiDensity Density { get; init; } = UiDensity.Comfortable;
    public string PhotoExportFolder { get; init; } = DefaultPhotoFolder();

    /// <summary>"yyyy/MM" or "" (all photos in one folder).</summary>
    public string PhotoFolderPattern { get; init; } = PatternYearMonth;

    public bool ConvertHeic { get; init; }
    public string FileCopyFolder { get; init; } = DefaultCopyFolder();
    public ConflictRule ConflictRule { get; init; } = ConflictRule.Ask;

    // Photos backup: the defaults for a phone that has no backup options of its own.

    /// <summary>Each phone backs up into a folder of its own name under this one.</summary>
    public string PhotoBackupFolder { get; init; } = DefaultBackupFolder();

    /// <summary>Date folders: "" or yyyy, MM and dd joined by / - _ . or a space ("yyyy/MM" → 2026\10).</summary>
    public string PhotoBackupPattern { get; init; } = PatternYearMonth;

    public bool PhotoBackupConvertHeic { get; init; }

    public static string DefaultBackupFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DeviceDeck Backup");

    // Backup (round 2, backup session)
    public const int MaxBackupKeep = 20;

    /// <summary>Where backups go: per iPhone Apple's layout plus dated copies, per Android backup one folder.</summary>
    public string BackupFolder { get; init; } = DefaultDeviceBackupFolder();

    /// <summary>Backups of each phone to keep; older ones are offered for deletion (never deleted unasked).</summary>
    public int BackupKeep { get; init; } = 3;

    public static string DefaultDeviceBackupFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeviceDeck", "Backups");

    // Desktop (round 2)

    /// <summary>Closing the window keeps DeviceDeck running in the notification area; Quit is in the tray menu.</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>A Windows notification when a phone connects while the window isn't in front.</summary>
    public bool NotifyOnConnect { get; init; } = true;

    /// <summary>Look for a new version on GitHub Releases in the background and download it (installed copies only).</summary>
    public bool AutoUpdate { get; init; } = true;

    public static string DefaultPhotoFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "DeviceDeck");

    public static string DefaultCopyFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DeviceDeck");

    /// <summary>Throws <see cref="ArgumentException"/> naming the first bad field.</summary>
    public void Validate()
    {
        if (PhotoFolderPattern is not (PatternYearMonth or PatternNone))
        {
            throw new ArgumentException("photoFolderPattern must be \"yyyy/MM\" or \"\".", nameof(PhotoFolderPattern));
        }

        if (string.IsNullOrWhiteSpace(PhotoExportFolder))
        {
            throw new ArgumentException("photoExportFolder must not be empty.", nameof(PhotoExportFolder));
        }

        if (string.IsNullOrWhiteSpace(FileCopyFolder))
        {
            throw new ArgumentException("fileCopyFolder must not be empty.", nameof(FileCopyFolder));
        }

        if (string.IsNullOrWhiteSpace(PhotoBackupFolder))
        {
            throw new ArgumentException("photoBackupFolder must not be empty.", nameof(PhotoBackupFolder));
        }

        if (!Photos.PhotoBackupNaming.IsValidPattern(PhotoBackupPattern))
        {
            throw new ArgumentException("photoBackupPattern must be \"\" or yyyy, MM and dd joined by / - _ . or a space.", nameof(PhotoBackupPattern));
        }

        if (string.IsNullOrWhiteSpace(BackupFolder) || !Path.IsPathFullyQualified(BackupFolder))
        {
            throw new ArgumentException("backupFolder must be a full path on this PC.", nameof(BackupFolder));
        }

        if (BackupKeep is < 1 or > MaxBackupKeep)
        {
            throw new ArgumentException($"backupKeep must be between 1 and {MaxBackupKeep}.", nameof(BackupKeep));
        }

        if (!Enum.IsDefined(Language) || !Enum.IsDefined(Theme) || !Enum.IsDefined(Density) || !Enum.IsDefined(ConflictRule))
        {
            throw new ArgumentException("A settings value is out of range.");
        }
    }
}
