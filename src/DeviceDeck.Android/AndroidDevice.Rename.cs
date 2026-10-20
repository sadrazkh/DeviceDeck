using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

/// <summary>device.rename: Settings › About phone › Device name (global device_name; adb's shell user may write it).</summary>
internal sealed partial class AndroidDevice : IDeviceRenamer
{
    /// <summary>Raised after a rename with the new summary; the provider passes it on as a Changed event.</summary>
    internal event Action<DeviceSummary>? Renamed;

    public async Task RenameAsync(string name, CancellationToken ct)
    {
        EnsureReady();
        var trimmed = DeviceNames.Validate(name);
        await Channel.ShellCheckedAsync($"settings put global device_name {ShellQuote.Quote(trimmed)}", ct).ConfigureAwait(false);

        var old = RequireProfile();
        var profile = new DeviceProfile(old.Props, trimmed, old.UtcOffset);
        Volatile.Write(ref _profile, profile);
        var summary = Summary with { Name = profile.DisplayName };
        SetSummary(summary);
        Renamed?.Invoke(summary);
    }
}
