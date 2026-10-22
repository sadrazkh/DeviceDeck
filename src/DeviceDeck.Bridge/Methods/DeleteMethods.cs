using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Deletes;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "Every delete as a transfer (v0.6)": files and folders, and apps, deleted by a job in the
// Transfers drawer (the confirmation closes at once), and any job's items afterwards (a delete's results).

public sealed record FilesDeleteItemsParams(string DeviceId, IReadOnlyList<FileDeleteItem> Items, long ExpectedBytes);

public sealed record AppsUninstallItemsParams(string DeviceId, IReadOnlyList<AppUninstallItem> Items, long ExpectedBytes, string? CopyFirstTo = null);

public static class DeleteMethods
{
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        rpc.Register<FilesDeleteItemsParams, DeleteStarted>("files.deleteItems", async (p, ct) =>
        {
            var request = new FilesDeleteRequest(p.Items ?? [], p.ExpectedBytes);
            var job = await s.Transfers.EnqueueAsync(p.DeviceId, TransferKind.FilesDelete, request, ConflictRule.KeepBoth, ct).ConfigureAwait(false);
            return new DeleteStarted(job.Id);
        });
        rpc.Register<AppsUninstallItemsParams, DeleteStarted>("apps.uninstallItems", async (p, ct) =>
        {
            var request = new AppsUninstallRequest(p.Items ?? [], p.ExpectedBytes, p.CopyFirstTo);
            var job = await s.Transfers.EnqueueAsync(p.DeviceId, TransferKind.AppsUninstall, request, ConflictRule.KeepBoth, ct).ConfigureAwait(false);
            return new DeleteStarted(job.Id);
        });
        rpc.Register<JobParams, IReadOnlyList<TransferItemView>>("transfers.items", (p, _) =>
        {
            var details = s.Transfers.Details(p.JobId) ?? throw new ArgumentException("No transfer has that id.");
            return Task.FromResult<IReadOnlyList<TransferItemView>>(details.Items.OrderBy(i => i.Index).Select(TransferItemView.Of).ToList());
        });
    }
}
