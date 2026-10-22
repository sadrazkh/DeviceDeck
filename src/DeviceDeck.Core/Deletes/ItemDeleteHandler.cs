using System.Text.Json;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Serialization;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Deletes;

/// <summary>
/// v0.6: a confirmed delete of files or apps as a whole job (see <see cref="IWholeJobTransferHandler"/>), like a photo
/// delete: "Reading what changed" (Clean-up), "Copying to this PC" (when asked), then "Deleting N of M" one item at a
/// time, each with its result. Starting it reads nothing from the phone, so the confirmation closes at once. A started
/// item always finishes (its result is known); Stop takes effect before the next one. A phone that goes away stops the
/// job; DeviceDeck closing in the middle ends it stopped, and nothing not tried is ever deleted by itself.
/// </summary>
public abstract class ItemDeleteHandler<TRequest, TItem> : TransferHandler<TRequest>, IWholeJobTransferHandler
    where TRequest : class, IDeleteRequest<TItem>
    where TItem : IDeleteItem
{
    /// <summary>Stage names (TransferStage.Name); the delete's own stage is <see cref="DeletingStage"/>.</summary>
    public const string Reading = "reading", Copying = "copying";

    /// <summary>Codes of items left alone: another size (or kind) than confirmed, or no longer on the phone.</summary>
    public const string ChangedCode = "changed", MissingCode = "missing";
    public const string StoppedNote = "Not tried: the delete was stopped.";
    private const string ClosedNote = "Not tried: DeviceDeck closed before the delete got to it.";
    private const string CopyIncompleteNote = "The copy to the PC didn't finish, so nothing was deleted.";

    /// <summary>"deleting", or "removing" for apps.</summary>
    protected abstract string DeletingStage { get; }

    /// <summary>The sentences of an item left alone because it changed since it was chosen, or is gone.</summary>
    protected abstract string ChangedNote { get; }

    protected abstract string MissingNote { get; }

    /// <summary>A delete job finished, with what went (Clean-up takes those items out of its report).</summary>
    public event EventHandler<DeleteFinished<TItem>>? Finished;

    /// <summary>Checks the request against the phone's abilities; bad input throws ArgumentException. Reads nothing from the phone.</summary>
    protected abstract void Validate(IDevice device, TRequest request);

    protected abstract string TitleOf(IDevice device, TRequest request);

    /// <summary>The key (Source) and name of an item in the drawer and its results.</summary>
    protected abstract TransferItemPlan PlanOf(TItem item);

    /// <summary>Clean-up: which items are still on the phone as the analysis saw them.</summary>
    protected abstract Task<ItemCheck[]> StillTheSameAsync(TransferWorkContext context, TRequest request, CancellationToken ct);

    /// <summary>Queues the copies to the PC of these items, adding each job to <paramref name="jobs"/> as it is queued.</summary>
    protected abstract Task EnqueueCopiesAsync(TransferQueue queue, IDevice device, IReadOnlyList<TItem> items, string folder, List<string> jobs, CancellationToken ct);

    /// <summary>
    /// Deletes one item. <see cref="ItemLeftAloneException"/> (or a DeviceException "path not found") leaves it alone;
    /// another DeviceException or an IOException fails it. Either way the job goes on with the next one.
    /// </summary>
    protected abstract Task DeleteAsync(TransferWorkContext context, TItem item, CancellationToken ct);

    protected override Task<string> DescribeAsync(IDevice device, TRequest request, CancellationToken ct)
    {
        var items = request.Items ?? throw new ArgumentException("items is missing.");
        if (items.Count == 0)
        {
            throw new ArgumentException("items is empty.");
        }

        if (items.Any(i => i.Size < 0))
        {
            throw new ArgumentException("A size is negative.");
        }

        if (items.Select(i => PlanOf(i).Source).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ArgumentException("An item is listed twice.");
        }

        if (items.Sum(i => i.Size) != request.ExpectedBytes)
        {
            throw new ArgumentException($"expectedBytes ({request.ExpectedBytes}) isn't the total of the items ({items.Sum(i => i.Size)}).");
        }

        if (request.CopyFirstTo is { } folder && !Path.IsPathFullyQualified(folder))
        {
            throw new ArgumentException("copyFirstTo must be a full folder path on this PC.");
        }

        Validate(device, request);
        return Task.FromResult(TitleOf(device, request));
    }

    protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, TRequest request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TransferItemPlan>>(request.Items.Select(PlanOf).ToList());

    protected override Task<TransferItemState> RunItemAsync(TransferWorkContext context, TRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct) =>
        throw new InvalidOperationException("A delete runs as a whole (RunJobAsync).");

    public async Task RunJobAsync(TransferWorkContext context, ITransferJobReporter report, CancellationToken ct)
    {
        var request = context.Request.Deserialize<TRequest>(DeviceDeckJson.Options) ?? throw new ArgumentException("The delete request is empty.");
        var items = request.Items;
        var settled = new bool[items.Count];
        var running = -1;
        var gone = new List<TItem>();
        var missing = new List<TItem>();
        var copyJobs = new List<string>();
        void Settle(int i, TransferItemState state, string? code = null, string? message = null)
        {
            settled[i] = true;
            report.Item(i, state, code, message);
        }

        void LeaveAlone(int i, ItemCheck why, string? message = null)
        {
            if (why == ItemCheck.Missing)
            {
                missing.Add(items[i]);
            }

            Settle(i, TransferItemState.Skipped, why == ItemCheck.Missing ? MissingCode : ChangedCode, message ?? (why == ItemCheck.Missing ? MissingNote : ChangedNote));
        }

        void SettleRest(string why)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (settled[i])
                {
                    continue;
                }

                if (i == running)
                {
                    Settle(i, TransferItemState.Failed, "interrupted", "The delete stopped while this was being deleted: it may be gone from the phone. Look on the phone.");
                }
                else
                {
                    Settle(i, TransferItemState.Queued, null, why);
                }
            }
        }

        try
        {
            var kept = Enumerable.Range(0, items.Count).ToList();
            if (request.SkipChanged)
            {
                report.Stage(Reading, 0, items.Count);
                var checks = await StillTheSameAsync(context, request, ct).ConfigureAwait(false);
                kept = kept.Where(i => checks[i] == ItemCheck.Same).ToList();
                foreach (var i in Enumerable.Range(0, items.Count).Where(i => checks[i] != ItemCheck.Same))
                {
                    LeaveAlone(i, checks[i]);
                }
            }

            if (kept.Count > 0 && request.CopyFirstTo is { } folder)
            {
                var queue = context.Queue ?? throw new InvalidOperationException("Copying first needs the transfer queue.");
                report.Stage(Copying, 0, kept.Count);
                await EnqueueCopiesAsync(queue, context.Device, kept.Select(i => items[i]).ToList(), folder, copyJobs, ct).ConfigureAwait(false);
                var copied = await PhotoDelete.PhotoDeletes.WaitForCopiesAsync(queue, copyJobs,
                    (done, total) => report.Stage(Copying, done, Math.Max(total, kept.Count)), ct).ConfigureAwait(false);
                if (!copied)
                {
                    SettleRest(CopyIncompleteNote);
                    report.Error(new TransferError("copy_incomplete", CopyIncompleteNote));
                    Finish(context, request, gone, missing);
                    return;
                }
            }

            var failed = new List<(int Index, string Code, string Message)>();
            report.Stage(DeletingStage, 0, kept.Count);
            for (var n = 0; n < kept.Count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var i = kept[n];
                running = i;
                report.Item(i, TransferItemState.Running);
                try
                {
                    // A started item finishes, so its result is known: Stop takes effect before the next one.
                    await DeleteAsync(context, items[i], CancellationToken.None).ConfigureAwait(false);
                    Settle(i, TransferItemState.Done);
                    gone.Add(items[i]);
                }
                catch (ItemLeftAloneException ex)
                {
                    LeaveAlone(i, ex.Why, ex.Message);
                }
                catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.PathNotFound)
                {
                    LeaveAlone(i, ItemCheck.Missing);
                }
                catch (DeviceException ex) when (ex.Code is not (DeviceErrorCodes.Disconnected or DeviceErrorCodes.TrustRequired or DeviceErrorCodes.Unauthorized))
                {
                    Settle(i, TransferItemState.Failed, ex.Code, ex.Message);
                    failed.Add((i, ex.Code, ex.Message));
                }
                catch (IOException ex)
                {
                    Settle(i, TransferItemState.Failed, DeviceErrorCodes.Protocol, ex.Message);
                    failed.Add((i, DeviceErrorCodes.Protocol, ex.Message));
                }

                running = -1;
                report.Stage(DeletingStage, n + 1, kept.Count);
            }

            if (gone.Count == 0 && failed.Count > 0)
            {
                var (index, code, message) = failed[0];
                report.Error(new TransferError(code, $"Nothing was deleted. {PlanOf(items[index]).Name}: {message}"));
            }

            Finish(context, request, gone, missing);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SettleRest(StoppedNote);
            Finish(context, request, gone, missing);
            CancelCopies(context.Queue, copyJobs);
            throw;
        }
        catch (Exception ex)
        {
            // The phone went away, or anything else: what went stays counted; the item in flight may or may not have gone.
            SettleRest(StoppedNote);
            report.Error(TransferQueue.Describe(ex) is { Code: "internal" } ? new TransferError("internal", "The delete stopped: " + ex.Message) : TransferQueue.Describe(ex));
            Finish(context, request, gone, missing);
            CancelCopies(context.Queue, copyJobs);
            throw;
        }
    }

    /// <summary>DeviceDeck closed in the middle of this delete: it ends stopped, and nothing not tried is deleted by itself.</summary>
    public void Restarted(StoredJob job)
    {
        foreach (var item in job.Items)
        {
            switch (item.State)
            {
                case TransferItemState.Running:
                    item.State = TransferItemState.Failed;
                    item.ErrorCode = "interrupted";
                    item.ErrorMessage = "DeviceDeck closed while this was being deleted: it may be gone from the phone. Look on the phone.";
                    break;
                case TransferItemState.Queued:
                    // Settled as "stopped" when the run stopped at shutdown: it was DeviceDeck closing.
                    item.ErrorMessage = item.ErrorMessage is null or StoppedNote ? ClosedNote : item.ErrorMessage;
                    break;
            }
        }

        var interrupted = job.Items.Count(i => i.State == TransferItemState.Failed && i.ErrorCode == "interrupted");
        var deleted = job.Items.Count(i => i.State == TransferItemState.Done);
        var notTried = job.Items.Count(i => i.State == TransferItemState.Queued);
        job.State = TransferState.Cancelled;
        job.FinishedAt ??= DateTimeOffset.Now;
        job.Error = new TransferError("interrupted", deleted == 0 && interrupted == 0
            ? "DeviceDeck closed before anything was deleted. Nothing is deleted by itself: choose them again to delete them."
            : $"DeviceDeck closed before the delete finished: {deleted:N0} deleted, {notTried:N0} not tried"
              + (interrupted > 0 ? $", {interrupted:N0} may be gone" : "") + ". The rest is never deleted by itself.");
    }

    private void Finish(TransferWorkContext context, TRequest request, List<TItem> gone, List<TItem> missing)
    {
        try
        {
            Finished?.Invoke(this, new DeleteFinished<TItem>(context.JobId, context.Device.Summary.Id, request.Source, gone.ToList(), missing.ToList()));
        }
        catch (Exception)
        {
            // A listener's bug must not change how the delete ended.
        }
    }

    private static void CancelCopies(TransferQueue? queue, List<string> jobs)
    {
        foreach (var id in jobs)
        {
            try
            {
                queue?.Cancel(id);
            }
            catch (ArgumentException)
            {
                // Cleared from the list meanwhile.
            }
        }
    }

    /// <summary>"Deleting 3 items from Sadal" or "Deleting notes.txt from Sadal".</summary>
    protected static string Title(string verb, string one, string many, int count, string device) =>
        count == 1 ? $"{verb} {one} from {device}" : $"{verb} {count:N0} {many} from {device}";
}
