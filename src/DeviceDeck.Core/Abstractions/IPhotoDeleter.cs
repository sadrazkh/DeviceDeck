namespace DeviceDeck.Core.Abstractions;

// Round 4 (photodelete): deleting photos item by item, with a reason the page can show when it can't.

/// <summary>
/// Optional: a device that deletes the photos the user confirmed one item at a time and says why it can't when it
/// can't. iPhone: through Windows Portable Devices, the camera connection File Explorer uses. A device that implements
/// it reports <see cref="DeviceCapability.PhotosDelete"/> only while <see cref="GetDeleteSupportAsync"/> says it can.
/// </summary>
public interface IPhotoDeleter
{
    /// <summary>Whether deleting works right now, and if not, why. <paramref name="recheck"/> asks the phone again.</summary>
    Task<PhotoDeleteSupport> GetDeleteSupportAsync(bool recheck, CancellationToken ct);

    /// <summary>
    /// Deletes exactly these assets, each at the size the user confirmed. Every item is matched first and nothing is
    /// deleted unless all of them match; then they go in small batches, stopping at the first mismatch or error.
    /// Returns one result per item, in order.
    /// <para>
    /// <paramref name="ct"/> cancels outright (OperationCanceledException) until the first batch is sent; after that it
    /// stops before the next batch, the rest say not tried, and what went is still checked and returned.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<PhotoDeleteResult>> DeleteConfirmedAsync(IReadOnlyList<PhotoDeleteItem> items, IPhotoDeleteObserver? observer, CancellationToken ct);
}

/// <summary>Why photos can't be deleted from the PC right now.</summary>
public enum PhotoDeleteBlock
{
    None,
    /// <summary>The phone is being asked; try again in a moment.</summary>
    Checking,
    /// <summary>The phone marks its photos read-only for the PC and iCloud Photos is on (the usual cause).</summary>
    ICloudPhotos,
    /// <summary>The phone marks its photos read-only for the PC, with no sign of iCloud Photos.</summary>
    ReadOnly,
    /// <summary>The phone is locked, so its camera storage looks empty to Windows.</summary>
    Locked,
    /// <summary>Windows doesn't list the phone as a camera (This PC › Apple iPhone).</summary>
    NotCamera,
    /// <summary>The phone converts photos for the PC ("Transfer to Mac or PC: Automatic"), so names and sizes don't match its files.</summary>
    Converted,
    /// <summary>This phone or this connection can't delete photos from a PC at all.</summary>
    Unsupported,
}

public enum PhotoDeleteDestination
{
    /// <summary>Not known for this phone yet.</summary>
    Unknown,
    /// <summary>The Photos app's Recently Deleted album: back for 30 days.</summary>
    RecentlyDeleted,
    /// <summary>Gone from the phone.</summary>
    Permanent,
}

/// <param name="Reason"><see cref="PhotoDeleteBlock.None"/> when <paramref name="CanDelete"/>.</param>
/// <param name="Detail">One technical sentence of what the check saw, for a "details" fold; never photo content.</param>
/// <param name="GoesTo">Where deleted photos end up.</param>
/// <param name="Observed">GoesTo comes from a delete on this phone in this session, not from what users report.</param>
/// <param name="ICloudPhotos">The library shows signs of iCloud Photos: a delete then reaches iCloud and the other devices too. Null when unknown.</param>
public sealed record PhotoDeleteSupport(
    bool CanDelete,
    PhotoDeleteBlock Reason,
    string? Detail,
    PhotoDeleteDestination GoesTo,
    bool Observed,
    DateTimeOffset? CheckedAt,
    bool? ICloudPhotos = null)
{
    public static PhotoDeleteSupport No(PhotoDeleteBlock reason, string? detail = null) =>
        new(false, reason, detail, PhotoDeleteDestination.Unknown, false, DateTimeOffset.Now);
}

/// <summary>An asset the user confirmed, with the size the page showed for it.</summary>
/// <param name="Name">v0.6: its file name as the page showed it, so the job is listed at once without reading the library.</param>
public sealed record PhotoDeleteItem(string Id, long Size, string? Name = null);

public enum PhotoDeleteOutcome
{
    Deleted,
    /// <summary>It didn't match exactly what the phone lists (changed, gone, or ambiguous), so it was left alone.</summary>
    Mismatch,
    /// <summary>The phone refused or the delete failed.</summary>
    Failed,
    /// <summary>Not tried: an earlier item didn't match or failed, so the delete stopped.</summary>
    NotTried,
}

/// <param name="Size">The bytes it frees: the file plus <paramref name="Also"/>.</param>
/// <param name="Code">A <see cref="DeviceErrorCodes"/> code when it wasn't deleted.</param>
/// <param name="Message">One plain sentence when it wasn't deleted, or a note about it when it was.</param>
/// <param name="Also">Files that went with it, such as a Live Photo's video ("IMG_0001.MOV").</param>
/// <param name="WentTo">Where it went, when the phone showed it.</param>
public sealed record PhotoDeleteResult(
    string Id,
    string FileName,
    long Size,
    PhotoDeleteOutcome Outcome,
    string? Code,
    string? Message,
    IReadOnlyList<string> Also,
    PhotoDeleteDestination WentTo);
