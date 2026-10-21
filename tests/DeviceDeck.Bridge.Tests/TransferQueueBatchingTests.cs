using System.Collections.Concurrent;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>
/// The queue with a handler that opts into <see cref="IBatchingTransferHandler"/> (the photo backup does): items side by
/// side, item states saved in batches, and the handler's own save before the job's new state goes out. Timing: these
/// assert order and overlap, never how long something took.
/// </summary>
public sealed class TransferQueueBatchingTests : IAsyncDisposable
{
    private readonly PhotosTestPhone _phone = new();
    private readonly DeviceHub _hub;
    private readonly CountingStore _store = new();
    private readonly GateHandler _handler = new();
    private TransferQueue? _queue;

    public TransferQueueBatchingTests()
    {
        _hub = new DeviceHub([new PhotosTestProvider(_phone)]) { DriverPollInterval = TimeSpan.Zero };
    }

    private async Task<TransferQueue> StartAsync(int items)
    {
        _handler.Items = items;
        await _hub.StartAsync(CancellationToken.None);
        _queue = new TransferQueue(_hub, _store, [_handler], new CountingKeepAwake(), TestHost.FastQueue());
        _handler.Queue = _queue;
        _queue.Start();
        return _queue;
    }

    private async Task<string> EnqueueAsync(TransferQueue queue) =>
        (await queue.EnqueueAsync(_phone.Summary.Id, TransferKind.CopyToPc, new GateRequest("x"), ConflictRule.KeepBoth, CancellationToken.None)).Id;

    [Fact]
    public async Task Items_run_side_by_side_up_to_the_handlers_limit()
    {
        var queue = await StartAsync(items: 12);
        _handler.Hold = true;
        var jobId = await EnqueueAsync(queue);

        await Wait.UntilAsync(() => _handler.Running == GateHandler.Lanes, "three items in flight");
        await Task.Delay(100);
        Assert.Equal(GateHandler.Lanes, _handler.Running);
        var job = queue.Get(jobId)!;
        Assert.Equal((TransferState.Running, 12, 0), (job.State, job.TotalItems, job.DoneItems));
        Assert.Equal(12 * GateHandler.ItemSize, job.TotalBytes);
        Assert.Equal(GateHandler.Lanes * GateHandler.ItemSize / 2, job.DoneBytes); // half of each item in flight reported

        _handler.ReleaseAll();
        await Wait.ForStateAsync(queue, jobId, TransferState.Done);
        job = queue.Get(jobId)!;
        Assert.Equal((12, 0, 12 * GateHandler.ItemSize), (job.DoneItems, job.FailedItems, job.DoneBytes));
        Assert.Equal(GateHandler.Lanes, _handler.MaxRunning);
        Assert.All(Enumerable.Range(0, 12), i => Assert.Equal(1, _handler.Completions.GetValueOrDefault(i)));
    }

    [Fact]
    public async Task Item_states_are_saved_in_batches_and_every_one_is_saved_by_the_end()
    {
        var queue = await StartAsync(items: 300);
        var jobId = await EnqueueAsync(queue);
        await Wait.ForStateAsync(queue, jobId, TransferState.Done);

        Assert.InRange(_store.ItemUpdates, 1, 300 / 10);
        var saved = _store.LoadAll().Single(j => j.Id == jobId);
        Assert.Equal(TransferState.Done, saved.State);
        Assert.All(saved.Items, i => Assert.Equal(TransferItemState.Done, i.State));
    }

    [Fact]
    public async Task The_handler_saves_before_the_jobs_new_state_goes_out_every_time_a_run_stops()
    {
        var queue = await StartAsync(items: 9);
        _handler.Hold = true;
        var jobId = await EnqueueAsync(queue);
        await Wait.UntilAsync(() => _handler.Running == GateHandler.Lanes, "three items in flight");

        queue.Pause(jobId);
        await Wait.ForStateAsync(queue, jobId, TransferState.Paused);
        // The items in flight went back to the queue; the store agrees with memory.
        Assert.Equal(0, queue.Get(jobId)!.DoneItems);
        Assert.All(_store.LoadAll().Single(j => j.Id == jobId).Items, i => Assert.Equal(TransferItemState.Queued, i.State));

        _handler.Hold = false;
        _handler.ReleaseAll();
        queue.Resume(jobId);
        await Wait.ForStateAsync(queue, jobId, TransferState.Done);

        // One save per run, each while the job still showed running.
        Assert.Equal([TransferState.Running, TransferState.Running], _handler.StateAtEndRun.ToArray());
        Assert.All(Enumerable.Range(0, 9), i => Assert.Equal(1, _handler.Completions.GetValueOrDefault(i)));
    }

