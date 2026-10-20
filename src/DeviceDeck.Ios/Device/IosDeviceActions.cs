using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Services;

namespace DeviceDeck.Ios.Device;

/// <summary>Restart and shut down through diagnostics_relay. Screenshot needs Developer Mode and comes later.</summary>
internal sealed class IosDeviceActions(IosDevice device) : IDeviceActions
{
    public Task RebootAsync(RebootTarget target, CancellationToken ct)
    {
        if (target != RebootTarget.System)
            throw IosErrors.NotSupported("An iPhone can only be restarted normally from here.");
        return device.UseAsync(async c =>
        {
            await using var relay = await c.StartServiceAsync(ServiceNames.DiagnosticsRelay, ct).ConfigureAwait(false);
            await DiagnosticsRelayClient.RestartAsync(relay, ct).ConfigureAwait(false);
        });
    }

    public Task ShutdownAsync(CancellationToken ct) => device.UseAsync(async c =>
    {
        await using var relay = await c.StartServiceAsync(ServiceNames.DiagnosticsRelay, ct).ConfigureAwait(false);
        await DiagnosticsRelayClient.ShutdownAsync(relay, ct).ConfigureAwait(false);
    });

    public Task<byte[]> TakeScreenshotAsync(CancellationToken ct) =>
        throw IosErrors.NotSupported("Screenshots of an iPhone need Developer Mode; they come in a later version.");
}
