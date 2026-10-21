namespace DeviceDeck.Core.Transfers;

/// <summary>
/// Optional, for a handler whose jobs are many small, independent items (the photo backup). The queue then runs up to
/// <see cref="ParallelItems"/> items of one job at once, saves item states in batches instead of one write per item, and
/// calls <see cref="EndRunAsync"/> once a run of the job stops for any reason, before the job's new state goes out.
/// <para>
/// Items must be safe to run again: after a crash, items whose state was not saved yet run once more, so the handler has
/// to recognise what it already did (the photo backup finds the photo in its ledger or in the destination).
/// </para>
/// </summary>
public interface IBatchingTransferHandler
{
    /// <summary>Items of one job running at once (1–8).</summary>
    int ParallelItems { get; }

    /// <summary>The run behind <paramref name="context"/> has stopped (done, paused, unplugged, cancelled or shutting down): save what is held.</summary>
    Task EndRunAsync(TransferWorkContext context);
}
