namespace DeviceDeck.Bridge;

// What the desktop window adds on top of IShellServices (round 2): drag out to File Explorer, opening a PC file,
// and app updates. The Shell implements it; tests use a fake. See docs/bridge-contract.md, "Desktop".

/// <summary>One dragged photo or phone file, as the page knows it (so nothing has to be looked up on the phone first).</summary>
/// <param name="Id">The asset id, or the file's path in its root.</param>
/// <param name="Size">Bytes; 0 when unknown.</param>
public sealed record DragOutEntry(string Id, string Name, long Size = 0, bool IsDirectory = false, DateTimeOffset? Modified = null);

/// <summary>shell.dragOut: what is being dragged out of the page.</summary>
/// <param name="Source">"photos" (Items of DeviceId), "files" (Items, or bare Paths, in Root of DeviceId) or "pc" (Paths on this PC).</param>
public sealed record DragOutParams(string Source, string? DeviceId = null, string? Root = null, IReadOnlyList<DragOutEntry>? Items = null, IReadOnlyList<string>? Paths = null);

/// <summary>Where the drag ended, in the page's CSS pixels, when it was dropped back on the window itself.</summary>
public sealed record DragPoint(double X, double Y);

/// <param name="Effect">"copy" when another app (File Explorer) took the drop, "none" when it was cancelled or dropped back on the window.</param>
/// <param name="DroppedInside">Set when the drop landed on DeviceDeck's own window: the page decides what that means there.</param>
public sealed record DragOutResult(string Effect, DragPoint? DroppedInside);

/// <summary>
/// Idle: not checked yet in this run. Ready: downloaded, a restart installs it (a Setup copy). Available: a newer version
/// is on GitHub that this copy can't install by itself (the portable zip): Download opens its release page.
/// </summary>
public enum UpdateState { Idle, Checking, UpToDate, Downloading, Ready, Available, Error }

/// <summary>How this copy runs: installed by the Setup (it updates itself) or from the portable zip (it only checks GitHub).</summary>
public enum UpdateInstall { Setup, Portable }

/// <summary>What the last check found; kept across restarts.</summary>
public enum UpdateCheckResult { UpToDate, NewVersion, Error }

/// <param name="NewVersion">The version found on GitHub Releases, once there is one.</param>
/// <param name="Percent">Download progress while <see cref="UpdateState.Downloading"/>.</param>
/// <param name="Error">One plain English sentence while <see cref="UpdateState.Error"/> (the page shows its own words).</param>
/// <param name="LastChecked">When a check last finished, also in an earlier run.</param>
/// <param name="Channel">"stable", or "prerelease" for a version like 0.4.0-beta.1.</param>
/// <param name="Architecture">"x64" or "arm64": the build this copy runs, so the one it updates to.</param>
/// <param name="LastResult">What the check at <paramref name="LastChecked"/> found.</param>
/// <param name="RetryAt">Set with <see cref="UpdateState.Error"/> when GitHub asked to wait (its limit for checks from this network).</param>
/// <param name="WhatsNew">This run is the first of a newer version: the page shows "What's new in v{WhatsNew}" once.</param>
public sealed record UpdateStatus(UpdateState State, string CurrentVersion, string? NewVersion = null, int? Percent = null, string? Error = null, DateTimeOffset? LastChecked = null,
    UpdateInstall Install = UpdateInstall.Setup, string Channel = "stable", string Architecture = "x64", UpdateCheckResult? LastResult = null,
    DateTimeOffset? RetryAt = null, string? WhatsNew = null);

/// <summary>Updates from GitHub Releases (Velopack in the Shell). Only a copy installed by the Setup can update itself.</summary>
public interface IUpdateService
{
    UpdateStatus Status { get; }

    /// <summary>Checks now and, when there is a newer version, downloads it (Setup) or reports it (portable). Returns the status after the check.</summary>
    Task<UpdateStatus> CheckAsync(CancellationToken ct);

    /// <summary>Restarts into the downloaded version (state Ready); anything else throws <see cref="InvalidOperationException"/>.</summary>
    void ApplyAndRestart();

    /// <summary>A version's release notes: from GitHub (cached with its ETag), else what came with the app or the update.</summary>
    Task<ReleaseNotes> GetReleaseNotesAsync(string version, CancellationToken ct);

    /// <summary>Opens <see cref="ReleaseLinks.ReleasePage"/> in the browser; nothing else is ever opened.</summary>
    void OpenReleasePage(string? version);

    /// <summary>The page showed "What's new" for this version: it isn't offered by itself again.</summary>
    void MarkWhatsNewShown(string version);

    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event EventHandler<UpdateStatus>? Changed;
}

public interface IDesktopShell
{
    /// <summary>Starts a native drag of the items; returns when it ends. Copies to File Explorer run in the transfer queue.</summary>
    Task<DragOutResult> DragOutAsync(DragOutParams request, CancellationToken ct);

    /// <summary>Opens a PC file with its default app.</summary>
    void OpenFile(string path);

    IUpdateService Updates { get; }
}
