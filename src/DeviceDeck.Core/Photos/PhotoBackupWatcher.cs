using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Photos;

/// <summary>Commands on the photo backup's notifications; the page also sees them (notification.activated).</summary>
public static class PhotoBackupCommands
{
    /// <summary>"Back up now" on the new-photos notification: the host starts the backup.</summary>
    public const string BackUpNow = "photos.backupNow";

    /// <summary>"Open folder" on the finished notification, with the folder in <see cref="FolderArg"/>.</summary>
    public const string OpenFolder = "photos.openFolder";

    /// <summary>"Open DeviceDeck" when a backup didn't start or stopped: the page opens the phone's Back up panel.</summary>
    public const string OpenPanel = "photos.openBackup";

    public const string FolderArg = "folder";
}

public sealed class PhotoBackupWatcherOptions
{
    /// <summary>The pause after a phone turns ready before its photos are looked at (the app's own first calls go first).</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Opens a folder in File Explorer (the finished notification's "Open folder").</summary>
    public Action<string>? OpenFolder { get; init; }
}

/// <summary>
/// "When this phone connects" (<see cref="PhotoBackupOnConnect"/>), once per connection: nothing; a notification of the
/// phone's new photos with "Back up now" (the default; only for a phone plugged in while DeviceDeck runs, as the
/// "connected" one); or a backup that starts by itself, also for a phone that was plugged in when DeviceDeck started.
/// A backup the destination can't take (its drive isn't there, or too little room) never starts: a notification says
/// why. A run started by a notification or by the phone ends with a notification ("312 photos backed up · Open folder").
/// </summary>
public sealed class PhotoBackupWatcher : IDisposable
{
    private readonly DeviceHub _hub;
    private readonly PhotoBackupService _backup;
    private readonly INotificationService _notifications;
    private readonly SettingsStore _settings;
    private readonly PhotoBackupWatcherOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _checks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _present = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private bool _armed;
    private bool _disposed;

    public PhotoBackupWatcher(DeviceHub hub, PhotoBackupService backup, INotificationService notifications, SettingsStore settings,
        PhotoBackupWatcherOptions? options = null)
    {
        _hub = hub;
        _backup = backup;
        _notifications = notifications;
        _settings = settings;
        _options = options ?? new PhotoBackupWatcherOptions();
        lock (_gate)
        {
            foreach (var device in hub.ListDevices())
            {
                _present.Add(device.Id);
                _names[device.Id] = device.Name;
            }
        }

        hub.DeviceChanged += OnDeviceChanged;
        notifications.Activated += OnActivated;
        backup.RunFinished += OnRunFinished;
    }

    /// <summary>A check that failed for a reason other than the phone (for the log).</summary>
    public event EventHandler<Exception>? CheckFailed;

    public static string TagFor(string deviceId) => "photos:" + deviceId;

    public static string FinishedTagFor(string deviceId) => "photos-done:" + deviceId;

