using DeviceDeck.Core.Hub;
using DeviceDeck.Core.PhotoDelete;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>v0.4.1: a delete is a transfer job; tests wait for it to finish and read its results.</summary>
internal static class DeleteJobs
{
    public static bool IsFinished(TransferState state) => state is TransferState.Done or TransferState.Failed or TransferState.Cancelled;

    /// <summary>Waits for the delete job to finish and returns its results.</summary>
    public static async Task<PhotoDeleteJobStatus> FinishedAsync(DeviceHub hub, TransferQueue queue, SettingsStore settings, PhotoDeleteStarted started)
    {
        Assert.NotNull(started.JobId);
        var id = started.JobId!;
        await Wait.UntilAsync(() => queue.Get(id) is { } job && IsFinished(job.State), $"delete job {id} to finish (now {queue.Get(id)?.State} {queue.Get(id)?.Stage})");
        return new PhotoDeleteService(hub, queue, settings).Status(id);
    }

    public static Task<PhotoDeleteJobStatus> FinishedAsync(TestHost host, PhotoDeleteStarted started) =>
        FinishedAsync(host.Hub, host.Queue, host.Settings, started);

    /// <summary>v0.6: how a files or apps delete job ended, read from its items.</summary>
    public sealed record ItemJobResult(TransferState State, int Deleted, int Skipped, int Failed, long FreedBytes, TransferError? Error, IReadOnlyList<TransferItem> Items);

    /// <summary>Waits for a files or apps delete job (v0.6) to finish and returns how it ended.</summary>
    public static async Task<ItemJobResult> FinishedAsync(TransferQueue queue, Core.Deletes.DeleteStarted started)
    {
        var id = started.JobId;
        await Wait.UntilAsync(() => queue.Get(id) is { } job && IsFinished(job.State), $"delete job {id} to finish (now {queue.Get(id)?.State} {queue.Get(id)?.Stage})");
        var job = queue.Get(id)!;
        var items = queue.Details(id)!.Value.Items;
        return new ItemJobResult(job.State, items.Count(i => i.State == TransferItemState.Done), items.Count(i => i.State == TransferItemState.Skipped),
            items.Count(i => i.State == TransferItemState.Failed), items.Where(i => i.State == TransferItemState.Done).Sum(i => i.Size), job.Error, items);
    }

    public static Task<ItemJobResult> FinishedAsync(TestHost host, Core.Deletes.DeleteStarted started) => FinishedAsync(host.Queue, started);
}
