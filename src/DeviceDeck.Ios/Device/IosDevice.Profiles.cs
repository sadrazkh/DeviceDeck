using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Toolbox;

namespace DeviceDeck.Ios.Device;

// Configuration profiles of an iPhone: which are installed, and handing it a new one to install.
public sealed partial class IosDevice : IConfigurationProfiles
{
    public Task<IReadOnlyList<InstalledProfile>> ListProfilesAsync(CancellationToken ct) => UseAsync(async c =>
    {
        await using var connection = await c.StartServiceAsync(McInstall.ServiceName, ct).ConfigureAwait(false);
        return await McInstall.ListAsync(connection, ct).ConfigureAwait(false);
    });

    public Task SendProfileAsync(byte[] profile, CancellationToken ct) => UseAsync(async c =>
    {
        await using var connection = await c.StartServiceAsync(McInstall.ServiceName, ct).ConfigureAwait(false);
        await McInstall.SendAsync(connection, profile, ct).ConfigureAwait(false);
    });
}
