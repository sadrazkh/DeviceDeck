namespace DeviceDeck.Core.Abstractions;

// Optional: implemented by an IPhotoLibrary that can read part of an original. The photo backup compares a sample of a
// phone's photo (its first and last bytes) with a file of the same size and name already in the destination before
// copying the whole photo, so after "forget history" (or under a new device id) a library already on the PC isn't
// downloaded again.

/// <summary>Reads part of a photo or video off the phone.</summary>
public interface IPhotoRangeReader
{
    /// <summary>Up to <paramref name="count"/> bytes from <paramref name="offset"/>; fewer only at the end of the file.</summary>
    Task<byte[]> ReadRangeAsync(string assetId, long offset, int count, CancellationToken ct);
}
