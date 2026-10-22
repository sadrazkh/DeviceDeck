using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.PhotoDelete;

/// <summary>
/// Deleting photos from Photos and Clean-up (round 4; a transfer job since v0.4.1): whether a phone lets the PC delete
/// them, starting the confirmed delete as a <see cref="TransferKind.PhotosDelete"/> job, and its results. The job
/// itself runs in <see cref="PhotoDeleteHandler"/>, registered with the queue (TransferHandlers.All).
/// </summary>
public sealed class PhotoDeleteService(DeviceHub hub, TransferQueue transfers, SettingsStore settings)
{
    /// <summary>The queue's delete handler, or null when the queue runs without one.</summary>
    public PhotoDeleteHandler? Handler { get; } = transfers.HandlerOf<PhotoDeleteHandler>();

    public async Task<PhotoDeleteSupport> GetSupportAsync(string deviceId, bool recheck, CancellationToken ct) =>
        await PhotoDeletes.SupportOf(hub.GetUsableDevice(deviceId), recheck, ct).ConfigureAwait(false);

    /// <summary>
    /// Queues the delete of exactly these items, each at the size the page showed, after an optional copy to the PC that
    /// must finish first. Bad input and a phone that can't delete answer at once; the delete runs in the background.
    /// </summary>
    public async Task<PhotoDeleteStarted> StartAsync(string deviceId, IReadOnlyList<PhotoDeleteItem> items, long expectedBytes, string? copyFirstTo,
        PhotoDeleteSource source, int skipped, CancellationToken ct)
    {
        if (Handler is null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "This DeviceDeck can't delete photos.");
        }

        var request = new PhotosDeleteRequest(items, expectedBytes, copyFirstTo, settings.Current.PhotoFolderPattern, source, skipped);
        var job = await transfers.EnqueueAsync(deviceId, TransferKind.PhotosDelete, request, ConflictRule.KeepBoth, ct).ConfigureAwait(false);
        return new PhotoDeleteStarted(job.Id, skipped);
    }

    /// <summary>A delete job's results so far; after a restart, from the job's saved items.</summary>
    public PhotoDeleteJobStatus Status(string jobId)
    {
        var job = transfers.Get(jobId) ?? throw new ArgumentException("No transfer has that id.");
        if (job.Kind != TransferKind.PhotosDelete || transfers.Details(jobId) is not { } saved)
        {
            throw new ArgumentException("That transfer isn't a photo delete.");
        }

        using var json = System.Text.Json.JsonDocument.Parse(saved.RequestJson);
        var request = PhotoDeleteHandler.Read(json.RootElement);
        var memory = Handler?.MemoryOf(jobId);
        var results = saved.Items.OrderBy(i => i.Index)
            .Select(i => memory?.Results.ElementAtOrDefault(i.Index) ?? PhotoDeleteHandler.FromItem(i))
            .ToList();
        var deleted = results.Where(r => r.Outcome == PhotoDeleteOutcome.Deleted).ToList();
        return new PhotoDeleteJobStatus(job.Id, job.DeviceId, request.Source, job.State, job.Stage, results, deleted.Count, deleted.Sum(r => r.Size),
            memory?.GoesTo ?? PhotoDeleteDestination.Unknown, memory?.CopyJobIds.ToList() ?? [], memory?.CopyIncomplete ?? false, request.Skipped, job.Error);
    }
}
