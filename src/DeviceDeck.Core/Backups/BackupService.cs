using System.Globalization;
using System.Text;
using System.Threading.Channels;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups.Contacts;
using DeviceDeck.Core.Backups.Messages;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Backups;

public sealed class BackupServiceOptions
{
    public IKeepAwake? KeepAwake { get; init; }

    /// <summary>Makes JPEG copies of HEIC pictures in exported conversations, so the page and the PDF can show them.</summary>
    public IImageTranscoder? Transcoder { get; init; }

    /// <summary>Folders of iTunes and Apple Devices backups, listed read-only. Null = Windows' usual two.</summary>
    public IReadOnlyList<string>? AppleBackupFolders { get; init; }

    /// <summary>How often a backup waiting for its phone checks again (besides the phone's own connect event).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>At most one progress event per job per interval; state changes go out at once.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Finished jobs kept in the list.</summary>
    public int KeepFinished { get; init; } = 50;

    /// <summary>Opened backups without a password kept ready for browsing.</summary>
    public int OpenBackups { get; init; } = 3;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    public static IReadOnlyList<string> DefaultAppleFolders() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apple Computer", "MobileSync", "Backup"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apple", "MobileSync", "Backup"),
    ];
}

/// <summary>
/// Backups on this PC and the work around them: iPhone backups (mobilebackup2, through <see cref="IDeviceBackup"/>)
/// and Android backups (APKs and media), each keeping a dated copy; restores behind a typed confirmation; backup
/// encryption; browsing and extracting files; contacts and messages with their exports. A backup whose phone goes
/// away waits and carries on when the same phone is back. Nothing is deleted without the user confirming a list first.
/// Unlocked encrypted backups stay open (in memory only) until locked or the app closes; passwords are never kept.
/// </summary>
public sealed class BackupService : IAsyncDisposable
{
    private readonly DeviceHub _hub;
    private readonly SettingsStore _settings;
    private readonly IIosBackupFormat? _ios;
    private readonly BackupServiceOptions _options;
    private readonly IKeepAwake _keepAwake;
    private readonly TimeProvider _time;
    private readonly BackupLibrary _library;
    private readonly Lock _gate = new();
    private readonly List<Job> _jobs = [];
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Channel<BackupJob> _outbox = Channel.CreateUnbounded<BackupJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _life = new();
    private Task _pump = Task.CompletedTask;
    private ITimer? _retry;
    private bool _started;
    private bool _disposed;

    public BackupService(DeviceHub hub, SettingsStore settings, IIosBackupFormat? iosFormat, BackupServiceOptions? options = null)
    {
        _hub = hub;
        _settings = settings;
        _ios = iosFormat;
        _options = options ?? new BackupServiceOptions();
        _keepAwake = _options.KeepAwake ?? new NullKeepAwake();
        _time = _options.Time;
        _library = new BackupLibrary(iosFormat, () => _settings.Current.BackupFolder, _options.AppleBackupFolders ?? BackupServiceOptions.DefaultAppleFolders());
    }

