using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Deletes;

// v0.6: every confirmed delete runs as a job in the Transfers drawer, the way photos do since v0.4.1: the dialog closes
// at once, the drawer shows the stage and a bar, Stop works between items, and each item ends with a result.

/// <summary>Where a delete of files or apps was confirmed.</summary>
public enum DeleteSource { Files, Apps, Cleanup }

/// <summary>What every delete request of this kind holds.</summary>
public interface IDeleteRequest<out TItem> where TItem : IDeleteItem
{
    /// <summary>Exactly what the user confirmed, each at the size the page showed.</summary>
    IReadOnlyList<TItem> Items { get; }

    /// <summary>The total of the items' sizes, as the page showed it.</summary>
    long ExpectedBytes { get; }

    /// <summary>A PC folder everything is copied to first; nothing goes unless every copy finished.</summary>
    string? CopyFirstTo { get; }

    DeleteSource Source { get; }

    /// <summary>Clean-up: what changed since the analysis is left alone (skipped) and the rest goes.</summary>
    bool SkipChanged { get; }
}

public interface IDeleteItem
{
    long Size { get; }
}

/// <summary>A file or folder the user confirmed: its size as the page showed it (a folder: what it holds, or 0 when not counted).</summary>
public sealed record FileDeleteItem(string Root, string Path, long Size, bool IsDirectory = false) : IDeleteItem;

public sealed record FilesDeleteRequest(
    IReadOnlyList<FileDeleteItem> Items,
    long ExpectedBytes,
    string? CopyFirstTo = null,
    DeleteSource Source = DeleteSource.Files,
    bool SkipChanged = false) : IDeleteRequest<FileDeleteItem>;

/// <summary>An app the user confirmed: its size (app and data) as the page showed it, and its name.</summary>
public sealed record AppUninstallItem(string Id, long Size, string? Name = null) : IDeleteItem;

public sealed record AppsUninstallRequest(
    IReadOnlyList<AppUninstallItem> Items,
    long ExpectedBytes,
    string? CopyFirstTo = null,
    DeleteSource Source = DeleteSource.Apps,
    bool SkipChanged = false) : IDeleteRequest<AppUninstallItem>;

/// <summary>What starting a delete answers: its job, shown in the Transfers drawer.</summary>
public sealed record DeleteStarted(string JobId);

/// <summary>A delete job finished (done, failed or stopped) with these items gone.</summary>
/// <param name="Missing">Items it found no longer on the phone (left alone): gone too, but they freed nothing now.</param>
public sealed record DeleteFinished<TItem>(string JobId, string DeviceId, DeleteSource Source, IReadOnlyList<TItem> Gone, IReadOnlyList<TItem> Missing);

/// <summary>What a look at an item just before it goes found: still as confirmed, changed, or gone.</summary>
public enum ItemCheck { Same, Changed, Missing }

/// <summary>Thrown by a handler's delete of one item that is no longer as the user confirmed it: it is left alone.</summary>
public sealed class ItemLeftAloneException(ItemCheck why, string message) : Exception(message)
{
    public ItemCheck Why { get; } = why;
}

/// <summary>One item of a job as the page reads it afterwards (transfers.items): a delete's results.</summary>
public sealed record TransferItemView(int Index, string Name, string Source, long Size, TransferItemState State, string? Code, string? Message)
{
    public static TransferItemView Of(TransferItem item) => new(item.Index, item.Name, item.Source, item.Size, item.State, item.ErrorCode, item.ErrorMessage);
}
