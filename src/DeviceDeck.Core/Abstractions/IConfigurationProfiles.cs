namespace DeviceDeck.Core.Abstractions;

/// <summary>A configuration profile a phone has installed, as its Settings lists it.</summary>
public sealed record InstalledProfile(string Identifier, string Name, string? Organization);

/// <summary>A phone that takes configuration profiles over the cable (an iPhone).</summary>
public interface IConfigurationProfiles
{
    Task<IReadOnlyList<InstalledProfile>> ListProfilesAsync(CancellationToken ct);

    /// <summary>Hands a profile to the phone. It isn't installed until the user finishes in the phone's own Settings.</summary>
    Task SendProfileAsync(byte[] profile, CancellationToken ct);
}
