using System.Collections.Concurrent;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.LocalSend;

/// <summary>
/// Photos straight into a phone's Photos app through LocalSend: finding it (cable first, then Wi-Fi), starting the
/// localSend transfer job, a retry with the PIN, and each send's phase and per-file results (localsend.* methods).
/// </summary>
public sealed class LocalSendService : IAsyncDisposable
{
    private readonly DeviceHub _hub;
    private readonly Func<string, IReadOnlyList<TransferItem>> _itemsOf;
    private readonly ConcurrentDictionary<string, (LocalSendPhase Phase, string? Code)> _published = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<(LocalSendUsbState, string?, LocalSendReceiver?)>> _probes = new(StringComparer.Ordinal);
    private TransferQueue? _queue;

    /// <param name="itemsOf">A job's saved items (SqliteTransferStore.LoadItems).</param>
    public LocalSendService(DeviceHub hub, Func<string, IReadOnlyList<TransferItem>> itemsOf, LocalSendIdentity? me = null,
        LocalSendFinderOptions? finder = null, LocalSendTimeouts? timeouts = null)
    {
        _hub = hub;
        _itemsOf = itemsOf;
        Identity = me ?? LocalSendIdentity.ForThisPc();
        Finder = new LocalSendFinder(Identity, finder);
        Handler = new LocalSendHandler(Identity, itemsOf, timeouts);
        Handler.PhaseChanged += (_, jobId) => Changed(jobId);
    }

    public LocalSendIdentity Identity { get; }

    public LocalSendFinder Finder { get; }

    /// <summary>Give it to the transfer queue (TransferKind.LocalSend), then call <see cref="Attach"/>.</summary>
    public LocalSendHandler Handler { get; }

    /// <summary>A send's phase or error changed (localsend.updated; <see cref="LocalSendJobStatus.Files"/> is null).</summary>
    public event EventHandler<LocalSendJobStatus>? Updated;

    /// <summary>
    /// Call before the queue starts: sends left unfinished when DeviceDeck closed are cancelled in the store, so the queue
    /// never runs them again by itself. The phone's side of the session is gone, and a send that started on its own would
    /// put an Accept question on the phone out of the blue. Retry sends the rest.
    /// </summary>
    public static int CancelLeftovers(ITransferStore store)
    {
        var leftovers = store.LoadAll()
            .Where(j => j.Kind == TransferKind.LocalSend && j.State is not (TransferState.Done or TransferState.Failed or TransferState.Cancelled))
            .ToList();
        foreach (var job in leftovers)
        {
            job.State = TransferState.Cancelled;
            job.FinishedAt = DateTimeOffset.Now;
            store.UpsertJob(job);
        }

        return leftovers.Count;
    }

    /// <summary>Joins the queue (after <see cref="CancelLeftovers"/> and the queue's Start).</summary>
    public void Attach(TransferQueue queue)
    {
        _queue = queue;
        queue.JobUpdated += (_, job) =>
        {
            if (job.Kind == TransferKind.LocalSend)
            {
                Changed(job.Id);
            }
        };
    }

    private TransferQueue Queue => _queue ?? throw new InvalidOperationException("LocalSendService isn't attached to the transfer queue.");

    /// <summary>
    /// localsend.discover: is LocalSend answering through the phone's cable, and (with <paramref name="wifi"/>) who answers
    /// on the network. Wi-Fi searches run in the background; ask again to see more.
    /// </summary>
    public async Task<LocalSendDiscovery> DiscoverAsync(string deviceId, bool wifi, CancellationToken ct)
    {
        var device = _hub.GetUsableDevice(deviceId);
        if (wifi)
        {
            Finder.StartWifiSearch();
        }

        var (state, detail, receiver) = await ProbeUsbAsync(device, ct).ConfigureAwait(false);
        if (!wifi && !Finder.WifiSearching)
        {
            return new LocalSendDiscovery(deviceId, state, detail, receiver, [], false, null);
        }

        var (receivers, searching, searchedAt) = Finder.WifiSnapshot(device.Summary);
        return new LocalSendDiscovery(deviceId, state, detail, receiver, receivers, searching, searchedAt);
    }

