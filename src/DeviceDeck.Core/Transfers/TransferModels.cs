namespace DeviceDeck.Core.Transfers;

// The bridge-only shapes of docs/bridge-contract.md ("TransferJob") plus what the queue keeps per item.

public enum TransferKind
{
    PhotosExport, PhotosImport, CopyToPc, CopyToDevice, AppInstall, AppExport,

    // Photos, round 2: incremental backup to the PC, and photos into an iPhone app's Documents.
    PhotosBackup, PhotosToApp,

    /// <summary>Photos or files dragged into File Explorer: the queue copies them while Explorer reads them (virtual files).</summary>
    DragOut,

    /// <summary>Round 3: PC photos and videos to the LocalSend app on the phone, which saves them into Photos (LocalSendHandler).</summary>
    LocalSend,

    /// <summary>Round 4: WhatsApp's media and its own encrypted backups copied off an Android phone (WhatsApp/WhatsAppMediaHandler).</summary>
    WhatsAppMedia,

    /// <summary>v0.4.1: deleting the photos the user confirmed, from Photos or Clean-up, in stages (PhotoDelete/PhotoDeleteHandler).</summary>
    PhotosDelete,
}

public enum TransferState { Queued, Running, Paused, WaitingForDevice, NeedsDecision, Done, Failed, Cancelled }

/// <summary>The user's answer to one conflict.</summary>
public enum ConflictChoice { Replace, KeepBoth, Skip }

/// <summary>What a job does when its destination already exists; Ask stops the job at <see cref="TransferState.NeedsDecision"/>.</summary>
public enum ConflictRule { Ask, Replace, KeepBoth, Skip }

public sealed record TransferConflict(string ItemName, long ExistingSize, long NewSize);

public sealed record TransferError(string Code, string Message);

/// <summary>A snapshot of one job as the page sees it.</summary>
public sealed record TransferJob(
    string Id,
    string DeviceId,
    TransferKind Kind,
    string Title,
    TransferState State,
    int TotalItems,
    int DoneItems,
    int FailedItems,
    long TotalBytes,
    long DoneBytes,
    long? BytesPerSecond,
    long? EtaSeconds,
    TransferConflict? Conflict,
    TransferError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    TransferStage? Stage = null);

/// <summary>
/// v0.4.1: where a job that runs in stages is (<see cref="IWholeJobTransferHandler"/>; deleting photos: "copying",
/// "reading", "deleting", "checking"), <paramref name="Done"/> of <paramref name="Total"/> in that stage (0 of 0 while
/// not known yet). Null for every other job, and once a job has finished.
/// </summary>
public sealed record TransferStage(string Name, int Done, int Total);

public enum TransferItemState { Queued, Running, Done, Failed, Skipped }

/// <summary>One file (or one install) inside a job.</summary>
/// <param name="Source">A device path, asset id, app id or PC path, depending on the job kind.</param>
/// <param name="Destination">Where it goes: a PC path or a device path; the handler may change it (Keep both).</param>
public sealed record TransferItemPlan(string Source, string Destination, string Name, long Size);

public sealed class TransferItem
{
    public required int Index { get; init; }
    public required string Source { get; init; }
    public required string Destination { get; set; }
    public required string Name { get; init; }
    public required long Size { get; set; }
    public TransferItemState State { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>The answer to this item's conflict, when the user chose for this item only.</summary>
    public ConflictChoice? Decision { get; set; }

    public static TransferItem FromPlan(int index, TransferItemPlan plan) => new()
    {
        Index = index,
        Source = plan.Source,
        Destination = plan.Destination,
        Name = plan.Name,
        Size = plan.Size,
        State = TransferItemState.Queued,
    };
}

// Request payloads, one per kind; persisted with the job so it can be planned again after a restart.

public sealed record PhotosExportRequest(IReadOnlyList<string> AssetIds, string Destination, bool ConvertHeic, string FolderPattern);

public sealed record PhotosImportRequest(IReadOnlyList<string> Files);

public sealed record CopyToPcRequest(string Root, IReadOnlyList<string> Paths, string Destination);

public sealed record CopyToDeviceRequest(string Root, IReadOnlyList<string> Files, string Path);

public sealed record AppInstallRequest(IReadOnlyList<string> Files);

public sealed record AppExportRequest(string AppId, string Destination);
