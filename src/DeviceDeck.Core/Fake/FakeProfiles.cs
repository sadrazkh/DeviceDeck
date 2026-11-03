using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Toolbox;

namespace DeviceDeck.Core.Fake;

// The configuration profiles of a simulated iPhone. A real phone lists a profile only after the user finishes in its
// Settings; here that step counts as done at once.
public sealed partial class FakeDevice : IConfigurationProfiles
{
    private readonly List<InstalledProfile> _profiles = [];

    /// <summary>What <see cref="SendProfileAsync"/> received, for tests.</summary>
    public List<byte[]> SentProfiles { get; } = [];

    public Task<IReadOnlyList<InstalledProfile>> ListProfilesAsync(CancellationToken ct)
    {
        EnsureConnected();
        lock (_profiles)
        {
            return Task.FromResult<IReadOnlyList<InstalledProfile>>([.. _profiles]);
        }
    }

    public Task SendProfileAsync(byte[] profile, CancellationToken ct)
    {
        EnsureConnected();
        lock (_profiles)
        {
            SentProfiles.Add(profile);
            _profiles.RemoveAll(p => p.Identifier == CellularProfile.Identifier);
            _profiles.Add(new InstalledProfile(CellularProfile.Identifier, "IPv6", null));
        }

        return Task.CompletedTask;
    }
}
