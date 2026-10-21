using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.PhotoDelete;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "Deleting photos (round 4)": whether a phone's photos can be deleted from the PC (and why
// not), and the confirmed delete with a result per photo.

public sealed record PhotosDeleteSupportParams(string DeviceId, bool Recheck = false);

public sealed record PhotosDeleteItemsParams(string DeviceId, IReadOnlyList<PhotoDeleteItem> Items, long ExpectedBytes, string? CopyFirstTo = null);

public static class PhotoDeleteMethods
{
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        var service = new PhotoDeleteService(s.Hub, s.Transfers, s.Settings);
        rpc.Register<PhotosDeleteSupportParams, PhotoDeleteSupport>("photos.deleteSupport", (p, ct) =>
            service.GetSupportAsync(p.DeviceId, p.Recheck, ct));
        rpc.Register<PhotosDeleteItemsParams, PhotoDeleteReport>("photos.deleteItems", (p, ct) =>
        {
            if (p.CopyFirstTo is { } folder && !Path.IsPathFullyQualified(folder))
            {
                throw new ArgumentException("copyFirstTo must be a full folder path on this PC.");
            }

            return service.DeleteAsync(p.DeviceId, p.Items ?? [], p.ExpectedBytes, p.CopyFirstTo, ct);
        });
    }
}