    // The page polls while its dialog is open; calls that overlap share one probe of the phone.
    private Task<(LocalSendUsbState, string?, LocalSendReceiver?)> ProbeUsbAsync(IDevice device, CancellationToken ct)
    {
        var probe = _probes.GetOrAdd(device.Summary.Id, _ => Task.Run(() => Finder.ProbeUsbAsync(device, CancellationToken.None), CancellationToken.None));
        _ = probe.ContinueWith(_ => _probes.TryRemove(new KeyValuePair<string, Task<(LocalSendUsbState, string?, LocalSendReceiver?)>>(device.Summary.Id, probe)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return probe.WaitAsync(ct);
    }

    /// <summary>localsend.send: queues the files for the receiver found by <see cref="DiscoverAsync"/>.</summary>
    public Task<TransferJob> SendAsync(string deviceId, LocalSendTarget target, IReadOnlyList<string> files, CancellationToken ct) =>
        Queue.EnqueueAsync(deviceId, TransferKind.LocalSend, new LocalSendRequest(target, files), ConflictRule.KeepBoth, ct);

    /// <summary>localsend.retry: the job runs again for the files that didn't go, with the PIN LocalSend shows when it asked for one.</summary>
    public void Retry(string jobId, string? pin)
    {
        RequireLocalSendJob(jobId);
        Handler.SetPin(jobId, pin);
        Queue.Retry(jobId);
    }

    /// <summary>localsend.job: the send's phase and each file's result.</summary>
    public LocalSendJobStatus GetStatus(string jobId) => Status(RequireLocalSendJob(jobId), withFiles: true);

    private TransferJob RequireLocalSendJob(string jobId)
    {
        var job = Queue.Get(jobId);
        if (job is not { Kind: TransferKind.LocalSend })
        {
            throw new ArgumentException("No LocalSend transfer has that id.", nameof(jobId));
        }

        return job;
    }

    private LocalSendJobStatus Status(TransferJob job, bool withFiles)
    {
        var (phase, alias, pinGiven, sessionError) = Handler.Peek(job.Id);
        var mapped = job.State switch
        {
            TransferState.Done => LocalSendPhase.Done,
            TransferState.Failed => LocalSendPhase.Failed,
            TransferState.Cancelled => LocalSendPhase.Cancelled,
            TransferState.Running => phase ?? LocalSendPhase.Connecting,
            _ => LocalSendPhase.Connecting,
        };
        var error = job.State switch
        {
            TransferState.Failed => job.Error ?? sessionError,
            TransferState.Done when job.FailedItems > 0 => sessionError,
            _ => null,
        };

        IReadOnlyList<LocalSendFileResult>? files = null;
        if (withFiles)
        {
            files = _itemsOf(job.Id).Select(i => new LocalSendFileResult(i.Index, i.Name, i.Size, i.State switch
            {
                TransferItemState.Running => LocalSendFileState.Sending,
                TransferItemState.Done => LocalSendFileState.Done,
                TransferItemState.Failed => LocalSendFileState.Failed,
                TransferItemState.Skipped => LocalSendFileState.Skipped,
                _ => LocalSendFileState.Queued,
            }, i.State == TransferItemState.Failed ? new TransferError(i.ErrorCode ?? "internal", i.ErrorMessage ?? "") : null)).ToList();
        }

        return new LocalSendJobStatus(job.Id, job.DeviceId, mapped, alias, pinGiven, error, files);
    }

    private void Changed(string jobId)
    {
        if (_queue?.Get(jobId) is not { Kind: TransferKind.LocalSend } job)
        {
            return;
        }

        var status = Status(job, withFiles: false);
        var key = (status.Phase, status.Error?.Code);
        if (_published.TryGetValue(jobId, out var last) && last == key)
        {
            return;
        }

        _published[jobId] = key;
        Updated?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync() => await Finder.DisposeAsync().ConfigureAwait(false);
}
