using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Photos;

// The photos.backup* shapes of docs/bridge-contract.md ("Photos backup").

/// <summary>How one phone is backed up. Each value falls back to Settings when the phone has none of its own.</summary>
public sealed record PhotoBackupOptions(string Destination, string FolderPattern, bool ConvertHeic, bool IncludeHidden);

/// <summary>Whether this PC can turn HEIC into JPEG: Windows needs the HEIF and HEVC extensions from the Microsoft Store.</summary>
public enum HeicConversion { Available, MissingCodec, Unknown }

/// <summary>What a backup would copy now. Counts leave out what is already on the PC.</summary>
/// <param name="NewCount">Photos and videos on the phone that were never backed up (Live Photos count once).</param>
/// <param name="NewBytes">Their size where the phone already said it (an iPhone's newest few may show 0 until a backup starts).</param>
/// <param name="CloudOnly">New ones whose original is only in iCloud: not copyable over USB, not in <paramref name="NewCount"/>.</param>
/// <param name="HiddenLeftOut">New ones in the iPhone's Hidden album, left out unless the options include it.</param>
public sealed record PhotoBackupCount(string DeviceId, int NewCount, int NewPhotos, int NewVideos, long NewBytes, int CloudOnly, int HiddenLeftOut);

public enum PhotoBackupRunState { Running, Done, Failed, Cancelled }

/// <param name="Copied">Items written to the PC this run.</param>
/// <param name="Duplicates">Items that were already in the destination (same size and SHA-256), now remembered.</param>
/// <param name="KeptHeic">HEIC files kept as HEIC because this PC couldn't convert them.</param>
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
    DateTimeOffset? FinishedAt);

/// <param name="Customized">The phone has options of its own; false = the Settings defaults.</param>
/// <param name="BackedUp">Photos this PC remembers copying from the phone.</param>
/// <param name="ActiveJobId">The backup job still queued, running, paused or waiting, if any.</param>
public sealed record PhotoBackupStatus(
    string DeviceId,
    PhotoBackupCount Count,
    PhotoBackupOptions Options,
    bool Customized,
    int BackedUp,
    PhotoBackupRun? LastRun,
    string? ActiveJobId,
    HeicConversion HeicConversion);

/// <summary>The transfer request of a photosBackup job: the options as they were when it started.</summary>
public sealed record PhotoBackupRequest(string Destination, string FolderPattern, bool ConvertHeic, bool IncludeHidden);

/// <summary>The transfer request of a photosToApp job: PC pictures and videos into an iPhone app's Documents.</summary>
public sealed record PhotosToAppRequest(string AppId, IReadOnlyList<string> Files);

/// <summary>Raised once per finished photosBackup job (done, failed or cancelled).</summary>
public sealed record PhotoBackupFinished(string DeviceId, PhotoBackupRun Run, TransferJob Job);
