using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.LocalSend;

namespace DeviceDeck.Bridge.LocalSend;

// docs/bridge-contract.md, "Photos into Photos with LocalSend (round 3)".

public sealed record LocalSendDiscoverParams(string DeviceId, bool Wifi = false, bool Rescan = false);

public sealed record LocalSendSendParams(string DeviceId, LocalSendTarget Target, IReadOnlyList<string> Files);

public sealed record LocalSendRetryParams(string JobId, string? Pin = null);

/// <summary>localsend.discover, localsend.send, localsend.retry, localsend.job; the localsend.updated event.</summary>
public static class LocalSendMethods
{
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        rpc.Register<LocalSendDiscoverParams, LocalSendDiscovery>("localsend.discover", (p, ct) => Service(s).DiscoverAsync(p.DeviceId, p.Wifi, ct, p.Rescan));
        rpc.Register<LocalSendSendParams, JobIdResult>("localsend.send", async (p, ct) =>
        {
            if (p.Target is null)
            {
                throw new ArgumentException("target is required.");
            }

            var job = await Service(s).SendAsync(p.DeviceId, p.Target, p.Files, ct).ConfigureAwait(false);
            return new JobIdResult(job.Id);
        });
        rpc.Register<LocalSendRetryParams, Empty>("localsend.retry", (p, _) =>
        {
            Service(s).Retry(p.JobId, p.Pin);
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<JobParams, LocalSendJobStatus>("localsend.job", (p, _) => Task.FromResult(Service(s).GetStatus(p.JobId)));
    }

    public static IDisposable PublishEvents(BridgeServices s, Action<string, object> publish)
    {
        if (s.LocalSend is not { } service)
        {
            return new CleanupMethods.Unsubscribe(null);
        }

        void OnUpdated(object? sender, LocalSendJobStatus status) => publish("localsend.updated", status);
        service.Updated += OnUpdated;
        return new CleanupMethods.Unsubscribe(() => service.Updated -= OnUpdated);
    }

    private static LocalSendService Service(BridgeServices s) =>
        s.LocalSend ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "LocalSend isn't available in this build.");
}
