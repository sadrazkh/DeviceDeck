namespace DeviceDeck.Shell.Desktop;

/// <summary>A newer version the update engine found or downloaded earlier.</summary>
/// <param name="NotesMarkdown">The release notes that came with it (vpk pack --releaseNotes), if any.</param>
/// <param name="Handle">The engine's own object for it (Velopack's UpdateInfo or VelopackAsset).</param>
internal sealed record UpdateOffer(string Version, string? NotesMarkdown, object? Handle = null);

/// <summary>What <see cref="UpdateService"/> needs from Velopack (UpdateVelopack.cs). Tests fake it.</summary>
internal interface IUpdateEngine
{
    /// <summary>False for the portable zip and a build run from the source tree: those only check GitHub.</summary>
    bool IsInstalled { get; }

    /// <summary>A version downloaded in an earlier run that is still waiting for a restart.</summary>
    UpdateOffer? Pending { get; }

    /// <summary>A newer version on GitHub Releases for this copy's channel, or null.</summary>
    Task<UpdateOffer?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads it next to the app; progress is 0–100.</summary>
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct);

    /// <summary>Hands the downloaded version to the updater, which waits for this process to exit, installs it and starts it.</summary>
    void ApplyOnExit(UpdateOffer offer);
}
