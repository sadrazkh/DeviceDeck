using System.Collections.Concurrent;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.PhotoDelete;

/// <param name="Items">One result per item the user confirmed, in order.</param>
/// <param name="FreedBytes">What the deleted items freed (a Live Photo's video included).</param>
/// <param name="CopyJobIds">The copy-to-PC job that ran first.</param>
/// <param name="CopyIncomplete">The copy didn't finish, so nothing was deleted.</param>
/// <param name="GoesTo">Where the deleted photos went (or, with nothing deleted, where they would have).</param>
public sealed record PhotoDeleteReport(
    IReadOnlyList<PhotoDeleteResult> Items,
    int Deleted,
    long FreedBytes,
    IReadOnlyList<string> CopyJobIds,
    bool CopyIncomplete,
    PhotoDeleteDestination GoesTo);

/// <summary>
/// The Photos screen's delete (round 4): exactly the photos the user confirmed, at the sizes the page showed, after an
/// optional copy to the PC that must finish first. One delete at a time per phone.
/// </summary>
public sealed class PhotoDeleteService(DeviceHub hub, TransferQueue transfers, SettingsStore settings)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _busy = new(StringComparer.Ordinal);

    public async Task<PhotoDeleteSupport> GetSupportAsync(string deviceId, bool recheck, CancellationToken ct) =>
        await PhotoDeletes.SupportOf(hub.GetUsableDevice(deviceId), recheck, ct).ConfigureAwait(false);

    public async Task<PhotoDeleteReport> DeleteAsync(string deviceId, IReadOnlyList<PhotoDeleteItem> items, long expectedBytes, string? copyFirstTo, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("items is empty.");
        }

        if (items.Any(i => i.Size < 0 || string.IsNullOrEmpty(i.Id)))
        {
            throw new ArgumentException("Every item needs an id and a size of 0 or more.");
        }

        if (items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw new ArgumentException("An item is listed twice.");
        }

        if (items.Sum(i => i.Size) != expectedBytes)
        {
            throw new ArgumentException($"expectedBytes ({expectedBytes}) isn't the total of the items ({items.Sum(i => i.Size)}).");
        }

        var device = hub.GetUsableDevice(deviceId);
        if (!device.Summary.Capabilities.HasFlag(DeviceCapability.PhotosDelete))
        {
            var support = await PhotoDeletes.SupportOf(device, recheck: false, ct).ConfigureAwait(false);
            throw new DeviceException(DeviceErrorCodes.NotSupported,
                $"{device.Summary.Name} doesn't let the PC delete its photos right now ({support.Reason}).");
        }

        var gate = _busy.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var before = await PhotoDeletes.SupportOf(device, recheck: false, ct).ConfigureAwait(false);
            var jobs = new List<string>();
            if (copyFirstTo is not null)
            {
                jobs.AddRange(await PhotoDeletes.EnqueueCopiesAsync(transfers, device, items.Select(i => i.Id).ToList(), copyFirstTo,
                    settings.Current.PhotoFolderPattern, ct).ConfigureAwait(false));
                if (!await WaitForCopiesAsync(jobs, ct).ConfigureAwait(false))
                {
                    var untouched = items.Select(i => new PhotoDeleteResult(i.Id, i.Id[(i.Id.LastIndexOf('/') + 1)..], i.Size, PhotoDeleteOutcome.NotTried,
                        null, "The copy to the PC didn't finish, so nothing was deleted.", [], PhotoDeleteDestination.Unknown)).ToList();
                    return new PhotoDeleteReport(untouched, 0, 0, jobs, true, before.GoesTo);
                }
            }

            var results = await PhotoDeletes.DeleteItemsAsync(device, items, null, ct).ConfigureAwait(false);
            var deleted = results.Where(r => r.Outcome == PhotoDeleteOutcome.Deleted).ToList();
            return new PhotoDeleteReport(results, deleted.Count, deleted.Sum(r => r.Size), jobs, false, PhotoDeletes.WentTo(results, before.GoesTo));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// True only when every job is done with every item copied; false as soon as one fails, is cancelled, or waits for an
    /// unplugged phone (it carries on by itself when the phone is back, but nothing is deleted).
    /// </summary>
    private async Task<bool> WaitForCopiesAsync(IReadOnlyList<string> jobIds, CancellationToken ct)
    {
        using var signal = new SemaphoreSlim(0);
        var watched = jobIds.ToHashSet(StringComparer.Ordinal);
        void OnJob(object? sender, TransferJob job)
        {
            if (watched.Contains(job.Id))
            {
                try
                {
                    signal.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        transfers.JobUpdated += OnJob;
        try
        {
            while (true)
            {
                var jobs = jobIds.Select(transfers.Get).ToList();
                if (jobs.Any(j => j is null || j.State is TransferState.Failed or TransferState.Cancelled or TransferState.WaitingForDevice or TransferState.NeedsDecision))
                {
                    return false;
                }

                if (jobs.All(j => j!.State == TransferState.Done))
                {
                    return jobs.All(j => j!.FailedItems == 0 && j.DoneItems == j.TotalItems);
                }

                await signal.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            transfers.JobUpdated -= OnJob;
        }
    }
}
