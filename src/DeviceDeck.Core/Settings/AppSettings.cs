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

        if (!Enum.IsDefined(Language) || !Enum.IsDefined(Theme) || !Enum.IsDefined(Density) || !Enum.IsDefined(ConflictRule))
        {
            throw new ArgumentException("A settings value is out of range.");
        }
    }
}
