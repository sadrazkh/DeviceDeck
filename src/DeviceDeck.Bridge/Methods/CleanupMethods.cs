using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Cleanup;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "Clean-up": analysis per phone, then deletes the user confirmed item by item.

public sealed record CleanupDeleteMediaParams(string DeviceId, IReadOnlyList<CleanupItem> Items, long ExpectedBytes, string? CopyFirstTo = null);

public sealed record CleanupDeleteFilesParams(string DeviceId, IReadOnlyList<CleanupFileItem> Items, long ExpectedBytes, string? CopyFirstTo = null);

public sealed record CleanupUninstallParams(string DeviceId, IReadOnlyList<CleanupItem> Items, long ExpectedBytes, string? CopyFirstTo = null);

public static class CleanupMethods
{
    /// <summary>Registers cleanup.*; does nothing in a host without a <see cref="CleanupService"/>.</summary>
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        if (s.Cleanup is not { } cleanup)
        {
            return;
        }

        rpc.Register<DeviceParams, CleanupStatus>("cleanup.status", (p, _) => Task.FromResult(cleanup.GetStatus(p.DeviceId)));
        rpc.Register<DeviceParams, CleanupStatus>("cleanup.analyze", (p, _) => Task.FromResult(cleanup.Analyze(p.DeviceId)));
        rpc.Register<DeviceParams, Empty>("cleanup.cancel", (p, _) =>
        {
            cleanup.Cancel(p.DeviceId);
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<CleanupDeleteMediaParams, Core.PhotoDelete.PhotoDeleteStarted>("cleanup.deleteMedia", (p, ct) =>
            cleanup.DeleteMediaAsync(p.DeviceId, p.Items, p.ExpectedBytes, PcFolder(p.CopyFirstTo), ct));
        // v0.6: files and apps go to the Transfers drawer as delete jobs too.
        rpc.Register<CleanupDeleteFilesParams, Core.Deletes.DeleteStarted>("cleanup.deleteFiles", (p, ct) =>
            cleanup.DeleteFilesAsync(p.DeviceId, p.Items, p.ExpectedBytes, PcFolder(p.CopyFirstTo), ct));
        rpc.Register<CleanupUninstallParams, Core.Deletes.DeleteStarted>("cleanup.uninstallApps", (p, ct) =>
            cleanup.UninstallAppsAsync(p.DeviceId, p.Items, p.ExpectedBytes, PcFolder(p.CopyFirstTo), ct));
        rpc.Register<DeviceParams, CleanupResult>("cleanup.trimCaches", (p, ct) => cleanup.TrimCachesAsync(p.DeviceId, ct));
    }

    /// <summary>Pushes cleanup.updated to the page; dispose to stop.</summary>
    public static IDisposable PublishEvents(BridgeServices s, Action<string, object> publish)
    {
        if (s.Cleanup is not { } cleanup)
        {
            return new Unsubscribe(null);
        }

        void OnUpdated(object? sender, CleanupStatus status) => publish("cleanup.updated", status);
        cleanup.Updated += OnUpdated;
        return new Unsubscribe(() => cleanup.Updated -= OnUpdated);
    }

    private static string? PcFolder(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("copyFirstTo must be a full folder path on this PC.");
        }

        return path;
    }

    internal sealed class Unsubscribe(Action? undo) : IDisposable
    {
        private Action? _undo = undo;

        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }
}
