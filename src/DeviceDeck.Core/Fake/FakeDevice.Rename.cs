using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

public sealed partial class FakeDevice : IDeviceRenamer
{
    private static DeviceSummary WithRename(DeviceSummary summary) =>
        summary with { Capabilities = summary.Capabilities | DeviceCapability.Rename };

    public Task RenameAsync(string name, CancellationToken ct)
    {
        EnsureConnected();
        var trimmed = DeviceNames.Validate(name);
        SetSummary(Summary with { Name = trimmed });

        // Same state again: announces the new name as a Changed event.
        _provider.SetState(Summary.Id, Summary.State);
        return Task.CompletedTask;
    }
}
