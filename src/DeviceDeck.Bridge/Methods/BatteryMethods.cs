using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Battery;
using DeviceDeck.Core.Settings;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "Battery history": a snapshot each time a phone connects, the history, the exported report.

public sealed record BatteryExportParams(string DeviceId, string Destination, string? Language = null);

public static class BatteryMethods
{
    /// <summary>Registers battery.*; does nothing in a host without a <see cref="BatteryRecorder"/>.</summary>
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        if (s.Battery is not { } battery)
        {
            return;
        }

        rpc.Register("battery.devices", _ => Task.FromResult(battery.Store.Devices()));
        rpc.Register<DeviceParams, IReadOnlyList<BatterySnapshot>>("battery.history", (p, _) => Task.FromResult(battery.Store.List(p.DeviceId)));
        rpc.Register<DeviceParams, BatterySnapshot>("battery.record", (p, ct) => battery.RecordNowAsync(p.DeviceId, ct));
        rpc.Register<BatteryExportParams, PathResult>("battery.export", async (p, ct) =>
        {
            var language = p.Language ?? (s.Settings.Current.Language == AppLanguage.Fa ? "fa" : "en");
            if (language is not ("en" or "fa"))
            {
                throw new ArgumentException("language must be \"en\" or \"fa\".");
            }

            var history = battery.Store.List(p.DeviceId);
            return new PathResult(await BatteryReport.WriteAsync(p.Destination, history, language, DateTimeOffset.Now, s.Version, ct).ConfigureAwait(false));
        });
    }

    /// <summary>Pushes battery.recorded to the page; dispose to stop.</summary>
    public static IDisposable PublishEvents(BridgeServices s, Action<string, object> publish)
    {
        if (s.Battery is not { } battery)
        {
            return new CleanupMethods.Unsubscribe(null);
        }

        void OnRecorded(object? sender, BatterySnapshot snapshot) => publish("battery.recorded", snapshot);
        battery.Recorded += OnRecorded;
        return new CleanupMethods.Unsubscribe(() => battery.Recorded -= OnRecorded);
    }
}
