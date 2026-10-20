using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Device;

/// <summary>device.rename (lockdown SetValue DeviceName) and the plain sentence shown under a state that needs the user.</summary>
public sealed partial class IosDevice : IDeviceRenamer
{
    public const string TrustDeniedDetail = "Don't Trust was tapped · unplug the iPhone and plug it in again, then tap Trust";
    public const string AwaitingTrustDetail = "Unlock the iPhone and tap Trust when it asks";
    public const string LockedDetail = "Unlock the iPhone to use it here";

    public async Task RenameAsync(string name, CancellationToken ct)
    {
        var trimmed = DeviceNames.Validate(name);
        await UseAsync(async c =>
        {
            await using var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false);
            await lockdown.RequestAsync("SetValue", new PlistDict { ["Key"] = "DeviceName", ["Value"] = trimmed }, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        // The handshake loop replaces _values wholesale; a copy keeps a reader of the old one safe.
        _values = new PlistDict(_values) { ["DeviceName"] = trimmed };
        UpdateSummary(Summary.State);
    }

    private string? StateDetailFor(DeviceState state) => state switch
    {
        DeviceState.AwaitingTrust when _pairingDenied => TrustDeniedDetail,
        DeviceState.AwaitingTrust => AwaitingTrustDetail,
        DeviceState.Locked => LockedDetail,
        _ => null,
    };
}
