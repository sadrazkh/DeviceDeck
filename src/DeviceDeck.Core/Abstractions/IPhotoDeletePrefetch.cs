namespace DeviceDeck.Core.Abstractions;

// v0.4.2 (deletejob2): a delete from an iPhone first reads Windows' view of its camera roll, about 25-50 ms an object
// the first time (90 s for 3,217 on the owner's iPhone 16), and Windows keeps what it read until the phone is unplugged.
// Reading it ahead, while the user is still choosing, takes that wait out of the delete.

/// <summary>Optional: a device whose delete reads something slow first and can read it ahead in the background.</summary>
public interface IPhotoDeletePrefetch
{
    /// <summary>
    /// Starts reading ahead, read-only and at low priority, unless the phone can't delete now, it is already read or
    /// being read, or a delete runs. Returns at once; never deletes anything. <paramref name="reason"/> goes to the log.
    /// </summary>
    void PrefetchForDelete(string reason);
}
