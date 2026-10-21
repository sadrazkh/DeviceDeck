using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Serialization;

namespace DeviceDeck.Core.Transfers;

public sealed class TransferQueueOptions
{
    /// <summary>Jobs running at once per phone. USB services are one pipe, so more than two only slows things down.</summary>
    public int WorkersPerDevice { get; init; } = 2;

    /// <summary>At most one progress event per job per interval (10 a second); state changes go out at once.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public TimeSpan SpeedWindow { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How soon a job that stopped on a locked or briefly missing phone tries again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Finished jobs kept in the list (and the database); older ones are dropped.</summary>
    public int KeepFinished { get; init; } = 200;
}

/// <summary>
/// Every byte copied to or from a phone goes through here. Jobs belong to one device; each device runs up to
/// <see cref="TransferQueueOptions.WorkersPerDevice"/> jobs. A job pauses when its phone leaves and carries on,
/// at the item it stopped on, when the same device id returns. State lives in an <see cref="ITransferStore"/>.
/// </summary>
public sealed class TransferQueue : IAsyncDisposable
{
    private enum StopReason { None, Pause, Cancel, DeviceGone, Shutdown }

    private sealed class Job(StoredJob data, ITransferHandler handler, TimeSpan speedWindow)
    {
        public StoredJob Data { get; } = data;
        public ITransferHandler Handler { get; } = handler;
        public IBatchingTransferHandler? Batching { get; } = handler as IBatchingTransferHandler;
        public SpeedMeter Speed { get; } = new(speedWindow);
        public CancellationTokenSource? Cts { get; set; }
        public Task? Worker { get; set; }
        public StopReason Stop { get; set; }

        /// <summary>Items being copied right now, by index (one, or several for a batching handler).</summary>
        public Dictionary<int, Flight> InFlight { get; } = [];

        public long CompletedBytes { get; set; }
        public long LastEmit { get; set; } = long.MinValue;
        public bool TrailingPending { get; set; }

        // Running totals of the items, so a snapshot doesn't walk them all (a photo backup can have tens of thousands).
        public int DoneCount { get; set; }
        public int FailedCount { get; set; }
        public long TotalBytes { get; set; }
        public long DoneBytes { get; set; }

        /// <summary>Where the next queued item is looked for: items before it were taken earlier in this run.</summary>
        public int Cursor { get; set; }

        /// <summary>Item states not saved yet (batching handlers save them every so often, and when the run stops).</summary>
        public List<TransferItem> Unsaved { get; } = [];
        public long LastSave { get; set; }

        /// <summary>The conflict an item stopped on; the run ends at needsDecision.</summary>
        public (TransferConflict Conflict, int Index)? Decision { get; set; }
    }

    private sealed class Flight(TransferItem item)
    {
        public TransferItem Item { get; } = item;

        /// <summary>The size the totals counted when the item started (a handler may correct it while copying).</summary>
        public long StartSize { get; } = item.Size;

        public long Bytes { get; set; }
    }

    /// <summary>A batching handler's item states are saved at least this often, or every <see cref="SaveEvery"/> items.</summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(1);
    private const int SaveEvery = 64;

    private readonly DeviceHub _hub;
    private readonly ITransferStore _store;
    private readonly IReadOnlyDictionary<TransferKind, ITransferHandler> _handlers;
    private readonly IKeepAwake _keepAwake;
    private readonly TransferQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Channel<TransferJob> _outbox = Channel.CreateUnbounded<TransferJob>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _outboxPump;
    private long _seq;
    private bool _started;
    private bool _disposed;