    [Fact]
    public async Task A_failed_item_does_not_stop_the_other_lanes()
    {
        var queue = await StartAsync(items: 10);
        _handler.FailAt.Add(3);
        var jobId = await EnqueueAsync(queue);
        await Wait.ForStateAsync(queue, jobId, TransferState.Done);

        var job = queue.Get(jobId)!;
        Assert.Equal((9, 1), (job.DoneItems, job.FailedItems));
        var failed = _store.LoadAll().Single(j => j.Id == jobId).Items.Single(i => i.State == TransferItemState.Failed);
        Assert.Equal((3, "internal"), (failed.Index, failed.ErrorCode));
    }

    [Fact]
    public async Task A_dropped_connection_stops_every_lane_and_the_job_carries_on_when_the_phone_answers_again()
    {
        var queue = await StartAsync(items: 12);
        _handler.DropOnceAt = 5;
        var jobId = await EnqueueAsync(queue);

        await Wait.ForStateAsync(queue, jobId, TransferState.Done);
        var job = queue.Get(jobId)!;
        Assert.Equal((12, 0), (job.DoneItems, job.FailedItems));
        Assert.True(_handler.Starts.GetValueOrDefault(5) >= 2);
        Assert.All(Enumerable.Range(0, 12), i => Assert.Equal(1, _handler.Completions.GetValueOrDefault(i)));
        Assert.Equal(2, _handler.StateAtEndRun.Count);
    }

    [Fact]
    public async Task A_conflict_in_one_lane_stops_the_run_at_needs_decision()
    {
        var queue = await StartAsync(items: 8);
        _handler.AskAt = 2;
        var jobId = await EnqueueAsync(queue);

        await Wait.ForStateAsync(queue, jobId, TransferState.NeedsDecision);
        var job = queue.Get(jobId)!;
        Assert.Equal("item-2", job.Conflict!.ItemName);

        _handler.AskAt = null;
        queue.ResolveConflict(jobId, ConflictChoice.KeepBoth, applyToAll: false);
        await Wait.ForStateAsync(queue, jobId, TransferState.Done);
        Assert.Equal(8, queue.Get(jobId)!.DoneItems);
    }

    public async ValueTask DisposeAsync()
    {
        _handler.ReleaseAll();
        if (_queue is not null)
        {
            await _queue.DisposeAsync();
        }

        await _hub.DisposeAsync();
    }

    private sealed record GateRequest(string Note);

    /// <summary>Items that can be held at a gate, fail, drop the connection once, or ask about a conflict.</summary>
    private sealed class GateHandler : TransferHandler<GateRequest>, IBatchingTransferHandler
    {
        public const int Lanes = 3;
        public const long ItemSize = 1000;

        private readonly Lock _gate = new();
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _maxRunning;

        public int Items { get; set; }
        public bool Hold { get; set; }
        public HashSet<int> FailAt { get; } = [];
        public int? DropOnceAt { get; set; }
        public int? AskAt { get; set; }
        public TransferQueue? Queue { get; set; }
        public ConcurrentDictionary<int, int> Starts { get; } = new();
        public ConcurrentDictionary<int, int> Completions { get; } = new();
        public ConcurrentQueue<TransferState> StateAtEndRun { get; } = new();
        public int Running => Volatile.Read(ref _running);
        public int MaxRunning => Volatile.Read(ref _maxRunning);

        public override TransferKind Kind => TransferKind.CopyToPc;

        public int ParallelItems => Lanes;

        public void ReleaseAll()
        {
            lock (_gate)
            {
                _release.TrySetResult();
            }
        }

