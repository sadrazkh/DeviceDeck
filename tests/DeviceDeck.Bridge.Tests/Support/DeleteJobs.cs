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
}
