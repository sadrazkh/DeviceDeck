using System.Text.Json;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Serialization;

namespace DeviceDeck.Core.Transfers;

/// <summary>
/// Does the actual copying for one job kind. The queue owns state, persistence, conflicts and disconnects;
/// a handler only plans items and moves one item's bytes.
/// </summary>
public interface ITransferHandler
{
    TransferKind Kind { get; }

    /// <summary>Checks the request and returns the job title ("Exporting 1,284 photos to D:\Photos\2026"). Bad input throws <see cref="ArgumentException"/>.</summary>
    Task<string> DescribeAsync(IDevice device, JsonElement request, CancellationToken ct);

    /// <summary>Expands the request into items (walks folders, looks up photos). Runs once, the first time the job starts.</summary>
    Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, CancellationToken ct);

    /// <summary>Moves one item. Returns Done or Skipped; throws to fail it. Progress reports the item's bytes done.</summary>
    Task<TransferItemState> RunItemAsync(TransferWorkContext context, TransferItem item, IProgress<long> progress, CancellationToken ct);
}

/// <summary>Typed requests for <see cref="ITransferHandler"/>.</summary>
public abstract class TransferHandler<TRequest> : ITransferHandler where TRequest : class
{
    public abstract TransferKind Kind { get; }

    public Task<string> DescribeAsync(IDevice device, JsonElement request, CancellationToken ct) =>
        DescribeAsync(device, Parse(request), ct);

    public Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, CancellationToken ct) =>
        PlanAsync(context, Parse(context.Request), ct);

    public Task<TransferItemState> RunItemAsync(TransferWorkContext context, TransferItem item, IProgress<long> progress, CancellationToken ct) =>
        RunItemAsync(context, Parse(context.Request), item, progress, ct);

    protected abstract Task<string> DescribeAsync(IDevice device, TRequest request, CancellationToken ct);

    protected abstract Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, TRequest request, CancellationToken ct);

    protected abstract Task<TransferItemState> RunItemAsync(TransferWorkContext context, TRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct);

    private static TRequest Parse(JsonElement json)
    {
        try
        {
            return json.Deserialize<TRequest>(DeviceDeckJson.Options) ?? throw new ArgumentException("The transfer request is empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The transfer request is malformed: {ex.Message}", ex);
        }
    }
}

/// <summary>Thrown by a handler when a destination exists and the job's rule is Ask; the queue parks the job at needsDecision.</summary>
public sealed class TransferNeedsDecisionException(TransferConflict conflict) : Exception($"'{conflict.ItemName}' already exists.")
{
    public TransferConflict Conflict { get; } = conflict;
}

/// <summary>What a handler gets while a job runs: the device, the request and the conflict rule.</summary>
public sealed class TransferWorkContext : IAsyncDisposable
{
    private readonly Dictionary<string, IDeviceFileSystem> _fileSystems = new(StringComparer.Ordinal);

    internal TransferWorkContext(string jobId, IDevice device, JsonElement request, ConflictRule rule, TransferQueue? queue = null)
    {
        JobId = jobId;
        Device = device;
        Request = request;
        Rule = rule;
        Queue = queue;
    }

    /// <summary>The queue running the job (a delete queues its copy to the PC there first); null outside a queue.</summary>
    public TransferQueue? Queue { get; }

    public string JobId { get; }
    public IDevice Device { get; }
    public JsonElement Request { get; }
    public ConflictRule Rule { get; }

    /// <summary>The answer for this item's conflict: its own decision, else the job's rule, else ask the user.</summary>
    public ConflictChoice Decide(TransferItem item, long existingSize, long newSize)
    {
        if (item.Decision is { } decided)
        {
            return decided;
        }

        return Rule switch
        {
            ConflictRule.Replace => ConflictChoice.Replace,
            ConflictRule.KeepBoth => ConflictChoice.KeepBoth,
            ConflictRule.Skip => ConflictChoice.Skip,
            _ => throw new TransferNeedsDecisionException(new TransferConflict(item.Name, existingSize, newSize)),
        };
    }

    /// <summary>One open file-system session per root for the whole run (AFC and sync connections are costly).</summary>
    public async Task<IDeviceFileSystem> GetFileSystemAsync(string rootKey, CancellationToken ct)
    {
        if (!_fileSystems.TryGetValue(rootKey, out var fs))
        {
            fs = await Device.OpenFileSystemAsync(rootKey, ct).ConfigureAwait(false);
            _fileSystems[rootKey] = fs;
        }

        return fs;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var fs in _fileSystems.Values)
        {
            try
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The phone may already be gone; nothing to release then.
            }
        }

        _fileSystems.Clear();
    }
}

/// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread, in order (Progress&lt;T&gt; posts and reorders).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