        public Task EndRunAsync(TransferWorkContext context)
        {
            StateAtEndRun.Enqueue(Queue!.Get(context.JobId)!.State);
            return Task.CompletedTask;
        }

        protected override Task<string> DescribeAsync(IDevice device, GateRequest request, CancellationToken ct) => Task.FromResult("Gate");

        protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, GateRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TransferItemPlan>>(Enumerable.Range(0, Items).Select(i => new TransferItemPlan($"src-{i}", $"dst-{i}", $"item-{i}", ItemSize)).ToList());

        protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, GateRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
        {
            var starts = Starts.AddOrUpdate(item.Index, 1, (_, n) => n + 1);
            var now = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxRunning, now);
            try
            {
                progress.Report(ItemSize / 2);
                if (AskAt == item.Index && item.Decision is null)
                {
                    throw new TransferNeedsDecisionException(new TransferConflict(item.Name, 1, 2));
                }

                if (DropOnceAt == item.Index && starts == 1)
                {
                    throw new DeviceException(DeviceErrorCodes.Disconnected, "The connection dropped.");
                }

                if (FailAt.Contains(item.Index))
                {
                    throw new InvalidOperationException("This one fails.");
                }

                if (Hold)
                {
                    Task release;
                    lock (_gate)
                    {
                        release = _release.Task;
                    }

                    await release.WaitAsync(ct);
                }
                else
                {
                    await Task.Yield();
                }

                Completions.AddOrUpdate(item.Index, 1, (_, n) => n + 1);
                return TransferItemState.Done;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            for (var current = Volatile.Read(ref target); value > current; current = Volatile.Read(ref target))
            {
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    return;
                }
            }
        }
    }

    /// <summary>The queue's store in memory, counting item writes.</summary>
    private sealed class CountingStore : ITransferStore
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, StoredJob> _jobs = [];
        private readonly Dictionary<string, Dictionary<int, TransferItem>> _items = [];

        public int ItemUpdates { get; private set; }

        public IReadOnlyList<StoredJob> LoadAll()
        {
            lock (_gate)
            {
                return _jobs.Values.Select(j => new StoredJob
                {
                    Id = j.Id,
                    Seq = j.Seq,
                    DeviceId = j.DeviceId,
                    Kind = j.Kind,
                    Title = j.Title,
                    State = j.State,
                    RequestJson = j.RequestJson,
                    Rule = j.Rule,
                    Planned = j.Planned,
                    CreatedAt = j.CreatedAt,
                    Items = _items.GetValueOrDefault(j.Id)?.Values.OrderBy(i => i.Index).Select(Copy).ToList() ?? [],
                }).ToList();
            }
        }

        public void UpsertJob(StoredJob job)
        {
            lock (_gate)
            {
                _jobs[job.Id] = new StoredJob
                {
                    Id = job.Id, Seq = job.Seq, DeviceId = job.DeviceId, Kind = job.Kind, Title = job.Title, State = job.State,
                    RequestJson = job.RequestJson, Rule = job.Rule, Planned = job.Planned, CreatedAt = job.CreatedAt,
                };
            }
        }

        public void ReplaceItems(string jobId, IReadOnlyList<TransferItem> items)
        {
            lock (_gate)
            {
                _items[jobId] = items.ToDictionary(i => i.Index, Copy);
            }
        }

        public void UpdateItems(string jobId, IEnumerable<TransferItem> items)
        {
            lock (_gate)
            {
                ItemUpdates++;
                foreach (var item in items)
                {
                    _items[jobId][item.Index] = Copy(item);
                }
            }
        }

        public void DeleteJob(string jobId)
        {
            lock (_gate)
            {
                _jobs.Remove(jobId);
                _items.Remove(jobId);
            }
        }

        public void Dispose()
        {
        }

        private static TransferItem Copy(TransferItem i) => new()
        {
            Index = i.Index, Source = i.Source, Destination = i.Destination, Name = i.Name, Size = i.Size, State = i.State,
            ErrorCode = i.ErrorCode, ErrorMessage = i.ErrorMessage, Decision = i.Decision,
        };
    }
}
