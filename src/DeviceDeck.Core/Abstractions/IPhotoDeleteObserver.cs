namespace DeviceDeck.Core.Abstractions;

// Deleting photos as a transfer job (v0.4.1, deletejob): what a delete tells the job while it runs, so the Transfers
// drawer can show its stage and a bar, and the job can save each item's outcome as soon as it is known.

/// <summary>The stages of a confirmed photo delete, after an optional copy to the PC.</summary>
public enum PhotoDeleteStage
{
    /// <summary>Reading the phone's library and, on an iPhone, Windows' view of its camera roll (objects read of all).</summary>
    Reading,
    /// <summary>Deleting, in batches (items of all).</summary>
    Deleting,
    /// <summary>Checking on the phone what went and where (items of all deleted).</summary>
    Checking,
}

/// <summary>
/// Told about a delete while it runs; called on the delete's own thread, so keep it quick. Every item index is the
/// item's place in the list the user confirmed.
/// </summary>
public interface IPhotoDeleteObserver
{
    /// <summary>The stage and how far it got: <paramref name="done"/> of <paramref name="total"/> (0 of 0 while not known yet).</summary>
    void Stage(PhotoDeleteStage stage, int done, int total);

    /// <summary>These items are being deleted right now (one batch); if DeviceDeck stops before their results, they may be gone.</summary>
    void Deleting(IReadOnlyList<int> items);

    /// <summary>One item's outcome as known now. A "deleted" can still turn into "failed" when the phone shows otherwise afterwards.</summary>
    void Result(int item, PhotoDeleteResult result);
}

/// <summary>An observer that ignores everything.</summary>
public sealed class NoPhotoDeleteObserver : IPhotoDeleteObserver
{
    public static NoPhotoDeleteObserver Instance { get; } = new();

    public void Stage(PhotoDeleteStage stage, int done, int total)
    {
    }

    public void Deleting(IReadOnlyList<int> items)
    {
    }

    public void Result(int item, PhotoDeleteResult result)
    {
    }
}
