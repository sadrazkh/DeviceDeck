namespace DeviceDeck.Core.Abstractions;

// v0.4.2 (deletejob2): a delete from an iPhone first reads Windows' view of its camera roll, about 25-50 ms an object
// the first time (90 s for 3,217 on the owner's iPhone 16), and Windows keeps what it read until the phone is unplugged.
// Reading it ahead, while the user is still choosing, takes that wait out of the delete. The same listing says, before
// anyone selects, which items Windows doesn't show at all: those can only be deleted on the iPhone.

/// <summary>Optional: a device whose delete reads something slow first and can read it ahead in the background.</summary>
public interface IPhotoDeletePrefetch
{
    /// <summary>
    /// Starts reading ahead, read-only and at low priority, unless the phone can't delete now, it is already read or
    /// being read, or a delete runs. Returns at once; never deletes anything. <paramref name="reason"/> goes to the log.
    /// </summary>
    void PrefetchForDelete(string reason);

    /// <summary>
    /// Which items the PC can't delete because Windows doesn't list them anywhere: known only from a complete reading
    /// of Windows' view (<see cref="PhotoDeleteReachState.Ready"/>); while it is read, how far it got. Starts the
    /// reading ahead when nothing is read. Never guesses.
    /// </summary>
    Task<PhotoDeleteReach> GetDeleteReachAsync(CancellationToken ct);
}

public enum PhotoDeleteReachState
{
    /// <summary>The phone can't delete from the PC now (photos.deleteSupport says why).</summary>
    Unsupported,
    /// <summary>Windows' view is being read: nothing is marked yet.</summary>
    Checking,
    /// <summary>Read completely: <see cref="PhotoDeleteReach.NotListed"/> is every item Windows doesn't list.</summary>
    Ready,
}

/// <param name="Done">Objects read of <paramref name="Total"/> while checking (0 of 0 before the folders are listed).</param>
/// <param name="NotListed">Ready: the asset ids Windows lists nowhere (delete those on the phone); empty otherwise.</param>
public sealed record PhotoDeleteReach(
    string DeviceId,
    PhotoDeleteReachState State,
    int Done,
    int Total,
    IReadOnlyList<string> NotListed,
    DateTimeOffset? CheckedAt);
