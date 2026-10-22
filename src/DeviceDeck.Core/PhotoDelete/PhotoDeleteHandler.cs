using System.Collections.Concurrent;
using System.Text.Json;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Serialization;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.PhotoDelete;

// v0.4.1 (deletejob): a confirmed photo delete, from Photos or Clean-up, runs as a transfer job beside the copies:
// "Copying to this PC" (when asked), "Reading the phone's camera roll: N of M", "Deleting N of 39", "Checking on the
// phone", then a result per item. The confirmation dialog closes at once; the Transfers drawer shows the job.

/// <summary>Where a delete was confirmed.</summary>
public enum PhotoDeleteSource { Photos, Cleanup }

/// <summary>A delete job's request, saved with the job.</summary>
/// <param name="Items">Exactly what the user confirmed, each at the size the page showed.</param>
/// <param name="Skipped">Clean-up: items left out before the job because they changed since the analysis.</param>
public sealed record PhotosDeleteRequest(
    IReadOnlyList<PhotoDeleteItem> Items,
    long ExpectedBytes,
    string? CopyFirstTo,
    string FolderPattern,
    PhotoDeleteSource Source = PhotoDeleteSource.Photos,
    int Skipped = 0);

/// <summary>What starting a delete answers: its job (shown in the Transfers drawer).</summary>
/// <param name="JobId">Null when nothing was left to delete (Clean-up: every item changed since the analysis).</param>
/// <param name="Skipped">Clean-up: items left out because they changed since the analysis.</param>
public sealed record PhotoDeleteStarted(string? JobId, int Skipped);

/// <summary>A delete job's results so far (all of them once it finished).</summary>
/// <param name="Items">One result per confirmed item, in order; items not reached yet say notTried.</param>
/// <param name="Stage">Where it is while it runs.</param>
/// <param name="GoesTo">Where the deleted items went, as the phone showed it.</param>
public sealed record PhotoDeleteJobStatus(
    string JobId,
    string DeviceId,
    PhotoDeleteSource Source,
    TransferState State,
    TransferStage? Stage,
    IReadOnlyList<PhotoDeleteResult> Items,
    int Deleted,
    long FreedBytes,
    PhotoDeleteDestination GoesTo,
    IReadOnlyList<string> CopyJobIds,
    bool CopyIncomplete,
    int Skipped,
    TransferError? Error);

/// <summary>A delete job finished (done, failed or stopped) with these items gone.</summary>
public sealed record PhotoDeleteFinished(string JobId, string DeviceId, IReadOnlyList<string> Deleted, long FreedBytes);

/// <summary>
/// Runs <see cref="TransferKind.PhotosDelete"/> jobs (see <see cref="IWholeJobTransferHandler"/>): every rule of the
/// delete itself stays in <see cref="PhotoDeletes.DeleteItemsAsync"/> and the device's <see cref="IPhotoDeleter"/>
/// (everything matched first or nothing goes, batches, stop at the first failure, never folders, a log line per object).
/// The job adds the stages, a result per item saved as it is known, cancelling, and what a restart does: a delete
/// DeviceDeck closed in the middle of ends stopped, with what went and what wasn't tried, and never runs again by itself.
/// </summary>
public sealed class PhotoDeleteHandler : TransferHandler<PhotosDeleteRequest>, IWholeJobTransferHandler
{
    /// <summary>Stage names (TransferStage.Name).</summary>
    public const string Copying = "copying", Reading = "reading", Deleting = "deleting", Checking = "checking";

    /// <summary>What an item says when the delete stopped before it got to it.</summary>
    public const string StoppedNote = "Not tried: the delete was stopped.";

    private const string CopyIncompleteNote = "The copy to the PC didn't finish, so nothing was deleted.";
    private const string InterruptedNote = "The delete stopped while this was being deleted: it may be gone from the phone. Look on the phone.";

    internal sealed class Memory(string deviceId, PhotosDeleteRequest request)
    {
        public string DeviceId { get; } = deviceId;
        public PhotosDeleteRequest Request { get; } = request;

