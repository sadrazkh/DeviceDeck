using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Cleanup;

// The shapes of docs/bridge-contract.md, "Clean-up". Nothing here deletes anything: a report only lists,
// and every delete names the exact items (and their sizes) the user confirmed.

/// <summary>The sections of an analysis, in the order they run.</summary>
public enum CleanupSection { Storage, Apps, Media, Caches, AppFiles, Duplicates }

public enum CleanupState { Idle, Running, Done, Failed, Cancelled }

/// <param name="Done">Items or bytes done in the running section (bytes for duplicates).</param>
public sealed record CleanupProgress(CleanupSection Section, long Done, long Total);

/// <summary>A section that could not be read, or was only partly read.</summary>
public sealed record CleanupNote(CleanupSection Section, string Code, string Message);

/// <param name="Largest">User apps, biggest (app + data) first.</param>
/// <param name="CanUninstall">The phone lets the PC remove apps.</param>
/// <param name="CanCopyFirst">The app itself can be saved to the PC before it goes (Android: the APK, not its data).</param>
public sealed record CleanupApps(IReadOnlyList<AppInfo> Largest, int Count, long TotalBytes, bool CanUninstall, bool CanCopyFirst);

/// <param name="CanDelete">
/// The phone's media can be deleted from the PC safely: Android's MediaStore, or an iPhone whose camera connection
/// (Windows Portable Devices) allows it (round 4). Otherwise: copy, then delete in Photos.
/// </param>
/// <param name="CloudOnlyCount">iPhone originals that are in iCloud only: they take no room and are left out.</param>
public sealed record CleanupMedia(
    IReadOnlyList<PhotoAsset> LargestVideos,
    IReadOnlyList<PhotoAsset> LargestPhotos,
    int VideoCount,
    long VideoBytes,
    int PhotoCount,
    long PhotoBytes,
    int CloudOnlyCount,
    bool CanDelete);

/// <summary>Byte-for-byte copies of one photo or video. The first asset is the oldest; the others are the extra copies.</summary>
public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<PhotoAsset> Assets)
{
    public long ReclaimableBytes => Size * Math.Max(0, Assets.Count - 1);
}

/// <param name="ReclaimableBytes">What deleting every extra copy would free.</param>
/// <param name="HashedCount">Files that had a twin in size and were hashed to be sure.</param>
public sealed record CleanupDuplicates(IReadOnlyList<DuplicateGroup> Groups, long ReclaimableBytes, int HashedCount, long HashedBytes, bool CanDelete);

/// <param name="MinBytes">Only files at least this big are listed.</param>
/// <param name="AppNames">Display names of the apps the files belong to.</param>
public sealed record CleanupAppFiles(IReadOnlyList<AppFileHit> Files, long TotalBytes, long MinBytes, IReadOnlyDictionary<string, string> AppNames, bool CanDelete);

/// <param name="Name">The app's display name, when known.</param>
public sealed record CleanupCacheApp(string AppId, string? Name, long Bytes);

/// <param name="TotalBytes">Null when the phone hasn't counted yet; trimming still works.</param>
/// <param name="TrimmedBytes">Set after a trim in this session: what it freed.</param>
public sealed record CleanupCaches(long? TotalBytes, IReadOnlyList<CleanupCacheApp> Apps, long? TrimmedBytes, DateTimeOffset? TrimmedAt);

/// <summary>What an analysis found. Sections are null until they have run, or when the phone can't do them.</summary>
public sealed record CleanupReport(
    string DeviceId,
    DevicePlatform Platform,
    DateTimeOffset StartedAt,
    long? TotalBytes,
    long? FreeBytes,
    CleanupApps? Apps,
    CleanupMedia? Media,
    CleanupCaches? Caches,
    CleanupAppFiles? AppFiles,
    CleanupDuplicates? Duplicates,
    IReadOnlyList<CleanupNote> Notes);

/// <summary>The clean-up state of one phone, as the page sees it.</summary>
public sealed record CleanupStatus(
    string DeviceId,
    CleanupState State,
    CleanupProgress? Progress,
    CleanupReport? Report,
    TransferError? Error,
    DateTimeOffset? FinishedAt);

/// <summary>A photo, video or app the user confirmed, with the size the page showed for it.</summary>
public sealed record CleanupItem(string Id, long Size);

/// <summary>A file the user confirmed, with the size the page showed for it.</summary>
public sealed record CleanupFileItem(string Root, string Path, long Size);

/// <param name="Skipped">Items left alone because they changed or disappeared since the analysis.</param>
/// <param name="CopyJobIds">The copy-to-PC jobs that ran first.</param>
/// <param name="CopyIncomplete">The copy didn't finish, so nothing was deleted.</param>
/// <remarks>Photos and videos (v0.4.1) are deleted by a transfer job instead: cleanup.deleteMedia answers PhotoDeleteStarted.</remarks>
public sealed record CleanupResult(int Deleted, int Skipped, long FreedBytes, IReadOnlyList<string> CopyJobIds, bool CopyIncomplete);
