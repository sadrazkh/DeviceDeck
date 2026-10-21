using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Photos;

// The photos.backup* shapes of docs/bridge-contract.md ("Photos backup").

/// <summary>How one phone is backed up. Each value falls back to Settings when the phone has none of its own.</summary>
/// <param name="Range">Which photos, by the date they were taken. The phone's own only (Settings has none): everything by default.</param>
/// <param name="SaveEdited">An iPhone photo edited in Photos also gets its edited version, "IMG_1234 (edited).jpg", kept up to date. Off by default.</param>
public sealed record PhotoBackupOptions(string Destination, string FolderPattern, bool ConvertHeic, bool IncludeHidden, PhotoBackupRange Range,
    bool SaveEdited = false);

public enum PhotoBackupRangeKind { All, LastDays, Since }

/// <summary>
/// Which photos a backup looks at, by the date they were taken: everything, the last <see cref="Days"/> days, or the
/// ones taken on or after <see cref="Since"/> (from midnight on this PC). A photo without any date is always included.
/// </summary>
public sealed record PhotoBackupRange(PhotoBackupRangeKind Kind, int? Days = null, DateOnly? Since = null)
{
    public const int MaxDays = 36_500;

    public static PhotoBackupRange Everything { get; } = new(PhotoBackupRangeKind.All);

    public static PhotoBackupRange LastDays(int days) => new(PhotoBackupRangeKind.LastDays, days);

    public static PhotoBackupRange From(DateOnly since) => new(PhotoBackupRangeKind.Since, Since: since);

    /// <summary>The same range with only the field its kind uses. Throws <see cref="ArgumentException"/> when that field is missing or out of range.</summary>
    public PhotoBackupRange Validated() => Kind switch
    {
        PhotoBackupRangeKind.All => Everything,
        PhotoBackupRangeKind.LastDays when Days is >= 1 and <= MaxDays => LastDays(Days.Value),
        PhotoBackupRangeKind.LastDays => throw new ArgumentException($"range.days must be between 1 and {MaxDays}."),
        PhotoBackupRangeKind.Since when Since is { } since => From(since),
        PhotoBackupRangeKind.Since => throw new ArgumentException("range.since must be a date (yyyy-MM-dd)."),
        _ => throw new ArgumentException("range.kind must be \"all\", \"lastDays\" or \"since\"."),
    };

    /// <summary>The oldest moment a photo can be taken at to be in range; null for everything.</summary>
    public DateTimeOffset? Cutoff(DateTimeOffset now, TimeZoneInfo zone)
    {
        switch (Kind)
        {
            case PhotoBackupRangeKind.LastDays:
                return now - TimeSpan.FromDays(Days ?? 30);
            case PhotoBackupRangeKind.Since when Since is { } since:
                var midnight = since.ToDateTime(TimeOnly.MinValue);
                if (zone.IsInvalidTime(midnight))
                {
                    midnight = midnight.AddHours(1); // the clocks skipped midnight that day
                }

                return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
            default:
                return null;
        }
    }

    /// <summary>True when a photo taken at <paramref name="takenAt"/> is in range; an undated one always is.</summary>
    public static bool Includes(DateTimeOffset? cutoff, DateTimeOffset? takenAt) => cutoff is null || takenAt is null || takenAt >= cutoff;
}

/// <summary>Whether this PC can turn HEIC into JPEG: Windows needs the HEIF and HEVC extensions from the Microsoft Store.</summary>
public enum HeicConversion { Available, MissingCodec, Unknown }

/// <summary>What a backup would copy now. Counts leave out what is already on the PC.</summary>
/// <param name="NewCount">Photos and videos on the phone that were never backed up (Live Photos count once).</param>
/// <param name="NewBytes">Their size where the phone already said it (an iPhone's newest few may show 0 until a backup starts).</param>
/// <param name="CloudOnly">New ones whose original is only in iCloud: not copyable over USB, not in <paramref name="NewCount"/>.</param>
/// <param name="HiddenLeftOut">New ones in the iPhone's Hidden album, left out unless the options include it.</param>
/// <param name="OutOfRange">New ones taken before the options' date range: left out, in no other count.</param>
/// <param name="NewEdits">Edited versions to copy (new photos' and new edits of photos already copied), when the options save them.</param>
public sealed record PhotoBackupCount(string DeviceId, int NewCount, int NewPhotos, int NewVideos, long NewBytes, int CloudOnly, int HiddenLeftOut,
    int OutOfRange = 0, int NewEdits = 0);

public enum PhotoBackupRunState { Running, Done, Failed, Cancelled }

/// <summary>"When this phone connects": nothing, a notification of its new photos (the default), or a backup that starts by itself.</summary>
public enum PhotoBackupOnConnect { Nothing, Notify, BackUp }

/// <summary>What started a run: the panel, the "Back up now" notification, or the phone connecting. The last two end with a notification.</summary>
public enum PhotoBackupTrigger { Manual, Notification, Automatic }

/// <param name="Copied">Items written to the PC this run.</param>
/// <param name="Duplicates">Items that were already in the destination (same size and SHA-256), now remembered.</param>
/// <param name="KeptHeic">HEIC files kept as HEIC because this PC couldn't convert them.</param>
/// <param name="Edited">Edited versions written (new or updated).</param>
/// <param name="EditsMissing">Photos the phone says were edited whose edited version it doesn't have where Photos keeps them.</param>
public sealed record PhotoBackupRun(
    string JobId,
    string Destination,
    PhotoBackupRunState State,
    int Planned,
    int Copied,
    int Duplicates,
    int Failed,
    long Bytes,
    int KeptHeic,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    PhotoBackupTrigger Trigger = PhotoBackupTrigger.Manual,
    int Edited = 0,
    int EditsMissing = 0);

/// <param name="Customized">The phone has options of its own; false = the Settings defaults.</param>
/// <param name="BackedUp">Photos this PC remembers copying from the phone.</param>
/// <param name="ActiveJobId">The backup job still queued, running, paused or waiting, if any.</param>
/// <param name="Space">Room for the new photos on the destination's drive; a backup is refused when it isn't enough.</param>
public sealed record PhotoBackupStatus(
    string DeviceId,
    PhotoBackupCount Count,
    PhotoBackupOptions Options,
    bool Customized,
    int BackedUp,
    PhotoBackupRun? LastRun,
    string? ActiveJobId,
    HeicConversion HeicConversion,
    PhotoBackupSpace Space,
    PhotoBackupOnConnect OnConnect = PhotoBackupOnConnect.Notify);

/// <summary>The transfer request of a photosBackup job: the options as they were when it started.</summary>
/// <param name="Range">Null in jobs queued before ranges existed: everything.</param>
public sealed record PhotoBackupRequest(string Destination, string FolderPattern, bool ConvertHeic, bool IncludeHidden, PhotoBackupRange? Range = null,
    PhotoBackupTrigger Trigger = PhotoBackupTrigger.Manual, bool SaveEdited = false);

/// <summary>The transfer request of a photosToApp job: PC pictures and videos into an iPhone app's Documents.</summary>
public sealed record PhotosToAppRequest(string AppId, IReadOnlyList<string> Files);

/// <summary>Leftover temp files of the photo backup that were removed, and the space they took.</summary>
public sealed record PhotoBackupCleanup(IReadOnlyList<string> Files, long Bytes);

/// <summary>Raised once per finished photosBackup job (done, failed or cancelled).</summary>
public sealed record PhotoBackupFinished(string DeviceId, PhotoBackupRun Run, TransferJob Job);