        /// <summary>Each item's name as the library gave it when the job was planned.</summary>
        public string[] Names { get; } = request.Items.Select(i => FileNameOf(i.Id)).ToArray();
        public PhotoDeleteResult?[] Results { get; } = new PhotoDeleteResult?[request.Items.Count];
        public List<string> CopyJobIds { get; } = [];
        public bool CopyIncomplete { get; set; }
        public PhotoDeleteDestination GoesTo { get; set; }
        public bool Finished { get; set; }
    }

    private readonly ConcurrentDictionary<string, Memory> _jobs = new(StringComparer.Ordinal);

    public override TransferKind Kind => TransferKind.PhotosDelete;

    /// <summary>A delete job finished, with what went (Clean-up takes those items out of its report).</summary>
    public event EventHandler<PhotoDeleteFinished>? Finished;

    internal Memory? MemoryOf(string jobId) => _jobs.GetValueOrDefault(jobId);

    protected override async Task<string> DescribeAsync(IDevice device, PhotosDeleteRequest request, CancellationToken ct)
    {
        var items = request.Items ?? throw new ArgumentException("items is missing.");
        if (items.Count == 0)
        {
            throw new ArgumentException("items is empty.");
        }

        if (items.Any(i => i.Size < 0 || string.IsNullOrEmpty(i.Id)))
        {
            throw new ArgumentException("Every item needs an id and a size of 0 or more.");
        }

        if (items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
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

        if (!device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete))
        {
            var support = await PhotoDeletes.SupportOf(device, recheck: false, ct).ConfigureAwait(false);
            throw new DeviceException(DeviceErrorCodes.NotSupported,
                $"{device.Summary.Name} doesn't let the PC delete its photos right now ({support.Reason}).");
        }

        return items.Count == 1
            ? $"Deleting 1 item from {device.Summary.Name}"
            : $"Deleting {items.Count:N0} photos and videos from {device.Summary.Name}";
    }

