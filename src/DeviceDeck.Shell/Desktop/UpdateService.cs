using DeviceDeck.Bridge;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Hosting;

namespace DeviceDeck.Shell.Desktop;

internal sealed class UpdateServiceOptions
{
    /// <summary>The first automatic check after start.</summary>
    public TimeSpan FirstCheck { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Then a Setup copy checks (and downloads) with Velopack every 6 hours…</summary>
    public TimeSpan SetupEvery { get; init; } = TimeSpan.FromHours(6);

    /// <summary>…and the portable zip asks GitHub's API every 12 hours.</summary>
    public TimeSpan PortableEvery { get; init; } = TimeSpan.FromHours(12);

    /// <summary>False: no automatic checks at all (a dev build, DEVICEDECK_UPDATE_FEED=off). Check now still works.</summary>
    public bool AutomaticChecks { get; init; } = true;

    /// <summary>"What's new" once in this run: null decides from the version that ran last; true always; false never.</summary>
    public bool? WhatsNew { get; init; }

    /// <summary>DeviceDeck ran on this PC before (its data was there). With no update history yet, that is an update from v0.3.0 or older.</summary>
    public bool RanBefore { get; init; }

    public string Architecture { get; init; } = "x64";

    /// <summary>This build's own release notes (docs/releases/v{version}.md, embedded): "What's new" without GitHub.</summary>
    public string? BundledNotes { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Opens a page in the browser. Only GitHub release pages (<see cref="ReleaseLinks.ReleasePage"/>) are ever passed.</summary>
    public Action<string> OpenUrl { get; init; } = _ => { };
}

/// <summary>
/// Updates from GitHub Releases. A copy installed by the Setup updates itself with Velopack: with settings.autoUpdate on it
/// checks a minute after start and every six hours, downloads a new version in the background and says so (state Ready,
/// a Windows notification and the title bar); "Restart and update" applies it. A downloaded update that is never applied is
/// installed by Velopack the next time DeviceDeck starts. The portable zip can't update itself: it asks GitHub's API for the
/// latest release a minute after start and every twelve hours (state Available) and Download opens that release's page.
/// The first run of a newer version offers "What's new" once.
/// </summary>
internal sealed class UpdateService : IUpdateService, IDisposable
{
    public const string NotificationTag = "app:update";

    /// <summary>The portable copy's notification button: the new version's release page.</summary>
    public const string DownloadCommand = "app.downloadUpdate";

    private readonly string _version;
    private readonly SettingsStore _settings;
    private readonly INotificationService _notifications;
    private readonly Action _quit;
    private readonly IUpdateEngine _engine;
    private readonly GithubReleaseFeed _feed;
    private readonly UpdateHistory _history;
    private readonly UpdateServiceOptions _options;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _gate = new();
    private UpdateStatus _status;
    private UpdateOffer? _found;
    private UpdateOffer? _ready;
    private ITimer? _timer;

    /// <param name="feed">Owned: disposed with the service.</param>
    public UpdateService(string currentVersion, SettingsStore settings, INotificationService notifications, Action quit,
        IUpdateEngine engine, GithubReleaseFeed feed, UpdateHistory history, UpdateServiceOptions? options = null)
    {
        _version = currentVersion;
        _settings = settings;
        _notifications = notifications;
        _quit = quit;
        _engine = engine;
        _feed = feed;
        _history = history;
        _options = options ?? new UpdateServiceOptions();

        _ready = engine.IsInstalled ? engine.Pending : null;
        _found = _ready;
        var past = history.Current;
        var status = new UpdateStatus(UpdateState.Idle, currentVersion,
            LastChecked: past.LastChecked,
            Install: engine.IsInstalled ? UpdateInstall.Setup : UpdateInstall.Portable,
            Channel: currentVersion.Contains('-') ? "prerelease" : "stable",
            Architecture: _options.Architecture,
            LastResult: past.LastResult,
            WhatsNew: WhatsNewFor(past));
        if (_ready is not null)
        {
            status = status with { State = UpdateState.Ready, NewVersion = _ready.Version };
        }
        else if (!engine.IsInstalled && past.LatestVersion is { } latest && IsNewer(latest))
        {
            // The portable copy found it on an earlier run: say so at once, before this run's check.
            status = status with { State = UpdateState.Available, NewVersion = latest };
        }

        _status = status;
        if (past.LastRunVersion != currentVersion)
        {
            history.Update(h => h with { LastRunVersion = currentVersion });
        }

        notifications.Activated += OnNotificationActivated;
    }

    public UpdateStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public event EventHandler<UpdateStatus>? Changed;

    /// <summary>Starts the automatic checks (they only run while settings.autoUpdate is on).</summary>
    public void Start()
    {
        if (!_options.AutomaticChecks)
        {
            return;
        }

        var every = _engine.IsInstalled ? _options.SetupEvery : _options.PortableEvery;
        _timer = _options.Time.CreateTimer(_ =>
        {
            if (_settings.Current.AutoUpdate && Status.State is not (UpdateState.Ready or UpdateState.Downloading or UpdateState.Checking))
            {
                _ = CheckQuietlyAsync();
            }
        }, null, _options.FirstCheck, every);
    }

    private async Task CheckQuietlyAsync()
    {
        try
        {
            await RunCheckAsync(automatic: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShellLog.Error("The background update check failed.", ex);
        }
    }

    public Task<UpdateStatus> CheckAsync(CancellationToken ct) => RunCheckAsync(automatic: false, ct);

    private async Task<UpdateStatus> RunCheckAsync(bool automatic, CancellationToken ct)
    {
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status.State == UpdateState.Ready)
            {
                return Status;
            }

            return _engine.IsInstalled
                ? await CheckWithVelopackAsync(ct).ConfigureAwait(false)
                : await CheckGithubAsync(automatic, ct).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>A Setup copy: Velopack finds the new version for this channel and downloads it.</summary>
    private async Task<UpdateStatus> CheckWithVelopackAsync(CancellationToken ct)
    {
        var before = Status;
        Change(s => before with { WhatsNew = s.WhatsNew, State = UpdateState.Checking, Error = null, Percent = null, RetryAt = null });
        try
        {
            var offer = await _engine.CheckAsync(ct).ConfigureAwait(false);
            var checkedAt = _options.Time.GetUtcNow();
            if (offer is null)
            {
                Remember(checkedAt, UpdateCheckResult.UpToDate, _version);
                return Change(s => s with { State = UpdateState.UpToDate, NewVersion = null, LastChecked = checkedAt, LastResult = UpdateCheckResult.UpToDate });
            }

            ShellLog.Info($"Update {offer.Version} found; downloading.");
            lock (_gate)
            {
                _found = offer;
            }

            Remember(checkedAt, UpdateCheckResult.NewVersion, offer.Version);
            Change(s => s with { State = UpdateState.Downloading, NewVersion = offer.Version, Percent = 0, LastChecked = checkedAt, LastResult = UpdateCheckResult.NewVersion });
            await _engine.DownloadAsync(offer, percent => Change(s => s with { Percent = Math.Clamp(percent, 0, 100) }), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _ready = offer;
            }

            NotifyOnce(offer.Version, ready: true);
            return Change(s => s with { State = UpdateState.Ready, Percent = null });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Change(s => before with { WhatsNew = s.WhatsNew });
        }
        catch (Exception ex)
        {
            ShellLog.Error("Checking for updates failed.", ex);
            var checkedAt = _options.Time.GetUtcNow();
            Remember(checkedAt, UpdateCheckResult.Error, null);
            return Change(s => s with
            {
                State = UpdateState.Error,
                Percent = null,
                Error = "DeviceDeck couldn't reach GitHub to look for updates. Check the connection and try again.",
                LastChecked = checkedAt,
                LastResult = UpdateCheckResult.Error,
            });
        }
    }

    /// <summary>The portable zip: GitHub's latest release, compared with this version.</summary>
    private async Task<UpdateStatus> CheckGithubAsync(bool automatic, CancellationToken ct)
    {
        var before = Status;
        Change(s => before with { WhatsNew = s.WhatsNew, State = UpdateState.Checking, Error = null, RetryAt = null });
        FeedAnswer answer;
        try
        {
            answer = await _feed.LatestAsync(before.Channel == "prerelease", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Change(s => before with { WhatsNew = s.WhatsNew });
        }

        var checkedAt = _options.Time.GetUtcNow();
        switch (answer.Outcome)
        {
            case FeedOutcome.Fresh when answer.Release is { } release && IsNewer(release.Version):
                ShellLog.Info($"Version {release.Version} is on GitHub.");
                Remember(checkedAt, UpdateCheckResult.NewVersion, release.Version);
                NotifyOnce(release.Version, ready: false);
                return Change(s => s with { State = UpdateState.Available, NewVersion = release.Version, LastChecked = checkedAt, LastResult = UpdateCheckResult.NewVersion });

            case FeedOutcome.Fresh or FeedOutcome.NotFound:
                Remember(checkedAt, UpdateCheckResult.UpToDate, answer.Release?.Version ?? _version);
                return Change(s => s with { State = UpdateState.UpToDate, NewVersion = null, LastChecked = checkedAt, LastResult = UpdateCheckResult.UpToDate });

            case FeedOutcome.Waiting when automatic:
                // GitHub's hourly limit for this network is used up (other apps share it): the next automatic check asks again.
                ShellLog.Info($"Update check skipped: GitHub asked to wait until {answer.RetryAt:HH:mm}.");
                return Change(s => before with { WhatsNew = s.WhatsNew });

            default:
                ShellLog.Warn($"Checking GitHub for a new version failed: {answer.Error}");
                Remember(checkedAt, UpdateCheckResult.Error, null);
                return Change(s =>
                {
                    var failed = s with
                    {
                        Error = answer.Outcome == FeedOutcome.Waiting
                            ? "GitHub asked DeviceDeck to wait before checking again."
                            : "DeviceDeck couldn't reach GitHub to look for updates. Check the connection and try again.",
                        RetryAt = answer.RetryAt,
                        LastChecked = checkedAt,
                        LastResult = UpdateCheckResult.Error,
                    };

                    // A newer version found earlier stays on offer.
                    return before.State == UpdateState.Available
                        ? failed with { State = UpdateState.Available }
                        : failed with { State = UpdateState.Error, NewVersion = null };
                });
        }
    }

    /// <summary>Hands the downloaded version to Velopack's updater, which waits for this process to exit, then quits.</summary>
    public void ApplyAndRestart()
    {
        UpdateOffer? ready;
        lock (_gate)
        {
            ready = _ready;
        }

        if (!_engine.IsInstalled || ready is null)
        {
            throw new InvalidOperationException("No update has been downloaded yet.");
        }

        ShellLog.Info($"Restarting into {ready.Version}.");
        _engine.ApplyOnExit(ready);
        _quit();
    }

    public async Task<ReleaseNotes> GetReleaseNotesAsync(string version, CancellationToken ct)
    {
        var url = ReleaseLinks.ReleasePage(version);
        var answer = await _feed.ReleaseAsync(version, ct).ConfigureAwait(false);
        if (answer.Release is { Body: { } body } release && !string.IsNullOrWhiteSpace(body))
        {
            return new ReleaseNotes(version, url, release.Name, body, release.Published, answer.Outcome == FeedOutcome.Fresh ? "github" : "cache");
        }

        UpdateOffer? found;
        lock (_gate)
        {
            found = _found;
        }

        var bundled = version == _version ? _options.BundledNotes : found?.Version == version ? found.NotesMarkdown : null;
        if (!string.IsNullOrWhiteSpace(bundled))
        {
            return new ReleaseNotes(version, url, null, bundled, null, "app");
        }

        if (answer.Error is { } error)
        {
            ShellLog.Warn($"The release notes of {version} couldn't be had: {error}");
        }

        return new ReleaseNotes(version, url, answer.Release?.Name, null, answer.Release?.Published, "none");
    }

    public void OpenReleasePage(string? version)
    {
        var url = ReleaseLinks.ReleasePage(version);
        ShellLog.Info($"Opening {url}.");
        _options.OpenUrl(url);
    }

    public void MarkWhatsNewShown(string version)
    {
        _history.Update(h => h with { WhatsNewShown = version });
        if (Status.WhatsNew == version)
        {
            Change(s => s with { WhatsNew = null });
        }
    }

    private string? WhatsNewFor(UpdateHistoryData past)
    {
        if (!ReleaseLinks.IsVersion(_version) || _options.WhatsNew == false)
        {
            return null;
        }

        if (_options.WhatsNew == true)
        {
            return _version;
        }

        if (past.WhatsNewShown == _version)
        {
            return null;
        }

        var updated = past.LastRunVersion is { } previous
            ? ReleaseLinks.IsVersion(previous) && ReleaseLinks.IsNewer(_version, previous)
            : _options.RanBefore;
        return updated ? _version : null;
    }

    private bool IsNewer(string version) => ReleaseLinks.IsVersion(version) && ReleaseLinks.IsVersion(_version) && ReleaseLinks.IsNewer(version, _version);

    private void Remember(DateTimeOffset checkedAt, UpdateCheckResult result, string? latest) =>
        _history.Update(h => h with { LastChecked = checkedAt, LastResult = result, LatestVersion = latest ?? h.LatestVersion });

    /// <summary>A Windows notification, once per version (the in-app button and banner are the main signal).</summary>
    private void NotifyOnce(string version, bool ready)
    {
        if (_history.Current.NotifiedVersion == version)
        {
            return;
        }

        _history.Update(h => h with { NotifiedVersion = version });
        var text = ShellText.For(_settings.Current.Language);
        _notifications.Show(ready
            ? new AppNotification(string.Format(text.UpdateReady, version), text.UpdateReadyBody)
            {
                Tag = NotificationTag,
                Buttons = [new NotificationButton(text.Restart, NotificationCommands.ApplyUpdate)],
            }
            : new AppNotification(string.Format(text.UpdateOnGithub, version), text.UpdateOnGithubBody)
            {
                Tag = NotificationTag,
                Buttons = [new NotificationButton(text.Download, DownloadCommand)],
                Args = new Dictionary<string, string> { ["version"] = version },
            });
    }

    private void OnNotificationActivated(object? sender, NotificationActivation activation)
    {
        try
        {
            if (activation.Command == NotificationCommands.ApplyUpdate)
            {
                ApplyAndRestart();
            }
            else if (activation.Command == DownloadCommand)
            {
                OpenReleasePage(activation.Args.TryGetValue("version", out var version) && ReleaseLinks.IsVersion(version) ? version : Status.NewVersion);
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error($"The update notification's {activation.Command} failed.", ex);
        }
    }

    /// <summary>Changes the status atomically and tells the listeners.</summary>
    private UpdateStatus Change(Func<UpdateStatus, UpdateStatus> change)
    {
        UpdateStatus status;
        lock (_gate)
        {
            status = change(_status);
            if (status == _status)
            {
                return status;
            }

            _status = status;
        }

        try
        {
            Changed?.Invoke(this, status);
        }
        catch (Exception ex)
        {
            ShellLog.Error("An update listener failed.", ex);
        }

        return status;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _notifications.Activated -= OnNotificationActivated;
        _feed.Dispose();
    }
}
