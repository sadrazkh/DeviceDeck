namespace DeviceDeck.Core.Transfers;

/// <summary>The persisted form of a job: everything needed to show it and to carry on after a restart.</summary>
public sealed class StoredJob
{
    public required string Id { get; init; }
    public required long Seq { get; init; }
    public required string DeviceId { get; init; }
    public required TransferKind Kind { get; init; }
    public required string Title { get; init; }
    public required TransferState State { get; set; }

    /// <summary>The request payload as JSON (camelCase), handed back to the handler to plan it again.</summary>
    public required string RequestJson { get; init; }

    public required ConflictRule Rule { get; set; }
    public bool Planned { get; set; }
    public TransferConflict? Conflict { get; set; }
    public int? ConflictItem { get; set; }
    public TransferError? Error { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<TransferItem> Items { get; set; } = [];
}

/// <summary>Synchronous on purpose: SQLite is local and fast, and the queue writes from inside its own lock so disk and memory never disagree.</summary>
public interface ITransferStore : IDisposable
{
    IReadOnlyList<StoredJob> LoadAll();

    /// <summary>Inserts or updates the job row (not its items).</summary>
    void UpsertJob(StoredJob job);

    void ReplaceItems(string jobId, IReadOnlyList<TransferItem> items);

    void UpdateItems(string jobId, IEnumerable<TransferItem> items);

    void DeleteJob(string jobId);
}