    /// <summary>One item per confirmed photo, named as the library names it.</summary>
    protected override async Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, PhotosDeleteRequest request, CancellationToken ct)
    {
        Dictionary<string, PhotoAsset> library;
        try
        {
            library = await PhotoDeletes.ReadLibraryAsync(context.Device, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported)
        {
            library = [];
        }

        return request.Items.Select(i => new TransferItemPlan(i.Id, "", library.GetValueOrDefault(i.Id)?.FileName ?? FileNameOf(i.Id), i.Size)).ToList();
    }

    protected override Task<TransferItemState> RunItemAsync(TransferWorkContext context, PhotosDeleteRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct) =>
        throw new InvalidOperationException("A photo delete runs as a whole (RunJobAsync).");

    public async Task RunJobAsync(TransferWorkContext context, ITransferJobReporter report, CancellationToken ct)
    {
        var request = Read(context.Request);
        var device = context.Device;
        var items = request.Items;
        var memory = new Memory(device.Summary.Id, request);
        if (context.Queue?.Details(context.JobId) is { } saved)
        {
            foreach (var item in saved.Items.Where(i => i.Index < memory.Names.Length))
            {
                memory.Names[item.Index] = item.Name;
            }
        }

        _jobs[context.JobId] = memory;
        var observer = new Observer(report, memory);
        try
        {
            if (request.CopyFirstTo is { } folder)
            {
                var queue = context.Queue ?? throw new InvalidOperationException("Copying first needs the transfer queue.");
                report.Stage(Copying, 0, items.Count);
                memory.CopyJobIds.AddRange(await PhotoDeletes.EnqueueCopiesAsync(queue, device, items.Select(i => i.Id).ToList(), folder,
                    request.FolderPattern, ct).ConfigureAwait(false));
                var copied = await PhotoDeletes.WaitForCopiesAsync(queue, memory.CopyJobIds,
                    (done, total) => report.Stage(Copying, done, Math.Max(total, items.Count)), ct).ConfigureAwait(false);
                if (!copied)
                {
                    memory.CopyIncomplete = true;
                    for (var i = 0; i < items.Count; i++)
                    {
                        observer.Result(i, NotTried(i, memory, CopyIncompleteNote));
                    }

                    report.Error(new TransferError("copy_incomplete", CopyIncompleteNote));
                    Finish(context.JobId, memory, PhotoDeleteDestination.Unknown);
                    return;
                }
            }

            var results = await PhotoDeletes.DeleteItemsAsync(device, items, observer, ct).ConfigureAwait(false);
            for (var i = 0; i < results.Count; i++)
            {
                observer.Result(i, results[i]);
            }

            if (results.All(r => r.Outcome != PhotoDeleteOutcome.Deleted) && results.FirstOrDefault(r => r.Outcome is PhotoDeleteOutcome.Mismatch or PhotoDeleteOutcome.Failed) is { } first)
            {
                report.Error(new TransferError(first.Code ?? DeviceErrorCodes.Protocol, $"Nothing was deleted. {first.FileName}: {first.Message}"));
            }

            Finish(context.JobId, memory, PhotoDeletes.WentTo(results, await GoesToAsync(device).ConfigureAwait(false)));

            // A cancel during the deletes stopped them after a batch: the job ends stopped, with what went.
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!memory.Finished)
            {
                // Stopped before anything was deleted (copying or reading).
                Settle(memory, observer, StoppedNote);
                Finish(context.JobId, memory, PhotoDeleteDestination.Unknown);
                CancelCopies(context.Queue, memory);
            }

            throw;
        }
        catch (Exception ex)
        {
            // The phone went away, or anything else: what went stays counted; a batch in flight may or may not have gone.
            Settle(memory, observer, StoppedNote);
            report.Error(TransferQueue.Describe(ex) is { Code: "internal" } ? new TransferError("internal", "The delete stopped: " + ex.Message) : TransferQueue.Describe(ex));
            Finish(context.JobId, memory, PhotoDeleteDestination.Unknown);
            CancelCopies(context.Queue, memory);
            throw;
        }
    }

    /// <summary>
    /// DeviceDeck closed in the middle of this delete: it ends stopped. Items that were being deleted may or may not have
    /// gone (the phone decides); nothing not tried is ever deleted by itself.
    /// </summary>
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
                    item.ErrorMessage ??= "Not tried: DeviceDeck closed before the delete got to it.";
                    break;
            }
        }

        // Settled as the run stopped at shutdown, or just now after a crash.
        var interrupted = job.Items.Count(i => i.State == TransferItemState.Failed && i.ErrorCode == "interrupted");
        var deleted = job.Items.Count(i => i.State == TransferItemState.Done);
        var notTried = job.Items.Count(i => i.State == TransferItemState.Queued);
        job.State = TransferState.Cancelled;
        job.FinishedAt ??= DateTimeOffset.Now;
        job.Error = new TransferError("interrupted", deleted == 0 && interrupted == 0
            ? "DeviceDeck closed before anything was deleted. Nothing is deleted by itself: select the photos again to delete them."
            : $"DeviceDeck closed before the delete finished: {deleted:N0} deleted, {notTried:N0} not tried"
              + (interrupted > 0 ? $", {interrupted:N0} may be gone" : "") + ". The rest is never deleted by itself.");
    }

    /// <summary>A job's results from the queue's saved items, when this run of DeviceDeck didn't run it (after a restart).</summary>
    internal static PhotoDeleteResult FromItem(TransferItem item) => item.State switch
    {
        TransferItemState.Done => new PhotoDeleteResult(item.Source, item.Name, item.Size, PhotoDeleteOutcome.Deleted, null, item.ErrorMessage, [], PhotoDeleteDestination.Unknown),
        TransferItemState.Failed => new PhotoDeleteResult(item.Source, item.Name, item.Size, PhotoDeleteOutcome.Failed, item.ErrorCode, item.ErrorMessage, [], PhotoDeleteDestination.Unknown),
        _ => new PhotoDeleteResult(item.Source, item.Name, item.Size, PhotoDeleteOutcome.NotTried, null, item.ErrorMessage, [], PhotoDeleteDestination.Unknown),
    };

    internal static PhotosDeleteRequest Read(JsonElement json) =>
        json.Deserialize<PhotosDeleteRequest>(DeviceDeckJson.Options) ?? throw new ArgumentException("The delete request is empty.");

    private void Finish(string jobId, Memory memory, PhotoDeleteDestination goesTo)
    {
        memory.GoesTo = goesTo;
        memory.Finished = true;
        var gone = memory.Results.Where(r => r?.Outcome == PhotoDeleteOutcome.Deleted).Select(r => r!).ToList();
        try
        {
            Finished?.Invoke(this, new PhotoDeleteFinished(jobId, memory.DeviceId, gone.Select(r => r.Id).ToList(), gone.Sum(r => r.Size)));
        }
        catch (Exception)
        {
            // A listener's bug must not change how the delete ended.
        }
    }

    /// <summary>Every item without a final result: not tried; one that was being deleted: may be gone.</summary>
    private static void Settle(Memory memory, Observer observer, string why)
    {
        var items = memory.Request.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (memory.Results[i] is null)
            {
                bool inFlight;
                lock (observer.InFlight)
                {
                    inFlight = observer.InFlight.Contains(i);
                }

                observer.Result(i, inFlight
                    ? new PhotoDeleteResult(items[i].Id, memory.Names[i], items[i].Size, PhotoDeleteOutcome.Failed, "interrupted", InterruptedNote, [], PhotoDeleteDestination.Unknown)
                    : NotTried(i, memory, why));
            }
        }
    }

    private static void CancelCopies(TransferQueue? queue, Memory memory)
    {
        foreach (var id in memory.CopyJobIds)
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

    private static async Task<PhotoDeleteDestination> GoesToAsync(IDevice device)
    {
        try
        {
            return (await PhotoDeletes.SupportOf(device, recheck: false, CancellationToken.None).ConfigureAwait(false)).GoesTo;
        }
        catch (Exception ex) when (ex is DeviceException or IOException or InvalidOperationException)
        {
            return PhotoDeleteDestination.Unknown;
        }
    }

    private static PhotoDeleteResult NotTried(int index, Memory memory, string why) =>
        new(memory.Request.Items[index].Id, memory.Names[index], memory.Request.Items[index].Size, PhotoDeleteOutcome.NotTried, null, why, [], PhotoDeleteDestination.Unknown);

    private static string FileNameOf(string id) => id[(id.LastIndexOf('/') + 1)..];

    /// <summary>The delete's stages and results, passed on to the job: saved per item as they come.</summary>
    private sealed class Observer(ITransferJobReporter report, Memory memory) : IPhotoDeleteObserver
    {
        public HashSet<int> InFlight { get; } = [];

        public void Stage(PhotoDeleteStage stage, int done, int total) => report.Stage(stage switch
        {
            PhotoDeleteStage.Reading => PhotoDeleteHandler.Reading,
            PhotoDeleteStage.Deleting => PhotoDeleteHandler.Deleting,
            _ => PhotoDeleteHandler.Checking,
        }, done, total);

        public void Deleting(IReadOnlyList<int> items)
        {
            lock (InFlight)
            {
                InFlight.UnionWith(items);
            }

            foreach (var i in items)
            {
                report.Item(i, TransferItemState.Running);
            }
        }

        public void Result(int item, PhotoDeleteResult result)
        {
            lock (InFlight)
            {
                InFlight.Remove(item);
            }

            // The library's name where the result only knows the id (an Android MediaStore id).
            var named = result.FileName == FileNameOf(result.Id) && memory.Names[item] != result.FileName ? result with { FileName = memory.Names[item] } : result;
            memory.Results[item] = named;
            report.Item(item, named.Outcome switch
            {
                PhotoDeleteOutcome.Deleted => TransferItemState.Done,
                PhotoDeleteOutcome.NotTried => TransferItemState.Queued,
                _ => TransferItemState.Failed,
            }, named.Outcome == PhotoDeleteOutcome.Deleted ? null : named.Code, named.Message,
                named.Outcome == PhotoDeleteOutcome.Deleted ? named.Size : null);
        }
    }
}