    public TransferQueue(
        DeviceHub hub,
        ITransferStore store,
        IEnumerable<ITransferHandler> handlers,
        IKeepAwake? keepAwake = null,
        TransferQueueOptions? options = null,
        TimeProvider? time = null)
    {
        _hub = hub;
        _store = store;
        _handlers = handlers.ToDictionary(h => h.Kind);
        _keepAwake = keepAwake ?? new NullKeepAwake();
        _options = options ?? new TransferQueueOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A job's new snapshot; raised in order from one background thread, progress throttled per <see cref="TransferQueueOptions.ProgressInterval"/>.</summary>
    public event EventHandler<TransferJob>? JobUpdated;

    /// <summary>Loads persisted jobs (a job that was running when the app closed is queued again) and starts work.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            foreach (var stored in _store.LoadAll())
            {
                if (!_handlers.TryGetValue(stored.Kind, out var handler))
                {
                    continue;
                }

                if (stored.State == TransferState.Running)
                {
                    stored.State = TransferState.Queued;
                }

                foreach (var item in stored.Items.Where(i => i.State == TransferItemState.Running))
                {
                    item.State = TransferItemState.Queued;
                }

                var job = new Job(stored, handler, _options.SpeedWindow);
                Recount(job);
                _jobs[stored.Id] = job;
                _seq = Math.Max(_seq, stored.Seq);
            }

            _hub.DeviceChanged += OnDeviceChanged;
            _outboxPump = Task.Run(PumpOutboxAsync);
            Prune();
            Pump();
        }
    }

    public IReadOnlyList<TransferJob> List()
    {
        lock (_gate)
        {
            return _jobs.Values.OrderBy(j => j.Data.Seq).Select(Snapshot).ToArray();
        }
    }

    public TransferJob? Get(string jobId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var job) ? Snapshot(job) : null;
        }
    }

    /// <summary>Validates the request with its handler, stores the job and returns at once; the copy runs in the background.</summary>
    public async Task<TransferJob> EnqueueAsync(string deviceId, TransferKind kind, object request, ConflictRule rule, CancellationToken ct)
    {
        if (!_handlers.TryGetValue(kind, out var handler))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "This kind of transfer isn't available.");
        }

        var device = _hub.GetUsableDevice(deviceId);
        var json = JsonSerializer.SerializeToElement(request, request.GetType(), DeviceDeckJson.Options);
        var title = await handler.DescribeAsync(device, json, ct).ConfigureAwait(false);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                throw new InvalidOperationException("The transfer queue hasn't started.");
            }

            var stored = new StoredJob
            {
                Id = Guid.CreateVersion7().ToString("N"),
                Seq = ++_seq,
                DeviceId = deviceId,
                Kind = kind,
                Title = title,
                State = TransferState.Queued,
                RequestJson = json.GetRawText(),
                Rule = rule,
                CreatedAt = _time.GetLocalNow(),
            };
            _store.UpsertJob(stored);
            var job = new Job(stored, handler, _options.SpeedWindow);
            _jobs.Add(stored.Id, job);
            Emit(job);
            Pump();
            return Snapshot(job);
        }
    }

    /// <summary>Stops a job where it is; the item in flight starts over on resume.</summary>
    public void Pause(string jobId)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            var job = Find(jobId);
            switch (job.Data.State)
            {
                case TransferState.Running when job.Stop is StopReason.None or StopReason.DeviceGone:
                    job.Stop = StopReason.Pause;
                    cancel = job.Cts;
                    break;
                case TransferState.Queued or TransferState.WaitingForDevice or TransferState.NeedsDecision:
                    SetState(job, TransferState.Paused);
                    break;
            }
        }

        TryCancel(cancel);
    }

    /// <summary>Paused → queued. On a failed job (or one done with failures) it retries the failed items.</summary>
    public void Resume(string jobId)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            switch (job.Data.State)
            {
                case TransferState.Paused:
                case TransferState.WaitingForDevice:
                    SetState(job, TransferState.Queued);
                    break;
                case TransferState.Failed:
                case TransferState.Done when job.Data.Items.Any(i => i.State == TransferItemState.Failed):
                    RequeueFailed(job);
                    break;
            }

            Pump();
        }
    }

    /// <summary>
    /// transfers.retry: a failed job, a job done with failed items, or a cancelled one runs again. Items already
    /// copied stay done; failed ones and the ones never reached are copied (a job that failed while planning plans again).
    /// </summary>
    public void Retry(string jobId)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            switch (job.Data.State)
            {
                case TransferState.Failed:
                case TransferState.Cancelled:
                case TransferState.Done when job.Data.Items.Any(i => i.State == TransferItemState.Failed):
                    RequeueFailed(job);
                    break;
                default:
                    throw new InvalidOperationException("This transfer has nothing to try again.");
            }

            Pump();
        }
    }

    /// <summary>
    /// transfers.clear: removes finished jobs (done, failed, cancelled) from the list and the database; all of them,
    /// or only those in <paramref name="jobIds"/>. Jobs still going are left alone. Returns how many were removed.
    /// </summary>
    public int Clear(IReadOnlyCollection<string>? jobIds = null)
    {
        lock (_gate)
        {
            var wanted = jobIds is null ? null : new HashSet<string>(jobIds, StringComparer.Ordinal);
            var finished = _jobs.Values
                .Where(j => j.Data.State is TransferState.Done or TransferState.Failed or TransferState.Cancelled && j.Worker is null)
                .Where(j => wanted is null || wanted.Contains(j.Data.Id))
                .ToList();
            foreach (var job in finished)
            {
                _jobs.Remove(job.Data.Id);
                _store.DeleteJob(job.Data.Id);
            }

            return finished.Count;
        }
    }

    // Caller holds _gate.
    private void RequeueFailed(Job job)
    {
        var retry = job.Data.Items.Where(i => i.State == TransferItemState.Failed).ToList();
        foreach (var item in retry)
        {
            item.State = TransferItemState.Queued;
            item.ErrorCode = null;
            item.ErrorMessage = null;
        }

        _store.UpdateItems(job.Data.Id, retry);
        Recount(job);
        job.Data.Error = null;
        job.Data.FinishedAt = null;
        SetState(job, TransferState.Queued);
    }

    /// <summary>Stops a job for good. Items already copied stay; the partial file of the one in flight is removed.</summary>
    public void Cancel(string jobId)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            var job = Find(jobId);
            switch (job.Data.State)
            {
                case TransferState.Running:
                    job.Stop = StopReason.Cancel;
                    cancel = job.Cts;
                    break;
                case TransferState.Queued or TransferState.Paused or TransferState.WaitingForDevice or TransferState.NeedsDecision:
                    job.Data.FinishedAt = _time.GetLocalNow();
                    SetState(job, TransferState.Cancelled);
                    Prune();
                    break;
            }
        }

        TryCancel(cancel);
    }

    /// <summary>Answers a job at needsDecision. With <paramref name="applyToAll"/> the choice becomes the job's rule for every later conflict.</summary>
    public void ResolveConflict(string jobId, ConflictChoice choice, bool applyToAll)
    {
        lock (_gate)
        {
            var job = Find(jobId);
            if (job.Data.State != TransferState.NeedsDecision)
            {
                throw new InvalidOperationException("This transfer isn't waiting for a decision.");
            }

            if (applyToAll)
            {
                job.Data.Rule = choice switch
                {
                    ConflictChoice.Replace => ConflictRule.Replace,
                    ConflictChoice.KeepBoth => ConflictRule.KeepBoth,
                    _ => ConflictRule.Skip,
                };
            }
            else if (job.Data.ConflictItem is { } index && job.Data.Items.FirstOrDefault(i => i.Index == index) is { } item)
            {
                item.Decision = choice;
                _store.UpdateItems(job.Data.Id, [item]);
            }

            SetState(job, TransferState.Queued);
            Pump();
        }
    }

    private Job Find(string jobId)
    {
        if (jobId is null || !_jobs.TryGetValue(jobId, out var job))
        {
            throw new ArgumentException("No transfer has that id.", nameof(jobId));
        }

        return job;
    }

    // Starts queued jobs on usable devices, up to the per-device worker count. Caller holds _gate.
    private void Pump()
    {
        if (_disposed || !_started)
        {
            return;
        }

        foreach (var group in _jobs.Values.Where(j => j.Data.State is TransferState.Queued or TransferState.Running).GroupBy(j => j.Data.DeviceId).ToList())
        {
            var usable = IsUsable(group.Key);
            var running = group.Count(j => j.Data.State == TransferState.Running);
            foreach (var job in group.Where(j => j.Data.State == TransferState.Queued).OrderBy(j => j.Data.Seq).ToList())
            {
                if (job.Worker is not null)
                {
                    // Retried or resumed while its last run is still closing; that run's finally pumps again.
                    continue;
                }

                if (!usable)
                {
                    SetState(job, TransferState.WaitingForDevice);
                }
                else if (running < _options.WorkersPerDevice)
                {
                    StartWorker(job);
                    running++;
                }
            }
        }
    }

    private bool IsUsable(string deviceId) =>
        _hub.TryGetDevice(deviceId, out var device) && IsUsableState(device.Summary.State);

    private static bool IsUsableState(DeviceState state) => state is DeviceState.Ready or DeviceState.Locked;

    private void StartWorker(Job job)
    {
        job.Stop = StopReason.None;
        job.Cts = new CancellationTokenSource();
        job.InFlight.Clear();
        job.Cursor = 0;
        job.Decision = null;
        Recount(job);
        job.CompletedBytes = job.DoneBytes;
        job.Speed.Reset();
        SetState(job, TransferState.Running);
        var ct = job.Cts.Token;
        job.Worker = Task.Run(() => RunAsync(job, ct));
    }

    private async Task RunAsync(Job job, CancellationToken ct)
    {
        using var hold = _keepAwake.Acquire();
        TransferWorkContext? context = null;
        Exception? stopped = null;
        try
        {
            var device = _hub.GetUsableDevice(job.Data.DeviceId);
            ConflictRule rule;
            bool planned;
            lock (_gate)
            {
                rule = job.Data.Rule;
                planned = job.Data.Planned;
            }

            using (var request = JsonDocument.Parse(job.Data.RequestJson))
            {
                context = new TransferWorkContext(job.Data.Id, device, request.RootElement.Clone(), rule);
            }

            if (!planned)
            {
                var plans = await job.Handler.PlanAsync(context, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    job.Data.Items = plans.Select((p, i) => TransferItem.FromPlan(i, p)).ToList();
                    job.Data.Planned = true;
                    _store.ReplaceItems(job.Data.Id, job.Data.Items);
                    _store.UpsertJob(job.Data);
                    Recount(job);
                    Emit(job);
                }
            }

            await RunItemsAsync(job, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stopped = ex;
        }

        try
        {
            // What the handler holds (the photo backup's ledger) is saved before the job's new state goes out.
            if (context is not null && job.Batching is { } batching)
            {
                try
                {
                    await batching.EndRunAsync(context).ConfigureAwait(false);
                }
                catch (Exception ex) when (stopped is null)
                {
                    stopped = ex;
                }
                catch (Exception)
                {
                    // The run is stopping for another reason already; that one is what the job reports.
                }
            }

            lock (_gate)
            {
                Conclude(job, stopped, ct);
            }
        }
        finally
        {
            if (context is not null)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }

            lock (_gate)
            {
                SaveUnsaved(job);
                job.InFlight.Clear();
                job.Decision = null;
                job.Cts?.Dispose();
                job.Cts = null;
                job.Worker = null;
                Pump();
            }
        }
    }

    /// <summary>The state a run ends in: done or failed by its items, needsDecision, or what stopped it. Caller holds _gate.</summary>
    private void Conclude(Job job, Exception? stopped, CancellationToken ct)
    {
        if (stopped is null && job.Decision is { } decision)
        {
            job.Data.Conflict = decision.Conflict;
            job.Data.ConflictItem = decision.Index;
            SetState(job, TransferState.NeedsDecision);
        }
        else if (stopped is null)
        {
            job.Data.FinishedAt = _time.GetLocalNow();
            if (job.FailedCount > 0 && job.DoneCount == 0)
            {
                var failed = job.Data.Items.First(i => i.State == TransferItemState.Failed);
                job.Data.Error = job.FailedCount == 1
                    ? new TransferError(failed.ErrorCode ?? "internal", failed.ErrorMessage ?? "The copy failed.")
                    : new TransferError(failed.ErrorCode ?? "internal", $"None of the {job.FailedCount:N0} items could be copied. {failed.ErrorMessage}");
                SetState(job, TransferState.Failed);
            }
            else
            {
                SetState(job, TransferState.Done);
            }

            Prune();
        }
        else if (ct.IsCancellationRequested || IsDeviceUnavailable(stopped))
        {
            switch (job.Stop)
            {
                case StopReason.Pause:
                    SetState(job, TransferState.Paused);
                    break;
                case StopReason.Cancel:
                    job.Data.FinishedAt = _time.GetLocalNow();
                    SetState(job, TransferState.Cancelled);
                    Prune();
                    break;
                case StopReason.Shutdown:
                    // Left as running on disk: the next start queues it again.
                    break;
                default:
                    SetState(job, TransferState.WaitingForDevice);
                    ScheduleRetry(job);
                    break;
            }
        }
        else
        {
            job.Data.Error = Describe(stopped);
            job.Data.FinishedAt = _time.GetLocalNow();
            SetState(job, TransferState.Failed);
            Prune();
        }
    }

    /// <summary>
    /// Copies the job's queued items, one at a time, or <see cref="IBatchingTransferHandler.ParallelItems"/> at once.
    /// Throws what stopped the run; returns when every item had its turn or one stopped on a conflict.
    /// </summary>
    private async Task RunItemsAsync(Job job, TransferWorkContext context, CancellationToken ct)
    {
        var lanes = Math.Clamp(job.Batching?.ParallelItems ?? 1, 1, 8);
        if (lanes == 1)
        {
            await LaneAsync(job, context, null, ct).ConfigureAwait(false);
            return;
        }

        // A lane that stops the run (a pulled cable, a conflict) stops the others; their items go back to the queue.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = Enumerable.Range(0, lanes).Select(_ => LaneAsync(job, context, stop, stop.Token)).ToArray();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return;
        }
        catch (Exception)
        {
            // Looked at below, all of them.
        }

        lock (_gate)
        {
            if (job.Decision is not null)
            {
                return;
            }
        }

        var errors = tasks.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).ToList();
        // A phone that went away explains the lanes that were stopped because of it.
        var reason = errors.FirstOrDefault(IsDeviceUnavailable) ?? errors.FirstOrDefault(e => e is not OperationCanceledException) ?? errors.FirstOrDefault();
        if (reason is not null)
        {
            ExceptionDispatchInfo.Throw(reason);
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException("The copy stopped.", ct);
    }

    private async Task LaneAsync(Job job, TransferWorkContext context, CancellationTokenSource? stopLanes, CancellationToken ct)
    {
        while (true)
        {
            Flight flight;
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                if (NextQueued(job) is not { } next)
                {
                    return;
                }

                next.State = TransferItemState.Running;
                flight = new Flight(next);
                job.InFlight[next.Index] = flight;
            }

            var item = flight.Item;
            var progress = new InlineProgress<long>(bytes => OnItemProgress(job, flight, bytes));
            try
            {
                var outcome = await job.Handler.RunItemAsync(context, item, progress, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    item.State = outcome == TransferItemState.Skipped ? TransferItemState.Skipped : TransferItemState.Done;
                    item.ErrorCode = null;
                    item.ErrorMessage = null;
                    job.CompletedBytes += item.Size;
                    EndItem(job, flight);
                }
            }
            catch (TransferNeedsDecisionException need)
            {
                lock (_gate)
                {
                    item.State = TransferItemState.Queued;
                    EndItem(job, flight);
                    job.Decision ??= (need.Conflict, item.Index);
                }

                TryCancel(stopLanes);
                return;
            }
            catch (Exception ex) when (ct.IsCancellationRequested || IsDeviceUnavailable(ex))
            {
                lock (_gate)
                {
                    item.State = TransferItemState.Queued;
                    EndItem(job, flight);
                }

                TryCancel(stopLanes);
                throw;
            }
            catch (Exception ex)
            {
                var error = Describe(ex);
                lock (_gate)
                {
                    item.State = TransferItemState.Failed;
                    item.ErrorCode = error.Code;
                    item.ErrorMessage = error.Message;
                    EndItem(job, flight);
                }
            }
        }
    }

    /// <summary>The next queued item at or after the cursor. Caller holds _gate.</summary>
    private static TransferItem? NextQueued(Job job)
    {
        var items = job.Data.Items;
        while (job.Cursor < items.Count)
        {
            var item = items[job.Cursor++];
            if (item.State == TransferItemState.Queued)
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>An item left the run (done, skipped, failed, or back to the queue): totals, saving, an update. Caller holds _gate.</summary>
    private void EndItem(Job job, Flight flight)
    {
        var item = flight.Item;
        job.InFlight.Remove(item.Index);
        job.TotalBytes += item.Size - flight.StartSize;
        switch (item.State)
        {
            case TransferItemState.Done or TransferItemState.Skipped:
                job.DoneCount++;
                job.DoneBytes += item.Size;
                break;
            case TransferItemState.Failed:
                job.FailedCount++;
                break;
        }

        if (job.Batching is null)
        {
            _store.UpdateItems(job.Data.Id, [item]);
        }
        else
        {
            job.Unsaved.Add(item);
            if (job.Unsaved.Count >= SaveEvery || _time.GetElapsedTime(job.LastSave) >= SaveInterval)
            {
                SaveUnsaved(job);
            }
        }

        EmitThrottled(job);
    }

    /// <summary>Writes the item states a batching handler's run hasn't saved yet. Caller holds _gate.</summary>
    private void SaveUnsaved(Job job)
    {
        job.LastSave = _time.GetTimestamp();
        if (job.Unsaved.Count == 0)
        {
            return;
        }

        _store.UpdateItems(job.Data.Id, job.Unsaved.ToArray());
        job.Unsaved.Clear();
    }

    /// <summary>The totals from scratch: when a job is loaded, planned, retried or started. Caller holds _gate.</summary>
    private static void Recount(Job job)
    {
        int done = 0, failed = 0;
        long total = 0, doneBytes = 0;
        foreach (var item in job.Data.Items)
        {
            total += item.Size;
            switch (item.State)
            {
                case TransferItemState.Done or TransferItemState.Skipped:
                    done++;
                    doneBytes += item.Size;
                    break;
                case TransferItemState.Failed:
                    failed++;
                    break;
            }
        }

        (job.DoneCount, job.FailedCount, job.TotalBytes, job.DoneBytes) = (done, failed, total, doneBytes);
    }

    private void OnItemProgress(Job job, Flight flight, long bytes)
    {
        lock (_gate)
        {
            if (job.Data.State != TransferState.Running || !job.InFlight.ContainsKey(flight.Item.Index))
            {
                return;
            }

            flight.Bytes = bytes;
            job.Speed.Add(_time.GetUtcNow().UtcTicks, job.CompletedBytes + InFlightBytes(job));
            EmitThrottled(job);
        }
    }

    private static long InFlightBytes(Job job)
    {
        long sum = 0;
        foreach (var flight in job.InFlight.Values)
        {
            sum += flight.Item is { Size: > 0 } item ? Math.Min(flight.Bytes, item.Size) : flight.Bytes;
        }

        return sum;
    }

    // A phone that comes back (or is ready again) gets its waiting jobs queued; a retry covers a phone
    // that came back before the job had finished stopping, and a locked phone that never changes state.
    private void ScheduleRetry(Job job)
    {
        _ = Task.Delay(_options.RetryDelay).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (!_disposed && job.Data.State == TransferState.WaitingForDevice && IsUsable(job.Data.DeviceId))
                {
                    SetState(job, TransferState.Queued);
                    Pump();
                }
            }
        }, TaskScheduler.Default);
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        var cancel = new List<CancellationTokenSource>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var usable = e.Change != DeviceChange.Removed && IsUsableState(e.Summary.State);
            foreach (var job in _jobs.Values.Where(j => j.Data.DeviceId == e.Summary.Id).ToList())
            {
                if (!usable)
                {
                    if (job.Data.State == TransferState.Running && job.Stop == StopReason.None && job.Cts is not null)
                    {
                        job.Stop = StopReason.DeviceGone;
                        cancel.Add(job.Cts);
                    }
                    else if (job.Data.State == TransferState.Queued)
                    {
                        SetState(job, TransferState.WaitingForDevice);
                    }
                }
                else if (job.Data.State == TransferState.WaitingForDevice)
                {
                    SetState(job, TransferState.Queued);
                }
            }

            if (usable)
            {
                Pump();
            }
        }

        foreach (var cts in cancel)
        {
            TryCancel(cts);
        }
    }

    private void SetState(Job job, TransferState state)
    {
        // Never a job state on disk ahead of its items'.
        SaveUnsaved(job);
        job.Data.State = state;
        if (state != TransferState.NeedsDecision)
        {
            job.Data.Conflict = null;
            job.Data.ConflictItem = null;
        }

        _store.UpsertJob(job.Data);
        Emit(job);
    }

    private void Emit(Job job)
    {
        job.LastEmit = _time.GetTimestamp();
        _outbox.Writer.TryWrite(Snapshot(job));
    }

    private void EmitThrottled(Job job)
    {
        var now = _time.GetTimestamp();
        var since = job.LastEmit == long.MinValue ? TimeSpan.MaxValue : _time.GetElapsedTime(job.LastEmit, now);
        if (since >= _options.ProgressInterval)
        {
            Emit(job);
            return;
        }

        if (job.TrailingPending)
        {
            return;
        }

        job.TrailingPending = true;
        _ = Task.Delay(_options.ProgressInterval - since).ContinueWith(_ =>
        {
            lock (_gate)
            {
                job.TrailingPending = false;
                if (!_disposed && _jobs.ContainsKey(job.Data.Id))
                {
                    // Under load this can fire late, after a progress report already went out on its own;
                    // checking the interval again keeps it to one update per interval instead of two in a row.
                    EmitThrottled(job);
                }
            }
        }, TaskScheduler.Default);
    }

    private async Task PumpOutboxAsync()
    {
        await foreach (var snapshot in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                JobUpdated?.Invoke(this, snapshot);
            }
            catch (Exception)
            {
                // A listener's bug must not stop the queue's events.
            }
        }
    }

    private TransferJob Snapshot(Job job)
    {
        var data = job.Data;
        var (done, failed, total, doneBytes) = (job.DoneCount, job.FailedCount, job.TotalBytes, job.DoneBytes);
        foreach (var flight in job.InFlight.Values)
        {
            total += flight.Item.Size - flight.StartSize;
        }

        var running = data.State == TransferState.Running;
        if (running)
        {
            doneBytes += InFlightBytes(job);
        }

        var rate = running ? job.Speed.BytesPerSecond : null;
        long? eta = rate is > 0 ? (long)Math.Ceiling(Math.Max(0, total - doneBytes) / (double)rate.Value) : null;
        return new TransferJob(
            data.Id,
            data.DeviceId,
            data.Kind,
            data.Title,
            data.State,
            data.Items.Count,
            done,
            failed,
            total,
            doneBytes,
            rate,
            eta,
            data.Conflict,
            data.Error,
            data.CreatedAt,
            data.FinishedAt);
    }

    private void Prune()
    {
        var finished = _jobs.Values
            .Where(j => j.Data.State is TransferState.Done or TransferState.Failed or TransferState.Cancelled)
            .OrderByDescending(j => j.Data.FinishedAt ?? j.Data.CreatedAt)
            .Skip(_options.KeepFinished)
            .ToList();
        foreach (var job in finished)
        {
            _jobs.Remove(job.Data.Id);
            _store.DeleteJob(job.Data.Id);
        }
    }

    internal static bool IsDeviceUnavailable(Exception ex) => ex is DeviceException
    {
        Code: DeviceErrorCodes.Disconnected or DeviceErrorCodes.NotFound or DeviceErrorCodes.Locked
            or DeviceErrorCodes.TrustRequired or DeviceErrorCodes.Unauthorized,
    };

    internal static TransferError Describe(Exception ex) => ex switch
    {
        DeviceException d => new TransferError(d.Code, d.Message),
        UnauthorizedAccessException => new TransferError(DeviceErrorCodes.PermissionDenied, "Windows didn't allow writing there. Pick another folder."),
        FileNotFoundException or DirectoryNotFoundException => new TransferError(DeviceErrorCodes.PathNotFound, "A file or folder on this PC is missing."),
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => new TransferError("internal", "The PC's disk is full."),
        IOException io => new TransferError("internal", io.Message),
        ArgumentException arg => new TransferError("internal", arg.Message),
        _ => new TransferError("internal", "Something went wrong while copying."),
    };

    private static void TryCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker finished in the meantime.
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cancel = new List<CancellationTokenSource>();
        var workers = new List<Task>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hub.DeviceChanged -= OnDeviceChanged;
            foreach (var job in _jobs.Values.Where(j => j.Worker is not null))
            {
                job.Stop = StopReason.Shutdown;
                if (job.Cts is not null)
                {
                    cancel.Add(job.Cts);
                }

                workers.Add(job.Worker!);
            }
        }

        foreach (var cts in cancel)
        {
            TryCancel(cts);
        }

        try
        {
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutting down; a worker that won't stop is abandoned.
        }

        _outbox.Writer.TryComplete();
        if (_outboxPump is not null)
        {
            await _outboxPump.ConfigureAwait(false);
        }
    }
}