    /// <summary>A job's new snapshot; raised in order from one background thread, progress throttled.</summary>
    public event EventHandler<BackupJob>? JobUpdated;

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }
        _hub.DeviceChanged += OnDeviceChanged;
        _pump = Task.Run(PumpAsync);
        _retry = _time.CreateTimer(_ => RetryWaiting(null), null, _options.RetryDelay, _options.RetryDelay);
        _ = Task.Run(RemoveLeftovers);
    }

    // ---------------------------------------------------------------- listing

    /// <summary>Every backup on this PC (or of one phone), newest first.</summary>
    public IReadOnlyList<BackupInfo> List(string? deviceId = null)
    {
        var records = _library.Scan();
        lock (_gate)
        {
            return records
                .Where(r => !r.Hidden && (deviceId is null || string.Equals(r.Info.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
                .Select(DecorateLocked)
                .ToList();
        }
    }

    /// <summary>What the Backup screen shows for one phone: where backups go, what the phone says, the last backup.</summary>
    public async Task<BackupDeviceInfo> GetDeviceInfoAsync(string deviceId, CancellationToken ct)
    {
        var device = _hub.GetDevice(deviceId);
        var summary = device.Summary;
        BackupDeviceState? state = null;
        if (device is IDeviceBackup backup && summary.State == DeviceState.Ready && (summary.Capabilities & DeviceCapability.Backup) != 0)
        {
            try
            {
                state = await backup.GetBackupStateAsync(ct).ConfigureAwait(false);
            }
            catch (DeviceException)
            {
                // The rest of the screen works without it.
            }
        }

        var root = _library.Root;
        var keepsHistory = await Task.Run(_library.KeepsHistory, ct).ConfigureAwait(false);
        long? free = null;
        try
        {
            free = new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
        var last = List(deviceId).FirstOrDefault(b => b.State == BackupState.Complete);
        return new BackupDeviceInfo(deviceId, summary.Platform, root, _settings.Current.BackupKeep, keepsHistory, free,
            state?.Encrypted, state?.CloudBackupEnabled, summary.Platform == DevicePlatform.Android, last);
    }

    // ---------------------------------------------------------------- jobs

    public IReadOnlyList<BackupJob> Jobs()
    {
        lock (_gate)
        {
            return _jobs.Select(Snapshot).ToList();
        }
    }

    /// <summary>Starts a backup of a connected phone. An unfinished Android backup of the same phone is continued.</summary>
    public BackupJob StartBackup(string deviceId, bool full = false)
    {
        var device = _hub.GetUsableDevice(deviceId);
        var summary = device.Summary;
        if ((summary.Capabilities & DeviceCapability.Backup) == 0 || (summary.Platform == DevicePlatform.Ios && device is not IDeviceBackup))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{summary.Name} can't be backed up by DeviceDeck.");
        }

        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Backup,
            DeviceId = deviceId,
            DeviceName = summary.Name,
            Title = $"Backing up {summary.Name}",
            ResumesOnReconnect = true,
            CreatedAt = _time.GetLocalNow(),
            Work = summary.Platform == DevicePlatform.Ios ? (j, ct) => RunIosBackupAsync(j, full, ct) : RunAndroidBackupAsync,
        };
        return Add(job, exclusive: true);
    }

    /// <summary>
    /// Restores an iPhone backup onto a connected iPhone. <paramref name="confirmName"/> must be the phone's name as the
    /// user typed it; an encrypted backup's password is checked before the phone is asked.
    /// </summary>
    public BackupJob StartRestore(string deviceId, string backupId, string confirmName, string? password, bool restoreSettings)
    {
        var device = _hub.GetUsableDevice(deviceId);
        var summary = device.Summary;
        if ((summary.Capabilities & DeviceCapability.Restore) == 0 || device is not IDeviceBackup backup)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{summary.Name} can't be restored by DeviceDeck.");
        }

        if (!string.Equals(confirmName?.Trim(), summary.Name.Trim(), StringComparison.CurrentCultureIgnoreCase))
        {
            throw new ArgumentException("To confirm the restore, type the phone's name exactly as it is shown.");
        }

        var record = Find(backupId);
        if (record.Ios is null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "Only iPhone backups can be restored onto an iPhone.");
        }

        if (!record.Ios.Complete)
        {
            throw new DeviceException(BackupErrors.Corrupt, "This backup isn't complete, so it can't be restored.");
        }

        if (record.Ios.Encrypted && string.IsNullOrEmpty(password))
        {
            throw new DeviceException(BackupErrors.PasswordRequired, "This backup is encrypted. Enter its password to restore it.");
        }

        if (IsNewer(record.Ios.ProductVersion, summary.OsVersion))
        {
            throw new DeviceException(BackupErrors.Refused,
                $"This backup is from iOS {record.Ios.ProductVersion}, newer than {summary.Name}'s iOS {summary.OsVersion}. Update the iPhone first.");
        }

        var folder = record.Folder;
        var encrypted = record.Ios.Encrypted;
        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Restore,
            DeviceId = deviceId,
            DeviceName = summary.Name,
            Title = $"Restoring {summary.Name} from the backup of {record.Info.DeviceName}",
            CreatedAt = _time.GetLocalNow(),
            BackupId = backupId,
            Work = async (j, ct) =>
            {
                if (encrypted)
                {
                    Report(j, BackupPhase.Starting, null, 0, 0);
                    await _ios!.VerifyPasswordAsync(folder, password!, ct).ConfigureAwait(false);
                }
                await backup.RestoreAsync(folder, new RestoreRequest(password, restoreSettings), Reporter(j), ct).ConfigureAwait(false);
            },
        };
        return Add(job, exclusive: true);
    }

    /// <summary>Turns backup encryption on or off, or changes its password (both passwords given). The phone asks for its passcode.</summary>
    public BackupJob StartEncryptionChange(string deviceId, bool enable, string? password, string? oldPassword)
    {
        var device = _hub.GetUsableDevice(deviceId);
        var summary = device.Summary;
        if ((summary.Capabilities & DeviceCapability.Backup) == 0 || device is not IDeviceBackup backup)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{summary.Name} has no backup encryption DeviceDeck can change.");
        }

        if (enable && string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Choose a password for the backups.");
        }

        if (!enable && string.IsNullOrEmpty(oldPassword))
        {
            throw new ArgumentException("Enter the current backup password to turn encryption off.");
        }

        var title = !enable ? $"Turning off backup encryption on {summary.Name}"
            : string.IsNullOrEmpty(oldPassword) ? $"Turning on backup encryption on {summary.Name}"
            : $"Changing the backup password of {summary.Name}";
        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Encryption,
            DeviceId = deviceId,
            DeviceName = summary.Name,
            Title = title,
            CreatedAt = _time.GetLocalNow(),
            Work = (j, ct) => backup.ChangeEncryptionAsync(string.IsNullOrEmpty(oldPassword) ? null : oldPassword, enable ? password : null, Reporter(j), ct),
        };
        return Add(job, exclusive: true);
    }

    /// <summary>Stops a job. A backup that stops part-way can be continued later; nothing it received is thrown away.</summary>
    public void Cancel(string jobId)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(j => j.Id == jobId) ?? throw new ArgumentException("No backup job has that id.", nameof(jobId));
            switch (job.State)
            {
                case BackupJobState.Running:
                    job.CancelRequested = true;
                    cancel = job.Cts;
                    break;
                case BackupJobState.Queued or BackupJobState.WaitingForDevice:
                    Finish(job, BackupJobState.Cancelled, null);
                    break;
            }
        }
        try
        {
            cancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---------------------------------------------------------------- cleaning up

    /// <summary>The phone's backups beyond Settings' keep count, oldest last, with what deleting them frees. Empty when none.</summary>
    public PrunePlan GetPrunePlan(string deviceId) => PlanPrune(deviceId) ?? new PrunePlan(deviceId, _settings.Current.BackupKeep, [], 0);

    /// <summary>What deleting these backups would free, for the confirmation the UI shows first.</summary>
    public PrunePlan GetDeletePlan(IReadOnlyList<string> backupIds)
    {
        var doomed = Doomed(backupIds);
        lock (_gate)
        {
            return new PrunePlan(doomed.FirstOrDefault()?.Info.DeviceId ?? "", _settings.Current.BackupKeep,
                doomed.Where(d => !d.Hidden).Select(DecorateLocked).ToList(), _library.FreedBytes(doomed));
        }
    }

    /// <summary>
    /// Deletes DeviceDeck's own backups (never iTunes' or Apple Devices'). Call it only after the user confirmed the list
    /// <see cref="GetDeletePlan"/> or <see cref="GetPrunePlan"/> gave. The folders vanish from the list at once and are
    /// removed from disk in the background.
    /// </summary>
    public BackupDeleteResult Delete(IReadOnlyList<string> backupIds)
    {
        var doomed = Doomed(backupIds);
        var freed = _library.FreedBytes(doomed);
        var moved = new List<string>();
        foreach (var record in doomed)
        {
            CloseSessions(record.Folder);
            var trash = record.Folder + ".deleting";
            Directory.Move(record.Folder, trash);
            _library.Forget(record.Folder);
            moved.Add(trash);
        }
        _ = Task.Run(() => DeleteFolders(moved));
        return new BackupDeleteResult(doomed.Count(d => !d.Hidden), freed);
    }

    private List<BackupRecord> Doomed(IReadOnlyList<string> backupIds)
    {
        if (backupIds.Count == 0)
        {
            throw new ArgumentException("backupIds is empty.");
        }

        var records = _library.Scan();
        var doomed = new List<BackupRecord>();
        foreach (var id in backupIds.Distinct(StringComparer.Ordinal))
        {
            var record = records.FirstOrDefault(r => r.Info.Id == id && !r.Hidden)
                         ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That backup isn't on this PC any more.");
            if (record.Info.Source != BackupSource.DeviceDeck)
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Backups made by iTunes or Apple Devices are only read by DeviceDeck. Delete them there.");
            }

            lock (_gate)
            {
                if (IsBeingWritten(record))
                {
                    throw new DeviceException(BackupErrors.Busy, "That backup is being written right now.");
                }
            }
            doomed.Add(record);
            // The folder the phone updates holds the same backup as its newest copy: it goes with it.
            if (record.Ios?.SnapshotId is { } snapshot)
            {
                doomed.AddRange(records.Where(r => r.Hidden && r.Info.DeviceId == record.Info.DeviceId && r.Ios?.SnapshotId == snapshot));
            }
        }
        return doomed.DistinctBy(d => d.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private PrunePlan? PlanPrune(string deviceId)
    {
        var keep = _settings.Current.BackupKeep;
        var copies = _library.Scan()
            .Where(r => string.Equals(r.Info.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && r.Info.Source == BackupSource.DeviceDeck
                        && r.Info.State == BackupState.Complete && !r.IsLive)
            .OrderByDescending(r => r.Info.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
        var doomed = copies.Skip(keep).ToList();
        if (doomed.Count == 0)
        {
            return null;
        }

        lock (_gate)
        {
            return new PrunePlan(deviceId, keep, doomed.Select(DecorateLocked).ToList(), _library.FreedBytes(doomed));
        }
    }

    // ---------------------------------------------------------------- browsing

    /// <summary>Opens an encrypted backup with its password for this session. A wrong one throws wrong_password.</summary>
    public async Task UnlockAsync(string backupId, string password, CancellationToken ct)
    {
        var record = Find(backupId);
        if (_ios is null || record.Ios is null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "Only iPhone backups are encrypted.");
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("password is empty.");
        }

        var archive = await _ios.OpenAsync(record.Folder, password, ct).ConfigureAwait(false);
        Remember(new Session(backupId, record, archive, Stamp(record.Folder), unlocked: true));
    }

    /// <summary>Forgets an unlocked backup's keys; it needs its password again.</summary>
    public void Lock(string backupId)
    {
        Session? session;
        lock (_gate)
        {
            if (_sessions.Remove(backupId, out session) is false)
            {
                return;
            }
        }
        _ = session!.DisposeAsync().AsTask();
    }

    public async Task<IReadOnlyList<BackupDomain>> GetDomainsAsync(string backupId, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var domains = session.Archive.Domains;
        var names = session.Record.Ios?.AppNames ?? new Dictionary<string, string>();
        var apps = domains.Where(d => d.Domain.StartsWith(AppPrefix, StringComparison.Ordinal)).Select(d => d.Domain[AppPrefix.Length..]).ToList();
        return domains.Select(d => Describe(d, names, apps))
            .OrderBy(d => d.Kind == BackupDomainKind.System ? 1 : 0)
            .ThenBy(d => d.Kind == BackupDomainKind.System ? "" : d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(d => d.Bytes)
            .ToList();
    }

    public async Task<IReadOnlyList<BackupEntry>> GetFilesAsync(string backupId, string domain, string path, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        if (!session.Archive.Domains.Any(d => d.Domain == domain))
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That domain isn't in this backup.");
        }

        return session.Archive.List(domain, path);
    }

    /// <summary>Copies files and folders out of a backup into a PC folder; names already there get " (2)".</summary>
    public BackupJob StartExtract(string backupId, IReadOnlyList<BackupItemRef> items, string destination)
    {
        RequirePcFolder(destination);
        if (items.Count == 0)
        {
            throw new ArgumentException("items is empty.");
        }

        var record = Find(backupId);
        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Extract,
            DeviceId = null,
            DeviceName = record.Info.DeviceName,
            Title = items.Count == 1
                ? $"Extracting {Leaf(items[0])} from the backup of {record.Info.DeviceName}"
                : $"Extracting {items.Count:N0} items from the backup of {record.Info.DeviceName}",
            CreatedAt = _time.GetLocalNow(),
            BackupId = backupId,
            Destination = destination,
            Work = (j, ct) => RunExtractAsync(j, backupId, items, destination, ct),
        };
        return Add(job, exclusive: false);
    }

    // ---------------------------------------------------------------- contacts and messages

    public async Task<IReadOnlyList<Contact>> GetContactsAsync(string backupId, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        return await WithDatabasesAsync(session, s => s.Contacts!, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Conversation>> GetConversationsAsync(string backupId, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        return await WithDatabasesAsync(session, s => s.Messages?.Conversations() ?? [], ct).ConfigureAwait(false);
    }

    public async Task<Page<Message>> GetMessagesAsync(string backupId, string conversationId, int offset, int limit, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        return await WithDatabasesAsync(session, s => s.Messages?.Messages(conversationId, offset, limit)
                                                   ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "This backup has no messages."), ct).ConfigureAwait(false);
    }

    /// <summary>Writes the chosen contacts (all when <paramref name="contactIds"/> is empty) to one .vcf or .csv file.</summary>
    public BackupJob StartContactsExport(string backupId, IReadOnlyList<string>? contactIds, ContactsFormat format, string destination)
    {
        RequirePcFolder(destination);
        var record = Find(backupId);
        RequireContactsAndMessages(record);
        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Export,
            DeviceId = null,
            DeviceName = record.Info.DeviceName,
            Title = $"Saving contacts from the backup of {record.Info.DeviceName}",
            CreatedAt = _time.GetLocalNow(),
            BackupId = backupId,
            Destination = destination,
            Work = (j, ct) => RunContactsExportAsync(j, backupId, contactIds, format, destination, ct),
        };
        return Add(job, exclusive: false);
    }

    /// <summary>One HTML page (or PDF) per conversation, with its attachments in a folder next to it.</summary>
    public BackupJob StartMessagesExport(string backupId, IReadOnlyList<string> conversationIds, MessagesFormat format, string destination, IPdfPrinter? printer)
    {
        RequirePcFolder(destination);
        if (conversationIds.Count == 0)
        {
            throw new ArgumentException("conversationIds is empty.");
        }

        if (format == MessagesFormat.Pdf && printer is null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "PDF export needs the DeviceDeck window. Save as HTML instead.");
        }

        var record = Find(backupId);
        RequireContactsAndMessages(record);
        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Export,
            DeviceId = null,
            DeviceName = record.Info.DeviceName,
            Title = conversationIds.Count == 1
                ? $"Saving a conversation from the backup of {record.Info.DeviceName}"
                : $"Saving {conversationIds.Count:N0} conversations from the backup of {record.Info.DeviceName}",
            CreatedAt = _time.GetLocalNow(),
            BackupId = backupId,
            Destination = destination,
            Work = (j, ct) => RunMessagesExportAsync(j, backupId, conversationIds, format, destination, printer, ct),
        };
        return Add(job, exclusive: false);
    }

    // ---------------------------------------------------------------- job bodies

    private async Task RunIosBackupAsync(Job job, bool full, CancellationToken ct)
    {
        var device = _hub.GetUsableDevice(job.DeviceId!);
        var backup = device as IDeviceBackup ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "This phone can't be backed up.");
        var root = PrepareRoot();
        var udid = device.Summary.Id;
        var live = Path.Combine(root, udid);
        lock (_gate)
        {
            job.Folder = live;
        }

        // A finished backup without a dated copy (from iTunes, or an older drive) is copied before the phone changes it.
        await Task.Run(() => PreserveLive(live, udid, ct), ct).ConfigureAwait(false);
        await backup.BackupAsync(root, new BackupRequest(full && job.Attempts == 1), Reporter(job), ct).ConfigureAwait(false);

        Report(job, BackupPhase.Finishing, 100, null, null);
        var header = _ios?.ReadHeader(live);
        if (header is not { Complete: true })
        {
            throw new DeviceException(BackupErrors.Corrupt, "The iPhone said the backup is done, but the backup folder isn't complete. Back up again.");
        }

        CloseSessions(live);
        var copy = await Task.Run(() => _library.Snapshot(live, udid, header.Date ?? _time.GetUtcNow(), ct), ct).ConfigureAwait(false);
        var plan = await Task.Run(() => PlanPrune(job.DeviceId!), ct).ConfigureAwait(false);
        lock (_gate)
        {
            job.BackupId = BackupLibrary.IdFor(copy ?? udid);
            job.Prune = plan;
        }
    }

    private async Task RunAndroidBackupAsync(Job job, CancellationToken ct)
    {
        var device = _hub.GetUsableDevice(job.DeviceId!);
        var root = PrepareRoot();
        var mine = _library.Scan().Where(r => r.Android is not null && r.Info.DeviceId == job.DeviceId).ToList();
        string folder;
        lock (_gate)
        {
            job.Folder ??= mine.FirstOrDefault(r => !r.Android!.Complete)?.Folder
                           ?? Path.Combine(root, AndroidBackupEngine.Safe(device.Summary.Id) + "-" + _time.GetLocalNow().ToString(BackupLibrary.StampFormat, CultureInfo.InvariantCulture));
            folder = job.Folder;
        }
        var previous = mine.Where(r => r.Android!.Complete && !string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Info.CreatedAt).FirstOrDefault();

        long totalBytes = 0;
        await AndroidBackupEngine.RunAsync(device, folder, previous is null ? null : (previous.Folder, previous.Android!),
            planned: t =>
            {
                lock (_gate)
                {
                    job.TotalFiles = t.Files;
                    job.TotalBytes = totalBytes = t.Bytes;
                }
                Report(job, BackupPhase.Transferring, 0, 0, 0);
            },
            progress: t => Report(job, BackupPhase.Transferring, totalBytes > 0 ? Math.Min(100, 100.0 * t.Bytes / totalBytes) : null, t.Bytes, t.Files),
            ct).ConfigureAwait(false);

        Report(job, BackupPhase.Finishing, 100, null, null);
        var plan = await Task.Run(() => PlanPrune(job.DeviceId!), ct).ConfigureAwait(false);
        lock (_gate)
        {
            job.BackupId = BackupLibrary.IdFor(Path.GetFileName(folder));
            job.Prune = plan;
        }
    }

    private async Task RunExtractAsync(Job job, string backupId, IReadOnlyList<BackupItemRef> items, string destination, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var names = (await GetDomainsAsync(backupId, ct).ConfigureAwait(false)).ToDictionary(d => d.Domain, d => d.Name, StringComparer.Ordinal);
        var plan = new List<(BackupEntry Entry, string Target)>();
        foreach (var item in items)
        {
            var path = item.Path.Trim('/');
            var cut = path.LastIndexOf('/');
            // The chosen file or folder keeps its own name; a whole domain gets one named after it.
            var baseFolder = path.Length == 0 ? Path.Combine(destination, AndroidBackupEngine.Safe(names.GetValueOrDefault(item.Domain) ?? item.Domain)) : destination;
            var skip = path.Length == 0 ? 0 : cut + 1;
            foreach (var entry in session.Archive.ListFiles(item.Domain, path))
            {
                var relative = entry.Path[skip..].Split('/').Select(AndroidBackupEngine.Safe);
                plan.Add((entry, Path.Combine([baseFolder, .. relative])));
            }
        }
        if (plan.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "There are no files there to extract.");
        }

        lock (_gate)
        {
            job.TotalFiles = plan.Count;
            job.TotalBytes = plan.Sum(p => p.Entry.Size);
            if (plan.Count == 1)
            {
                job.Destination = plan[0].Target;
            }
        }
        var bytes = 0L;
        var files = 0;
        var missing = 0;
        foreach (var (entry, target) in plan)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var final = UniquePath(target);
            var temp = final + ".ddpart";
            var before = bytes;
            try
            {
                await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                {
                    await session.Archive.CopyToAsync(entry, output, new Inline<long>(b => Report(job, BackupPhase.Transferring, Percent(before + b, job), before + b, files)), ct)
                        .ConfigureAwait(false);
                }
                if (entry.Modified is { } modified)
                {
                    File.SetLastWriteTimeUtc(temp, modified.UtcDateTime);
                }

                File.Move(temp, final);
                if (plan.Count == 1)
                {
                    lock (_gate)
                    {
                        job.Destination = final;
                    }
                }
            }
            catch (DeviceException ex) when (ex.Code == BackupErrors.Missing)
            {
                missing++;
                File.Delete(temp);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
            bytes = before + entry.Size;
            files++;
            Report(job, BackupPhase.Transferring, Percent(bytes, job), bytes, files);
        }
        if (missing == plan.Count)
        {
            throw new DeviceException(BackupErrors.Missing, "The backup lists those files, but their data isn't in it.");
        }

        if (missing > 0)
        {
            lock (_gate)
            {
                job.Warning = new BackupJobError(BackupErrors.Missing, $"{missing:N0} of {plan.Count:N0} files are listed in the backup but their data isn't in it.");
            }
        }
    }

    private async Task RunContactsExportAsync(Job job, string backupId, IReadOnlyList<string>? contactIds, ContactsFormat format, string destination, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var all = await WithDatabasesAsync(session, s => s.Contacts!, ct).ConfigureAwait(false);
        var wanted = contactIds is { Count: > 0 } ? contactIds.ToHashSet(StringComparer.Ordinal) : null;
        var contacts = wanted is null ? all : all.Where(c => wanted.Contains(c.Id)).ToList();
        if (contacts.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "There are no contacts to save.");
        }

        lock (_gate)
        {
            job.TotalFiles = contacts.Count;
        }

        Directory.CreateDirectory(destination);
        var stem = AndroidBackupEngine.Safe($"Contacts - {session.Record.Info.DeviceName} - {(session.Record.Info.CreatedAt ?? _time.GetLocalNow()).ToLocalTime():yyyy-MM-dd}");
        var path = UniquePath(Path.Combine(destination, stem + (format == ContactsFormat.VCard ? ".vcf" : ".csv")));
        var temp = path + ".ddpart";
        try
        {
            await WithDatabasesAsync(session, s =>
            {
                // Excel only reads UTF-8 CSV as such with a byte-order mark; vCard readers want none.
                using var writer = new StreamWriter(temp, false, new UTF8Encoding(format == ContactsFormat.Csv));
                if (format == ContactsFormat.Csv)
                {
                    ContactsExport.WriteCsv(writer, contacts);
                }
                else
                {
                    ContactsExport.WriteVCards(writer, contacts, c => c.HasPhoto && s.Images is { } images ? AddressBookReader.Photo(images.Connection, c.Id) : null);
                }

                return true;
            }, ct).ConfigureAwait(false);
            File.Move(temp, path);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
        lock (_gate)
        {
            job.Destination = path;
        }

        Report(job, BackupPhase.Finishing, 100, new FileInfo(path).Length, contacts.Count);
    }

    private async Task RunMessagesExportAsync(Job job, string backupId, IReadOnlyList<string> conversationIds, MessagesFormat format, string destination,
        IPdfPrinter? printer, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var work = await WithDatabasesAsync(session, s =>
        {
            var store = s.Messages ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "This backup has no messages.");
            return conversationIds.Distinct(StringComparer.Ordinal)
                .Select(id => store.Find(id) is { } conversation ? (conversation, store.AllMessages(id)) : default)
                .Where(c => c.conversation is not null)
                .Select(c => (Conversation: c.conversation!, Messages: (IReadOnlyList<Message>)c.Item2,
                    Paths: c.Item2.SelectMany(m => m.Attachments).Where(a => a.InBackup).ToDictionary(a => a.Id, a => store.AttachmentPath(a.Id))))
                .ToList();
        }, ct).ConfigureAwait(false);
        if (work.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "Those conversations aren't in this backup.");
        }

        lock (_gate)
        {
            job.TotalFiles = work.Count + work.Sum(w => w.Paths.Count);
            job.TotalBytes = null;
        }
        Directory.CreateDirectory(destination);
        var info = session.Record.Info;
        var source = $"From the backup of {info.DeviceName}" + (info.CreatedAt is { } made ? $" made {made.ToLocalTime():d MMM yyyy, HH:mm}" : "") + " · saved with DeviceDeck";
        var files = 0;
        var bytes = 0L;
        foreach (var (conversation, messages, paths) in work)
        {
            ct.ThrowIfCancellationRequested();
            var stem = UniqueStem(destination, Trim(AndroidBackupEngine.Safe(conversation.Title), 80), format == MessagesFormat.Pdf ? ".pdf" : ".html");
            var filesFolder = stem + " files";
            var hrefs = new Dictionary<string, string>(StringComparer.Ordinal);
            var previews = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var attachment in messages.SelectMany(m => m.Attachments).Where(a => a.InBackup))
            {
                ct.ThrowIfCancellationRequested();
                if (paths.GetValueOrDefault(attachment.Id) is not { } location || session.Archive.Find(location.Domain, location.Path) is not { IsDirectory: false } entry)
                {
                    continue;
                }

                var folder = Path.Combine(destination, filesFolder);
                Directory.CreateDirectory(folder);
                var target = UniquePath(Path.Combine(folder, AndroidBackupEngine.Safe(attachment.Name)));
                try
                {
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                    {
                        await session.Archive.CopyToAsync(entry, output, null, ct).ConfigureAwait(false);
                    }
                }
                catch (DeviceException ex) when (ex.Code == BackupErrors.Missing)
                {
                    File.Delete(target);
                    continue;
                }
                hrefs[attachment.Id] = filesFolder + "/" + Path.GetFileName(target);
                if (ConversationHtml.Kind(attachment) == "image" && !ConversationHtml.IsBrowserImage(attachment) && _options.Transcoder is { } transcoder)
                {
                    await using var original = File.OpenRead(target);
                    if (await transcoder.ToJpegAsync(original, 2048, ct).ConfigureAwait(false) is { } jpeg)
                    {
                        var previewFolder = Path.Combine(folder, "previews");
                        Directory.CreateDirectory(previewFolder);
                        var preview = UniquePath(Path.Combine(previewFolder, Path.GetFileNameWithoutExtension(target) + ".jpg"));
                        await File.WriteAllBytesAsync(preview, jpeg, ct).ConfigureAwait(false);
                        previews[attachment.Id] = filesFolder + "/previews/" + Path.GetFileName(preview);
                    }
                }
                files++;
                bytes += entry.Size;
                Report(job, BackupPhase.Transferring, Percent(files, job), bytes, files);
            }

            var html = Path.Combine(destination, stem + ".html");
            await using (var writer = new StreamWriter(html, false, new UTF8Encoding(false)))
            {
                ConversationHtml.Write(writer, conversation, messages, source, a => hrefs.GetValueOrDefault(a.Id), a => previews.GetValueOrDefault(a.Id),
                    forPrint: format == MessagesFormat.Pdf);
            }

            if (format == MessagesFormat.Pdf)
            {
                await printer!.PrintToPdfAsync(html, Path.Combine(destination, stem + ".pdf"), ct).ConfigureAwait(false);
                File.Delete(html);
            }
            files++;
            Report(job, BackupPhase.Transferring, Percent(files, job), bytes, files);
        }
    }

    // ---------------------------------------------------------------- sessions

    private const string AppPrefix = "AppDomain-";

    private sealed class Session(string backupId, BackupRecord record, IBackupArchive archive, DateTime stamp, bool unlocked) : IAsyncDisposable
    {
        public string BackupId { get; } = backupId;
        public BackupRecord Record { get; } = record;
        public IBackupArchive Archive { get; } = archive;
        public DateTime Stamp { get; } = stamp;
        public bool Unlocked { get; } = unlocked;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long LastUsed { get; set; }
        public bool Loaded { get; set; }
        public IReadOnlyList<Contact>? Contacts { get; set; }
        public SqliteImage? AddressBook { get; set; }
        public SqliteImage? Images { get; set; }
        public SqliteImage? Sms { get; set; }
        public MessageStore? Messages { get; set; }

        public async ValueTask DisposeAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                AddressBook?.Dispose();
                Images?.Dispose();
                Sms?.Dispose();
                Archive.Dispose();
            }
            finally
            {
                Gate.Release();
            }
        }
    }

    private async Task<Session> GetSessionAsync(string backupId, CancellationToken ct)
    {
        var record = Find(backupId);
        var stamp = Stamp(record.Folder);
        lock (_gate)
        {
            if (_sessions.TryGetValue(backupId, out var open) && open.Stamp == stamp)
            {
                open.LastUsed = _time.GetTimestamp();
                return open;
            }
        }
        if (record.Info.State != BackupState.Complete)
        {
            throw new DeviceException(BackupErrors.Corrupt, "This backup isn't complete, so it can't be opened. Back up again to finish it.");
        }

        if (record.Ios is { Encrypted: true })
        {
            throw new DeviceException(BackupErrors.PasswordRequired, "This backup is encrypted. Enter its password to open it.");
        }

        IBackupArchive archive = record.Android is not null
            ? await Task.Run(() => new FolderArchive(record.Folder), ct).ConfigureAwait(false)
            : await (_ios ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "iPhone backups can't be read here.")).OpenAsync(record.Folder, null, ct).ConfigureAwait(false);
        return Remember(new Session(backupId, record, archive, stamp, unlocked: false));
    }

    private Session Remember(Session session)
    {
        var dropped = new List<Session>();
        Session kept;
        lock (_gate)
        {
            session.LastUsed = _time.GetTimestamp();
            if (_sessions.TryGetValue(session.BackupId, out var existing) && existing.Stamp == session.Stamp && existing.Unlocked && !session.Unlocked)
            {
                dropped.Add(session);
                kept = existing;
            }
            else
            {
                if (existing is not null)
                {
                    dropped.Add(existing);
                }

                _sessions[session.BackupId] = session;
                kept = session;
            }
            // Backups opened without a password are cheap to open again; keep a few.
            foreach (var old in _sessions.Values.Where(s => !s.Unlocked).OrderByDescending(s => s.LastUsed).Skip(_options.OpenBackups).ToList())
            {
                _sessions.Remove(old.BackupId);
                dropped.Add(old);
            }
        }
        foreach (var old in dropped)
        {
            _ = old.DisposeAsync().AsTask();
        }

        return kept;
    }

    private void CloseSessions(string folder)
    {
        List<Session> closing;
        lock (_gate)
        {
            closing = _sessions.Values.Where(s => string.Equals(s.Record.Folder, folder, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var s in closing)
            {
                _sessions.Remove(s.BackupId);
            }
        }
        foreach (var s in closing)
        {
            _ = s.DisposeAsync().AsTask();
        }
    }

    /// <summary>Runs <paramref name="read"/> with the address book and Messages databases open (SQLite needs one caller at a time).</summary>
    private async Task<T> WithDatabasesAsync<T>(Session session, Func<Session, T> read, CancellationToken ct)
    {
        RequireContactsAndMessages(session.Record);
        await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!session.Loaded)
            {
                session.AddressBook = await OpenDatabaseAsync(session.Archive, AddressBookReader.Domain, AddressBookReader.DatabasePath, ct).ConfigureAwait(false);
                session.Images = await OpenDatabaseAsync(session.Archive, AddressBookReader.Domain, AddressBookReader.ImagesPath, ct).ConfigureAwait(false);
                session.Contacts = session.AddressBook is null ? [] : Guard(() => AddressBookReader.Read(session.AddressBook.Connection, session.Images?.Connection));
                session.Sms = await OpenDatabaseAsync(session.Archive, MessageStore.Domain, MessageStore.DatabasePath, ct).ConfigureAwait(false);
                var archive = session.Archive;
                session.Messages = session.Sms is null ? null
                    : Guard(() => new MessageStore(session.Sms.Connection, session.Contacts, path => archive.Find(MessageStore.AttachmentDomain, path) is { IsDirectory: false }));
                session.Loaded = true;
            }
            return Guard(() => read(session));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>A database in the backup: opened in place when stored as is, else decrypted into memory. Null when the backup lacks it.</summary>
    private static async Task<SqliteImage?> OpenDatabaseAsync(IBackupArchive archive, string domain, string path, CancellationToken ct)
    {
        if (archive.Find(domain, path) is not { IsDirectory: false } entry)
        {
            return null;
        }

        if (archive.PlainPath(entry) is { } plain)
        {
            return Guard(() => SqliteImage.OpenFile(plain));
        }

        using var memory = new MemoryStream(entry.Size > 0 && entry.Size < int.MaxValue ? (int)entry.Size : 0);
        await archive.CopyToAsync(entry, memory, null, ct).ConfigureAwait(false);
        var image = memory.ToArray();
        return Guard(() => SqliteImage.FromBytes(image));
    }

    private static T Guard<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            throw new DeviceException(BackupErrors.Corrupt, "A database in the backup can't be read: " + ex.Message, ex);
        }
    }

    private static void RequireContactsAndMessages(BackupRecord record)
    {
        if (record.Android is not null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported,
                "Android backups don't include contacts or messages: Android only lets an app on the phone read them, and DeviceDeck's helper app isn't available yet.");
        }
    }

    private static BackupDomain Describe(BackupDomainInfo domain, IReadOnlyDictionary<string, string> names, IReadOnlyList<string> apps)
    {
        string NameOf(string app) => names.GetValueOrDefault(app) ?? app;
        string? Owner(string id, Func<string, bool> matches) => apps.Where(matches).OrderByDescending(a => a.Length).FirstOrDefault();

        var d = domain.Domain;
        if (d.StartsWith("AppDomainGroup-", StringComparison.Ordinal))
        {
            var group = d["AppDomainGroup-".Length..];
            var app = Owner(group, a => group.Contains(a, StringComparison.OrdinalIgnoreCase));
            return new BackupDomain(d, BackupDomainKind.AppGroup, app, app is null ? group : NameOf(app), domain.Files, domain.Bytes);
        }
        if (d.StartsWith("AppDomainPlugin-", StringComparison.Ordinal))
        {
            var plugin = d["AppDomainPlugin-".Length..];
            var app = Owner(plugin, a => plugin.StartsWith(a + ".", StringComparison.Ordinal));
            return new BackupDomain(d, BackupDomainKind.AppPlugin, app, app is null ? plugin : NameOf(app), domain.Files, domain.Bytes);
        }
        if (d.StartsWith(AppPrefix, StringComparison.Ordinal))
        {
            var app = d[AppPrefix.Length..];
            return new BackupDomain(d, BackupDomainKind.App, app, NameOf(app), domain.Files, domain.Bytes);
        }
        return new BackupDomain(d, BackupDomainKind.System, null, d, domain.Files, domain.Bytes);
    }

    // ---------------------------------------------------------------- job machinery

    private sealed class Job
    {
        public required string Id { get; init; }
        public required BackupJobKind Kind { get; init; }
        public required string? DeviceId { get; init; }
        public required string DeviceName { get; init; }
        public required string Title { get; init; }
        public required Func<Job, CancellationToken, Task> Work { get; init; }

        /// <summary>A backup carries on when its phone is back; a restore or a password change is not started again by itself.</summary>
        public bool ResumesOnReconnect { get; init; }

        public DateTimeOffset CreatedAt { get; init; }
        public BackupJobState State { get; set; }
        public BackupPhase? Phase { get; set; }
        public double? Percent { get; set; }
        public long Bytes { get; set; }
        public int Files { get; set; }
        public long? TotalBytes { get; set; }
        public int? TotalFiles { get; set; }
        public string? BackupId { get; set; }
        public string? Destination { get; set; }
        public PrunePlan? Prune { get; set; }
        public BackupJobError? Error { get; set; }

        /// <summary>A problem that didn't stop the job (some files missing from the backup).</summary>
        public BackupJobError? Warning { get; set; }

        public DateTimeOffset? FinishedAt { get; set; }
        public int Attempts { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public bool CancelRequested { get; set; }
        public SpeedMeter Speed { get; } = new(TimeSpan.FromSeconds(5));
        public long LastEmit { get; set; } = long.MinValue;

        /// <summary>Where the backup is being written (an iPhone's live folder, an Android backup's folder).</summary>
        public string? Folder { get; set; }
    }

    private BackupJob Add(Job job, bool exclusive)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                throw new InvalidOperationException("The backup service hasn't started.");
            }

            if (exclusive && _jobs.Any(j => j.DeviceId == job.DeviceId && IsDeviceJob(j) && j.State is BackupJobState.Queued or BackupJobState.Running or BackupJobState.WaitingForDevice))
            {
                throw new DeviceException(BackupErrors.Busy, $"{job.DeviceName} is already busy with a backup, restore or encryption change.");
            }

            _jobs.Add(job);
            Launch(job);
            return Snapshot(job);
        }
    }

    private static bool IsDeviceJob(Job job) => job.Kind is BackupJobKind.Backup or BackupJobKind.Restore or BackupJobKind.Encryption;

    // Caller holds _gate.
    private void Launch(Job job)
    {
        job.State = BackupJobState.Running;
        job.Attempts++;
        job.Error = null;
        job.Speed.Reset();
        job.Cts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        Emit(job);
        var ct = job.Cts.Token;
        _ = Task.Run(() => RunAsync(job, ct), CancellationToken.None);
    }

    private async Task RunAsync(Job job, CancellationToken ct)
    {
        using var hold = _keepAwake.Acquire();
        try
        {
            await job.Work(job, ct).ConfigureAwait(false);
            lock (_gate)
            {
                Finish(job, BackupJobState.Done, job.Warning);
            }
        }
        catch (Exception) when (job.CancelRequested || _life.IsCancellationRequested)
        {
            lock (_gate)
            {
                Finish(job, BackupJobState.Cancelled, null);
            }
        }
        catch (Exception ex) when (job.ResumesOnReconnect && TransferQueue.IsDeviceUnavailable(ex))
        {
            lock (_gate)
            {
                job.State = BackupJobState.WaitingForDevice;
                job.Phase = null;
                Emit(job);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                Finish(job, BackupJobState.Failed, Describe(ex));
            }
        }
        finally
        {
            lock (_gate)
            {
                job.Cts?.Dispose();
                job.Cts = null;
            }
        }
    }

    // Caller holds _gate.
    private void Finish(Job job, BackupJobState state, BackupJobError? error)
    {
        job.State = state;
        job.Error = error;
        job.Phase = null;
        job.FinishedAt = _time.GetLocalNow();
        if (state == BackupJobState.Done)
        {
            job.Percent = 100;
        }

        Emit(job);
        var finished = _jobs.Where(j => j.State is BackupJobState.Done or BackupJobState.Failed or BackupJobState.Cancelled).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - _options.KeepFinished)))
        {
            _jobs.Remove(old);
        }
    }

    private IProgress<BackupProgress> Reporter(Job job) => new Inline<BackupProgress>(p => Report(job, p.Phase, p.Percent, p.Bytes, p.Files));

    private void Report(Job job, BackupPhase? phase, double? percent, long? bytes, int? files)
    {
        lock (_gate)
        {
            if (job.State != BackupJobState.Running)
            {
                return;
            }

            var phaseChanged = phase is not null && phase != job.Phase;
            job.Phase = phase ?? job.Phase;
            job.Percent = percent ?? job.Percent;
            job.Bytes = bytes ?? job.Bytes;
            job.Files = files ?? job.Files;
            var now = _time.GetUtcNow().UtcTicks;
            job.Speed.Add(now, job.Bytes);
            if (phaseChanged || now - job.LastEmit >= _options.ProgressInterval.Ticks)
            {
                job.LastEmit = now;
                Emit(job);
            }
        }
    }

    private static double? Percent(long done, Job job) =>
        job.TotalBytes is > 0 ? Math.Min(100, 100.0 * done / job.TotalBytes.Value)
        : job.TotalFiles is > 0 ? Math.Min(100, 100.0 * done / job.TotalFiles.Value)
        : null;

    // Caller holds _gate.
    private void Emit(Job job) => _outbox.Writer.TryWrite(Snapshot(job));

    private BackupJob Snapshot(Job job) => new(
        job.Id, job.DeviceId, job.DeviceName, job.Kind, job.Title, job.State, job.Phase, job.Percent is { } p ? Math.Round(p, 1) : null,
        job.Bytes, job.Files, job.TotalBytes, job.TotalFiles, job.State == BackupJobState.Running ? job.Speed.BytesPerSecond : null,
        job.BackupId, job.Destination, job.Prune, job.Error, job.CreatedAt, job.FinishedAt);

    private async Task PumpAsync()
    {
        await foreach (var snapshot in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                JobUpdated?.Invoke(this, snapshot);
            }
            catch (Exception)
            {
                // A listener's failure must not stop the others.
            }
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        if (e.Change != DeviceChange.Removed && e.Summary.State is DeviceState.Ready or DeviceState.Locked)
        {
            RetryWaiting(e.Summary.Id);
        }
    }

    private void RetryWaiting(string? deviceId)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var job in _jobs.Where(j => j.State == BackupJobState.WaitingForDevice && (deviceId is null || j.DeviceId == deviceId)).ToList())
            {
                if (_hub.TryGetDevice(job.DeviceId!, out var device) && device.Summary.State is DeviceState.Ready or DeviceState.Locked)
                {
                    Launch(job);
                }
            }
        }
    }

    private static BackupJobError Describe(Exception ex) => ex switch
    {
        DeviceException d => new BackupJobError(d.Code, d.Message),
        UnauthorizedAccessException => new BackupJobError(DeviceErrorCodes.PermissionDenied, "Windows didn't allow writing there. Pick another folder."),
        DirectoryNotFoundException or DriveNotFoundException => new BackupJobError(DeviceErrorCodes.PathNotFound, "The folder isn't on this PC any more."),
        IOException io when (io.HResult & 0xFFFF) is 0x70 or 0x27 => new BackupJobError(BackupErrors.NoSpace, "The PC's disk is full."),
        IOException io => new BackupJobError("internal", io.Message),
        ArgumentException arg => new BackupJobError("internal", arg.Message),
        _ => new BackupJobError("internal", "Something went wrong: " + ex.Message),
    };

    // ---------------------------------------------------------------- helpers

    private BackupRecord Find(string backupId)
    {
        if (string.IsNullOrEmpty(backupId) || _library.Resolve(backupId) is null)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That backup isn't on this PC any more.");
        }

        return _library.Load(backupId)
               ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That folder doesn't hold a backup DeviceDeck can read.");
    }

    // Caller holds _gate.
    private BackupInfo DecorateLocked(BackupRecord record)
    {
        var info = record.Info;
        var locked = info.Encrypted && !(_sessions.TryGetValue(info.Id, out var session) && session.Unlocked);
        return info with { Locked = locked, State = IsBeingWritten(record) ? BackupState.InProgress : info.State };
    }

    // Caller holds _gate.
    private bool IsBeingWritten(BackupRecord record) =>
        _jobs.Any(j => j.Kind == BackupJobKind.Backup && j.State is BackupJobState.Running or BackupJobState.WaitingForDevice or BackupJobState.Queued
                       && j.Folder is { } folder && string.Equals(folder, record.Folder, StringComparison.OrdinalIgnoreCase));

    private void PreserveLive(string live, string udid, CancellationToken ct)
    {
        if (_ios?.ReadHeader(live) is not { Complete: true, SnapshotId: { } snapshot } header)
        {
            return;
        }

        var copied = _library.Scan().Any(r => !r.IsLive && r.Ios?.SnapshotId == snapshot && r.Info.Source == BackupSource.DeviceDeck);
        if (!copied)
        {
            _library.Snapshot(live, udid, header.Date ?? _time.GetUtcNow(), ct);
        }
    }

    private string PrepareRoot()
    {
        var root = _library.Root;
        try
        {
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, $"DeviceDeck can't use the backup folder {root}: {ex.Message} Choose another one in Settings.", ex);
        }
    }

    private static DateTime Stamp(string folder)
    {
        var manifest = Path.Combine(folder, "Manifest.db");
        return File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest) : File.GetLastWriteTimeUtc(Path.Combine(folder, AndroidBackupManifest.FileName));
    }

    private static bool IsNewer(string? backupVersion, string phoneVersion) =>
        Version.TryParse(Pad(backupVersion), out var backup) && Version.TryParse(Pad(phoneVersion), out var phone) && backup > phone;

    private static string? Pad(string? version) => version is null ? null : version.Count(c => c == '.') == 0 ? version + ".0" : version;

    private static void RequirePcFolder(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
        {
            throw new ArgumentException("destination must be a full path on this PC.");
        }
    }

    private static string Leaf(BackupItemRef item) => item.Path.Trim('/') is { Length: > 0 } path ? path[(path.LastIndexOf('/') + 1)..] : item.Domain;

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

    /// <summary>The path itself, or "name (2).ext", "name (3).ext"… when something is already there.</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A name free for both "{stem}{ext}" and its "{stem} files" folder.</summary>
    private static string UniqueStem(string folder, string stem, string ext)
    {
        bool Free(string s) => !File.Exists(Path.Combine(folder, s + ext)) && !File.Exists(Path.Combine(folder, s + ".html"))
                               && !Directory.Exists(Path.Combine(folder, s + " files"));
        if (Free(stem))
        {
            return stem;
        }

        for (var n = 2; ; n++)
        {
            if (Free($"{stem} ({n})"))
            {
                return $"{stem} ({n})";
            }
        }
    }

    private static string NewId() => Guid.CreateVersion7().ToString("N");

    private void RemoveLeftovers()
    {
        try
        {
            var root = _library.Root;
            if (Directory.Exists(root))
            {
                DeleteFolders(Directory.EnumerateDirectories(root, "*.deleting").Concat(Directory.EnumerateDirectories(root, "*.partial")).ToList());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Folders the user already agreed to delete (renamed *.deleting) and unfinished dated copies (*.partial).</summary>
    private static void DeleteFolders(IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again at the next start.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Session> sessions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }
        _hub.DeviceChanged -= OnDeviceChanged;
        _retry?.Dispose();
        await _life.CancelAsync().ConfigureAwait(false);
        _outbox.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