    /// <summary>The providers' first start is over. Phones already ready get their automatic backup now (not the notification).</summary>
    public void Arm()
    {
        lock (_gate)
        {
            _armed = true;
        }

        foreach (var device in _hub.ListDevices())
        {
            Consider(device);
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        var device = e.Summary;
        if (e.Change == DeviceChange.Removed || device.State == DeviceState.Disconnected)
        {
            CancellationTokenSource? check;
            lock (_gate)
            {
                _checks.Remove(device.Id, out check);
                _handled.Remove(device.Id);
                _present.Remove(device.Id);
            }

            Cancel(check);
            _notifications.Remove(TagFor(device.Id)); // its "Back up now" can't work any more
            return;
        }

        lock (_gate)
        {
            _names[device.Id] = device.Name;
            if (!_armed)
            {
                // Found while the providers start (an iPhone takes a few seconds more to be ready): it was there already.
                _present.Add(device.Id);
                return;
            }
        }

        Consider(device);
    }

    private void Consider(DeviceSummary device)
    {
        if (device.State != DeviceState.Ready || (device.Capabilities & DeviceCapability.PhotosRead) == 0)
        {
            return;
        }

        CancellationTokenSource check;
        bool atStart;
        lock (_gate)
        {
            if (_disposed || !_armed || _handled.Contains(device.Id) || _checks.ContainsKey(device.Id))
            {
                return;
            }

            check = new CancellationTokenSource();
            _checks[device.Id] = check;
            atStart = _present.Contains(device.Id);
        }

        _ = Task.Run(() => CheckAsync(device, atStart, check));
    }

    private async Task CheckAsync(DeviceSummary device, bool atStart, CancellationTokenSource check)
    {
        var ct = check.Token;
        var done = false;
        try
        {
            await Task.Delay(_options.Settle, ct).ConfigureAwait(false);
            switch (_backup.Ledger.GetOnConnect(device.Id))
            {
                case PhotoBackupOnConnect.Notify when !atStart:
                    var count = await _backup.CountNewAsync(device.Id, ct).ConfigureAwait(false);
                    if (count.NewCount > 0)
                    {
                        ShowNewPhotos(device, count);
                    }

                    break;
                case PhotoBackupOnConnect.BackUp:
                    await StartUnattendedAsync(device.Id, PhotoBackupTrigger.Automatic, ct).ConfigureAwait(false);
                    break;
            }

            done = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Unplugged (or shutting down) while looking.
        }
        catch (DeviceException ex) when (TransferQueue.IsDeviceUnavailable(ex))
        {
            // Locked, not trusted yet, or gone: looked at again the next time it is ready.
        }
        catch (Exception ex)
        {
            done = true;
            CheckFailed?.Invoke(this, ex);
        }
        finally
        {
            lock (_gate)
            {
                if (_checks.TryGetValue(device.Id, out var current) && current == check)
                {
                    _checks.Remove(device.Id);
                    if (done)
                    {
                        _handled.Add(device.Id);
                    }
                }
            }

            check.Dispose();
        }
    }

    /// <summary>Starts the phone's backup for the phone or a notification. Only something new; a destination that can't take it gets a notification instead.</summary>
    private async Task StartUnattendedAsync(string deviceId, PhotoBackupTrigger trigger, CancellationToken ct)
    {
        try
        {
            if (trigger == PhotoBackupTrigger.Automatic && (await _backup.CountNewAsync(deviceId, ct).ConfigureAwait(false)).NewCount == 0)
            {
                return;
            }

            await _backup.StartAsync(deviceId, trigger, ct).ConfigureAwait(false);
            _notifications.Remove(TagFor(deviceId));
        }
        catch (DeviceException ex) when (ex.Code is BackupErrors.NoSpace or DeviceErrorCodes.PathNotFound)
        {
            var text = Text;
            _notifications.Show(new AppNotification(text.NotStarted(NameOf(deviceId)), ex.Message)
            {
                Tag = TagFor(deviceId),
                DeviceId = deviceId,
                Command = PhotoBackupCommands.OpenPanel,
                Buttons = [new NotificationButton(text.Open, PhotoBackupCommands.OpenPanel)],
            });
        }
    }

    private void ShowNewPhotos(DeviceSummary device, PhotoBackupCount count)
    {
        var text = Text;
        _notifications.Show(new AppNotification(text.NewPhotos(count.NewCount, device.Name), text.NewDetail(count))
        {
            Tag = TagFor(device.Id),
            DeviceId = device.Id,
            Command = PhotoBackupCommands.OpenPanel,
            Buttons = [new NotificationButton(text.BackUpNow, PhotoBackupCommands.BackUpNow)],
        });
    }

    private void OnActivated(object? sender, NotificationActivation click)
    {
        switch (click.Command)
        {
            case PhotoBackupCommands.BackUpNow when click.DeviceId is { } deviceId:
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await StartUnattendedAsync(deviceId, PhotoBackupTrigger.Notification, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (DeviceException)
                    {
                        // Unplugged since: nothing to start. The page shows the phone's state.
                    }
                    catch (Exception ex)
                    {
                        CheckFailed?.Invoke(this, ex);
                    }
                });
                break;
            case PhotoBackupCommands.OpenFolder when click.Args.TryGetValue(PhotoBackupCommands.FolderArg, out var folder):
                // Only a folder a backup wrote to: the argument came back from a notification, not from the user.
                if (Path.IsPathFullyQualified(folder) && Directory.Exists(folder) && IsBackupFolder(folder))
                {
                    _options.OpenFolder?.Invoke(folder);
                }

                break;
        }
    }

    private bool IsBackupFolder(string folder) =>
        _backup.Ledger.Destinations().Any(d => string.Equals(Path.TrimEndingDirectorySeparator(d), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase));

    private void OnRunFinished(object? sender, PhotoBackupFinished e)
    {
        var run = e.Run;
        if (run.Trigger == PhotoBackupTrigger.Manual || run.State == PhotoBackupRunState.Cancelled)
        {
            return;
        }

        var text = Text;
        var device = NameOf(e.DeviceId);
        AppNotification? notification = run.State switch
        {
            PhotoBackupRunState.Done when run.Copied > 0 => new AppNotification(text.Done(run.Copied, run.Failed), text.DoneDetail(device, run.Bytes))
            {
                Buttons = [new NotificationButton(text.OpenFolder, PhotoBackupCommands.OpenFolder)],
                Command = PhotoBackupCommands.OpenFolder,
                Args = new Dictionary<string, string> { [PhotoBackupCommands.FolderArg] = run.Destination },
            },
            PhotoBackupRunState.Done when run.Duplicates > 0 => new AppNotification(text.UpToDate(device), text.UpToDateDetail),
            PhotoBackupRunState.Done => null,
            _ => new AppNotification(text.Stopped(device), e.Job.Error?.Message)
            {
                Command = PhotoBackupCommands.OpenPanel,
                Buttons = [new NotificationButton(text.Open, PhotoBackupCommands.OpenPanel)],
            },
        };

        if (notification is not null)
        {
            _notifications.Show(notification with { Tag = FinishedTagFor(e.DeviceId), DeviceId = e.DeviceId });
        }
    }

    private PhotoBackupText Text => PhotoBackupText.For(_settings.Current.Language);

    private string NameOf(string deviceId)
    {
        lock (_gate)
        {
            return _names.GetValueOrDefault(deviceId) ?? Text.SomePhone;
        }
    }

    private static void Cancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        List<CancellationTokenSource> checks;
        lock (_gate)
        {
            _disposed = true;
            checks = [.. _checks.Values];
            _checks.Clear();
        }

        _hub.DeviceChanged -= OnDeviceChanged;
        _notifications.Activated -= OnActivated;
        _backup.RunFinished -= OnRunFinished;
        foreach (var check in checks)
        {
            Cancel(check);
        }
    }
}
