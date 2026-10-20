using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;

namespace DeviceDeck.Bridge.Methods;

public sealed record AppFilesMeasureParams(string DeviceId, string Root, IReadOnlyList<string> Paths);

/// <summary>
/// appFiles.* of docs/bridge-contract.md ("App files"). Copies in and out use files.copyToDevice and files.copyToPc
/// on the folders' root keys; these two list the apps and count what a folder holds.
/// </summary>
public static class AppFilesMethods
{
    private const int MaxPaths = 1000;

    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        var hub = s.Hub;

        rpc.Register<DeviceParams, AppFilesOverview>("appFiles.list", (p, ct) =>
        {
            var device = hub.GetUsableDevice(p.DeviceId);
            if ((device.Summary.Capabilities & DeviceCapability.AppDocuments) == 0)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't share files with apps.");
            }

            return SourceOf(device).GetAppFilesAsync(ct);
        });

        rpc.Register<AppFilesMeasureParams, FolderUsage>("appFiles.measure", (p, ct) =>
        {
            if (string.IsNullOrEmpty(p.Root))
            {
                throw new ArgumentException("root is required.");
            }

            if (p.Paths is null || p.Paths.Count is 0 or > MaxPaths)
            {
                throw new ArgumentException($"paths must hold between 1 and {MaxPaths} paths.");
            }

            var device = hub.GetUsableDevice(p.DeviceId);
            var needs = p.Root.StartsWith("app:", StringComparison.Ordinal) ? DeviceCapability.AppDocuments : DeviceCapability.Files;
            if ((device.Summary.Capabilities & needs) == 0)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't share that folder.");
            }

            return SourceOf(device).MeasureAsync(p.Root, p.Paths, ct);
        });
    }

    private static IAppFilesSource SourceOf(IDevice device) =>
        device as IAppFilesSource
        ?? throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't list app files with this version of DeviceDeck.");
}
