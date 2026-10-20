using DeviceDeck.Bridge;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Hosting;
using Velopack;
using Velopack.Sources;

namespace DeviceDeck.Shell.Desktop;

/// <summary>
/// Updates from GitHub Releases with Velopack. Only a copy installed by the Setup can update itself; the portable
/// zip reports NotInstalled. With settings.autoUpdate on, it checks a minute after start and every six hours, and
/// downloads a new version in the background; "Restart" (Settings or the notification) applies it. A downloaded
/// update that is never applied is installed by Velopack the next time DeviceDeck starts.
/// </summary>
internal sealed class UpdateService : IUpdateService, IDisposable
{
    public const string Repository = "https://github.com/sadrazkh/DeviceDeck";
    public const string NotificationTag = "app:update";

    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private readonly SettingsStore _settings;
    private readonly INotificationService _notifications;
    private readonly Action _quit;
    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _gate = new();
    private UpdateStatus _status;
    private VelopackAsset? _ready;
    private Timer? _timer;

    public UpdateService(string currentVersion, SettingsStore settings, INotificationService notifications, Action quit)
    {
        _settings = settings;
        _notifications = notifications;
        _quit = quit;
        try
        {
            var manager = new UpdateManager(new GithubSource(Repository, null, prerelease: currentVersion.Contains('-')));
            if (manager.IsInstalled)
            {
                _manager = manager;
                _ready = manager.UpdatePendingRestart;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error("The update manager could not start.", ex);
        }

        _status = _manager is null
            ? new UpdateStatus(UpdateState.NotInstalled, currentVersion)
            : _ready is { } pending
                ? new UpdateStatus(UpdateState.Ready, currentVersion, pending.Version.ToString())
                : new UpdateStatus(UpdateState.Idle, currentVersion);
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

    /// <summary>Starts the background checks (they only run while settings.autoUpdate is on).</summary>
    public void Start()
    {
        if (_manager is null)
        {
            return;
        }

        _timer = new Timer(_ =>
        {
            if (_settings.Current.AutoUpdate && Status.State is not (UpdateState.Ready or UpdateState.Downloading))
            {
                _ = CheckQuietlyAsync();
            }
        }, null, FirstCheck, Every);
    }

    private async Task CheckQuietlyAsync()
    {
        try
        {
            await CheckAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ShellLog.Error("The background update check failed.", ex);
        }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        if (_manager is null)
        {
            return Status;
        }

        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status.State == UpdateState.Ready)
            {
                return Status;
            }

            Set(Status with { State = UpdateState.Checking, Error = null, Percent = null });
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            var checkedAt = DateTimeOffset.Now;
            if (update is null)
            {
                return Set(Status with { State = UpdateState.UpToDate, NewVersion = null, LastChecked = checkedAt });
            }

            var version = update.TargetFullRelease.Version.ToString();
            ShellLog.Info($"Update {version} found; downloading.");
            Set(Status with { State = UpdateState.Downloading, NewVersion = version, Percent = 0, LastChecked = checkedAt });
            await _manager.DownloadUpdatesAsync(update, percent => Set(Status with { Percent = percent }), ct).ConfigureAwait(false);
            lock (_gate)
            {
                _ready = update.TargetFullRelease;
            }

            var text = ShellText.For(_settings.Current.Language);
            _notifications.Show(new AppNotification(string.Format(text.UpdateReady, version), text.UpdateReadyBody)
            {
                Tag = NotificationTag,
                Buttons = [new NotificationButton(text.Restart, NotificationCommands.ApplyUpdate)],
            });
            return Set(Status with { State = UpdateState.Ready, Percent = null });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Set(Status with { State = UpdateState.Idle, Percent = null });
        }
        catch (Exception ex)
        {
            ShellLog.Error("Checking for updates failed.", ex);
            return Set(Status with
            {
                State = UpdateState.Error,
                Percent = null,
                Error = "DeviceDeck couldn't reach GitHub to look for updates. Check the connection and try again.",
            });
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Hands the downloaded version to Velopack's updater, which waits for this process to exit, then quits.</summary>
    public void ApplyAndRestart()
    {
        VelopackAsset? ready;
        lock (_gate)
        {
            ready = _ready;
        }

        if (_manager is null || ready is null)
        {
            throw new InvalidOperationException("No update has been downloaded yet.");
        }

        ShellLog.Info($"Restarting into {ready.Version}.");
        _manager.WaitExitThenApplyUpdates(ready, silent: false, restart: true);
        _quit();
    }

    private UpdateStatus Set(UpdateStatus status)
    {
        lock (_gate)
        {
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
    }
}
