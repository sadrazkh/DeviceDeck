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

    // Backup (round 2, backup session)
    public const int MaxBackupKeep = 20;

    /// <summary>Where backups go: per iPhone Apple's layout plus dated copies, per Android backup one folder.</summary>
    public string BackupFolder { get; init; } = DefaultBackupFolder();

    /// <summary>Backups of each phone to keep; older ones are offered for deletion (never deleted unasked).</summary>
    public int BackupKeep { get; init; } = 3;

    public static string DefaultBackupFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DeviceDeck", "Backups");

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
