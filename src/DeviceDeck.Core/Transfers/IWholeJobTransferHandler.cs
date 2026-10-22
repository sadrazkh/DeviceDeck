namespace DeviceDeck.Core.Transfers;

/// <summary>
/// Optional (v0.4.1), for a job that runs as one piece in stages instead of item by item: deleting photos, where every
/// item is matched before anything goes and a cancel stops between batches. The queue calls <see cref="RunJobAsync"/>
/// once per run instead of <see cref="ITransferHandler.RunItemAsync"/> per item. Such a job:
/// <list type="bullet">
/// <item>runs one at a time per phone, beside the copy workers (it may wait for copy jobs it queued itself);</item>
/// <item>can't be paused, resumed or retried: running it again needs a new confirmation;</item>
/// <item>never waits for its phone: a phone that goes away (or isn't there when its turn comes) fails it;</item>
/// <item>found unfinished when the queue starts (DeviceDeck closed mid-run) is ended by <see cref="Restarted"/>, never run again.</item>
/// </list>
/// </summary>
public interface IWholeJobTransferHandler
{
    /// <summary>
    /// Runs the job. Throw to stop it: an OperationCanceledException after a cancel ends it cancelled, anything else
    /// failed. Before throwing, settle every item reported as running (the queue doesn't know what happened to it).
    /// </summary>
    Task RunJobAsync(TransferWorkContext context, ITransferJobReporter report, CancellationToken ct);

    /// <summary>
    /// A job of this kind DeviceDeck closed in the middle of: set its state (finished), its items and its error. Called
    /// once, as the queue starts; the queue saves the job and its items afterwards.
    /// </summary>
    void Restarted(StoredJob job);
}

/// <summary>What a whole-job handler tells the queue while it runs. Item states are saved at once.</summary>
public interface ITransferJobReporter
{
    /// <summary>The stage and how far it got (shown in the Transfers drawer; at most 10 updates a second).</summary>
    void Stage(string name, int done, int total);

    /// <summary>
    /// One item's state as known now; <paramref name="code"/> and <paramref name="message"/> say why it failed (or a note);
    /// <paramref name="size"/> corrects its bytes (a deleted Live Photo freed its video's too).
    /// </summary>
    void Item(int index, TransferItemState state, string? code = null, string? message = null, long? size = null);

    /// <summary>The error the job ends with (failed when nothing worked); null clears it.</summary>
    void Error(TransferError? error);
}
