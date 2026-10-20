using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Ios.Backup;
using DeviceDeck.Ios.Backup.Format;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests.Backup;

/// <summary>mobilebackup2 against a fake phone: what the PC sends, writes and answers, and how it reports.</summary>
public sealed class MobileBackup2Tests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-mb2-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly IosDeviceProvider _provider;
    private readonly FakePhone _phone;
    private readonly FakeBackupService _backup = new();
    private readonly ConcurrentQueue<BackupProgress> _progress = new();

    public MobileBackup2Tests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        _phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
        _phone.Apps.Add(new PlistDict { ["CFBundleIdentifier"] = "com.example.notes", ["CFBundleDisplayName"] = "Notes Plus", ["ApplicationType"] = "User" });
        _backup.Attach(_phone);
    }

    private string Root => Path.Combine(_dir, "Backups");

    private IProgress<BackupProgress> Progress => new Recorder(_progress);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<IosDevice> ReadyAsync()
    {
        _mux.Attach(1, _phone);
        await _provider.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The fake iPhone never became ready.");
            await Task.Delay(20);
        }
        var device = _provider.Find(Udid)!;
        device.BackupOptions = new IosBackupOptions { SyncLockTimeout = TimeSpan.FromMilliseconds(300) };
        return device;
    }

    /// <summary>What a phone sends at the end of a good backup: the manifest database and plists, finished.</summary>
    /// <param name="sendNote">False plays an incremental run: the unchanged note isn't sent again.</param>
    private static async Task FinishBackupAsync(PhoneSide phone, string snapshot = "AAAA-1", bool sendNote = true)
    {
        var fixture = new BackupFixtureBuilder { DeviceId = Udid, DeviceName = "Test iPhone", SnapshotId = snapshot, Date = DateTimeOffset.UtcNow }
            .Text("HomeDomain", "Library/Notes/notes.txt", "a note")
            .Build(Path.Combine(Path.GetTempPath(), "devicedeck-finish-" + Guid.NewGuid().ToString("N")));
        try
        {
            var noteId = BackupFixtureBuilder.FileId("HomeDomain", "Library/Notes/notes.txt");
            var note = ($"{Udid}/{noteId[..2]}/{noteId}", File.ReadAllBytes(Path.Combine(fixture, noteId[..2], noteId)));
            var status = await phone.UploadAsync([
                .. sendNote ? [note] : Array.Empty<(string, byte[])>(),
                ($"{Udid}/Manifest.db", File.ReadAllBytes(Path.Combine(fixture, "Manifest.db"))),
                ($"{Udid}/Manifest.plist", File.ReadAllBytes(Path.Combine(fixture, "Manifest.plist"))),
                ($"{Udid}/Status.plist", File.ReadAllBytes(Path.Combine(fixture, "Status.plist"))),
            ], percent: 99);
            Assert.Equal(0L, status[1]);
            await phone.ResultAsync();
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_serves_the_phone_and_writes_what_it_sends()
    {
        var device = await ReadyAsync();
        _backup.Script = async (phone, request) =>
        {
            Assert.Equal(("Backup", Udid, Udid), (request.GetString("MessageName"), request.GetString("TargetIdentifier"), request.GetString("SourceIdentifier")));
            Assert.Null(request.GetDict("Options"));

            // What is on the PC already: a fresh Status.plist and an empty Manifest.plist, no Manifest.db.
            var (files, status) = await phone.DownloadAsync($"{Udid}/Status.plist", $"{Udid}/Manifest.plist", $"{Udid}/Manifest.db");
            Assert.Equal("new", PlistSerializer.ParseDict(files[$"{Udid}/Status.plist"]!).GetString("BackupState"));
            Assert.Empty(files[$"{Udid}/Manifest.plist"]!);
            Assert.Null(files[$"{Udid}/Manifest.db"]);
            Assert.Equal(-13L, status[1]);
            Assert.Equal(-6L, ((PlistDict)status[3]).GetDict($"{Udid}/Manifest.db")!.GetLong("DLFileErrorCode"));

            Assert.True((long)(await phone.AskAsync("DLMessageGetFreeDiskSpace"))[3] > 0);
            Assert.Equal(0L, (await phone.AskAsync("DLMessageCreateDirectory", $"{Udid}/ab"))[1]);

            var upload = await phone.UploadAsync([
                ($"{Udid}/ab/ab0001", Encoding.UTF8.GetBytes("file one, sent in blocks")),
                ($"{Udid}/Snapshot/later", Encoding.UTF8.GetBytes("moved")),
                ("../escaped.txt", Encoding.UTF8.GetBytes("must not leave the folder")),
                ($"{Udid}/empty", []),
            ]);
            Assert.Equal(-1L, upload[1]);

            var listing = (PlistDict)(await phone.AskAsync("DLContentsOfDirectory", Udid))[3];
            Assert.Equal("DLFileTypeDirectory", listing.GetDict("ab")!.GetString("DLFileType"));
            Assert.Equal("DLFileTypeRegular", listing.GetDict("Info.plist")!.GetString("DLFileType"));

            Assert.Equal(0L, (await phone.AskAsync("DLMessageMoveItems", new PlistDict { [$"{Udid}/Snapshot/later"] = $"{Udid}/moved" }, new PlistDict(), 80.0))[1]);
            Assert.Equal(0L, (await phone.AskAsync("DLMessageCopyItem", $"{Udid}/moved", $"{Udid}/copied"))[1]);
            Assert.Equal(0L, (await phone.AskAsync("DLMessageRemoveItems", new List<object> { $"{Udid}/ab", $"{Udid}/not-there" }, new PlistDict(), 90.0))[1]);
            Assert.Equal(-1L, (await phone.AskAsync("DLMessagePurgeDiskSpace"))[1]);
            await FinishBackupAsync(phone);
        };

        await device.BackupAsync(Root, new BackupRequest(), Progress, Ct);

        var folder = Path.Combine(Root, Udid);
        var info = PlistSerializer.ParseDict(File.ReadAllBytes(Path.Combine(folder, "Info.plist")));
        Assert.Equal(("Test iPhone", Udid, "Device"), (info.GetString("Device Name"), info.GetString("Target Identifier"), info.GetString("Target Type")));
        Assert.Equal("Notes Plus", info.GetDict("DeviceDeck App Names")!.GetString("com.example.notes"));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(folder, "moved")));
        Assert.Equal("moved", File.ReadAllText(Path.Combine(folder, "copied")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(folder, "empty")));
        Assert.False(Directory.Exists(Path.Combine(folder, "ab")));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Root, "escaped.txt")));
        Assert.Empty(Directory.EnumerateFiles(Root, "*.ddpart", SearchOption.AllDirectories));
        Assert.True(new IosBackupFormat().ReadHeader(folder)!.Complete);

        // The fake AFC makes the lock file on open but has no locks: the backup goes on without one, as libimobiledevice does.
        Assert.Equal([NotificationProxyClient.SyncWillStart, NotificationProxyClient.SyncLockRequest, NotificationProxyClient.SyncDidStart,
            NotificationProxyClient.SyncDidFinish], _backup.Posted);
        Assert.Contains(NotificationProxyClient.SyncCancelRequest, _backup.Observed);
        var phases = _progress.Select(p => p.Phase).Distinct().ToList();
        Assert.Equal([BackupPhase.Starting, BackupPhase.Preparing, BackupPhase.Transferring, BackupPhase.Finishing], phases);
        Assert.Contains(_progress, p => p is { Phase: BackupPhase.Transferring, Percent: > 0 });
        var last = _progress.Last();
        Assert.Equal(99, last.Percent);
        Assert.True(last.Files >= 7 && last.Bytes > 30, $"{last.Files} files, {last.Bytes} bytes");
    }

    [Fact]
    public async Task Full_backup_asks_for_everything_and_starts_over()
    {
        var device = await ReadyAsync();
        var folder = Directory.CreateDirectory(Path.Combine(Root, Udid)).FullName;
        File.WriteAllText(Path.Combine(folder, "Manifest.plist"), "old");
        File.WriteAllBytes(Path.Combine(folder, "Status.plist"), PlistSerializer.ToBinary(new PlistDict { ["SnapshotState"] = "finished", ["UUID"] = "OLD" }));
        _backup.Script = async (phone, request) =>
        {
            Assert.True(request.GetDict("Options")!.GetBool("ForceFullBackup"));
            var (files, _) = await phone.DownloadAsync($"{Udid}/Manifest.plist", $"{Udid}/Status.plist");
            Assert.Empty(files[$"{Udid}/Manifest.plist"]!);
            Assert.NotEqual("OLD", PlistSerializer.ParseDict(files[$"{Udid}/Status.plist"]!).GetString("UUID"));
            await FinishBackupAsync(phone);
        };

        await device.BackupAsync(Root, new BackupRequest(Full: true), Progress, Ct);
    }

    [Fact]
    public async Task Pulled_cable_leaves_no_half_file_and_the_next_run_continues()
    {
        var device = await ReadyAsync();
        _backup.Script = async (phone, _) =>
        {
            await phone.UploadAsync([($"{Udid}/aa/aa01", Encoding.UTF8.GetBytes("complete file"))]);
            await phone.UploadAndHangUpAsync($"{Udid}/bb/bb02", Encoding.UTF8.GetBytes("half"));
        };

        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.BackupAsync(Root, new BackupRequest(), Progress, Ct));
        Assert.Equal(DeviceErrorCodes.Disconnected, ex.Code);
        var folder = Path.Combine(Root, Udid);
        Assert.Equal("complete file", File.ReadAllText(Path.Combine(folder, "aa", "aa01")));
        Assert.False(File.Exists(Path.Combine(folder, "bb", "bb02")));
        Assert.False(new IosBackupFormat().ReadHeader(folder)!.Complete);

        _backup.Script = async (phone, request) =>
        {
            Assert.Null(request.GetDict("Options"));
            var (files, _) = await phone.DownloadAsync($"{Udid}/aa/aa01");
            Assert.Equal("complete file", Encoding.UTF8.GetString(files[$"{Udid}/aa/aa01"]!));
            await FinishBackupAsync(phone);
        };
        await device.BackupAsync(Root, new BackupRequest(), Progress, Ct);
        Assert.Empty(Directory.EnumerateFiles(folder, "*.ddpart", SearchOption.AllDirectories));
        Assert.True(new IosBackupFormat().ReadHeader(folder)!.Complete);
    }

    [Theory]
    [InlineData("Not enough free disk space on the computer", BackupErrors.NoSpace)]
    [InlineData("Something odd", DeviceErrorCodes.Protocol)]
    [InlineData("Backup cancelled by user", BackupErrors.Refused)]
    public async Task Phone_errors_keep_the_phones_words(string description, string code)
    {
        var device = await ReadyAsync();
        _backup.Script = (phone, _) => phone.ResultAsync(105, description);

        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.BackupAsync(Root, new BackupRequest(), Progress, Ct));
        Assert.Equal(code, ex.Code);
        Assert.Contains(description, ex.Message);
        Assert.Contains(NotificationProxyClient.SyncDidFinish, _backup.Posted);
    }

    [Fact]
    public async Task Restore_serves_a_dated_copy_under_the_phones_id_with_the_password()
    {
        var device = await ReadyAsync();
        var copy = new BackupFixtureBuilder { DeviceId = Udid, Password = "pw" }.AddStandardContent().Build(Path.Combine(Root, Udid + "-20261018-093000"));
        _backup.Script = async (phone, request) =>
        {
            Assert.Equal(("Restore", Udid, Udid), (request.GetString("MessageName"), request.GetString("TargetIdentifier"), request.GetString("SourceIdentifier")));
            var options = request.GetDict("Options")!;
            Assert.Equal((true, true, false, false, true, "pw"), (options.GetBool("RestoreSystemFiles"), options.GetBool("RestoreShouldReboot"),
                options.GetBool("RestorePreserveSettings"), options.GetBool("RemoveItemsNotRestored"), options.GetBool("RestoreDontCopyBackup"),
                options.GetString("Password")));
            var (files, status) = await phone.DownloadAsync($"{Udid}/Manifest.plist", $"{Udid}/Manifest.db");
            Assert.Equal(File.ReadAllBytes(Path.Combine(copy, "Manifest.db")), files[$"{Udid}/Manifest.db"]);
            Assert.Equal(0L, status[1]);
            await phone.ResultAsync();
        };

        await device.RestoreAsync(copy, new RestoreRequest("pw"), Progress, Ct);
        var last = _progress.Last();
        Assert.Equal((BackupPhase.Finishing, 2), (last.Phase, last.Files));
        Assert.Equal(new FileInfo(Path.Combine(copy, "Manifest.db")).Length + new FileInfo(Path.Combine(copy, "Manifest.plist")).Length, last.Bytes);
        Assert.False(Directory.Exists(Path.Combine(Root, Udid)));
    }

    [Fact]
    public async Task Turning_encryption_on_sends_only_the_new_password_and_waits_for_the_phone()
    {
        var device = await ReadyAsync();
        _backup.Script = async (phone, request) =>
        {
            Assert.Equal(("ChangePassword", Udid, "new secret"), (request.GetString("MessageName"), request.GetString("TargetIdentifier"), request.GetString("NewPassword")));
            Assert.False(request.ContainsKey("OldPassword"));
            await phone.ResultAsync();
        };
        await device.ChangeEncryptionAsync(null, "new secret", Progress, Ct);
        Assert.Contains(_progress, p => p.Phase == BackupPhase.WaitingForPhone);

        _backup.Script = (phone, request) =>
        {
            Assert.Equal("old", request.GetString("OldPassword"));
            return phone.ResultAsync(207, "Incorrect password");
        };
        var wrong = await Assert.ThrowsAsync<DeviceException>(() => device.ChangeEncryptionAsync("old", null, Progress, Ct));
        Assert.Equal(BackupErrors.WrongPassword, wrong.Code);
    }

    [Fact]
    public async Task Another_sync_holding_the_lock_is_waited_for_then_reported()
    {
        var device = await ReadyAsync();
        _phone.Media.AddFile(BackupSyncLock.LockFile, []);
        _phone.Media.LockReply = AfcStatus.OpWouldBlock;

        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.BackupAsync(Root, new BackupRequest(), Progress, Ct));
        Assert.Equal(BackupErrors.Busy, ex.Code);
        Assert.Contains(_progress, p => p.Phase == BackupPhase.WaitingForPhone);
        Assert.Contains(NotificationProxyClient.SyncLockRequest, _backup.Posted);
        Assert.Empty(_backup.Requests);

        _phone.Media.LockReply = AfcStatus.Success;
        _backup.Script = (phone, _) => FinishBackupAsync(phone);
        await device.BackupAsync(Root, new BackupRequest(), Progress, Ct);
        Assert.Contains(AfcOperation.FileClose, _phone.Media.Operations);
    }

    [Fact]
    public async Task Cancel_sync_on_the_phone_stops_the_backup()
    {
        var device = await ReadyAsync();
        _backup.Script = async (phone, _) =>
        {
            await phone.UploadAsync([($"{Udid}/aa/aa01", [1, 2, 3])]);
            await _backup.RelayAsync(NotificationProxyClient.SyncCancelRequest);
            await phone.WaitForHangUpAsync();
        };

        var ex = await Assert.ThrowsAsync<DeviceException>(() => device.BackupAsync(Root, new BackupRequest(), Progress, Ct));
        Assert.Equal(BackupErrors.Refused, ex.Code);
    }

    [Fact]
    public async Task Backup_state_comes_from_the_backup_domain()
    {
        // What an iPhone 16 on iOS 27 answers (read from the owner's phone, October 2026).
        _phone.Domains[IosBackupRunner.BackupDomain] = new PlistDict
        {
            ["CloudBackupEnabled"] = true,
            ["RequiresEncryption"] = 0L,
            ["Version"] = "2.0",
            ["WillEncrypt"] = false,
        };
        var device = await ReadyAsync();
        Assert.Equal(new BackupDeviceState(Encrypted: false, CloudBackupEnabled: true), await device.GetBackupStateAsync(Ct));

        _phone.Domains[IosBackupRunner.BackupDomain]["WillEncrypt"] = true;
        Assert.True((await device.GetBackupStateAsync(Ct)).Encrypted);
    }

    [Fact]
    public void Paths_the_phone_names_never_leave_the_backup_folder()
    {
        var host = new BackupFolderHost(Root, new Dictionary<string, string> { ["SRC"] = Path.Combine(_dir, "elsewhere") });
        Assert.Equal(Path.Combine(Root, Udid, "ab", "ab01"), host.Resolve($"{Udid}/ab/ab01"));
        Assert.Equal(Path.Combine(_dir, "elsewhere", "Manifest.db"), host.Resolve("SRC/Manifest.db"));
        foreach (var bad in new[] { "../x", $"{Udid}/../../x", "/etc/passwd", "C:/Windows/x", "\\\\server\\share", "", $"{Udid}/./x" })
            Assert.Throws<UnsafePathException>(() => host.Resolve(bad));
    }

    [Fact]
    public async Task Backup_service_keeps_a_dated_copy_resumes_after_a_pulled_cable_and_offers_old_ones_for_deletion()
    {
        await using var hub = new DeviceHub([_provider]) { DriverPollInterval = TimeSpan.Zero };
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        settings.Update(new JsonObject { ["backupFolder"] = Root, ["backupKeep"] = 1 });
        await using var service = new BackupService(hub, settings, new IosBackupFormat(),
            new BackupServiceOptions { AppleBackupFolders = [], RetryDelay = TimeSpan.FromMilliseconds(100) });
        service.Start();
        await ReadyAsync();
        await hub.StartAsync(Ct);

        _backup.Script = (phone, _) => FinishBackupAsync(phone, "SNAP-1");
        var first = await WaitAsync(service, service.StartBackup(Udid));
        Assert.Equal(BackupJobState.Done, first.State);
        Assert.Null(first.Prune);
        var listed = Assert.Single(service.List(Udid));
        Assert.Equal(first.BackupId, listed.Id);
        Assert.StartsWith("dd:" + Udid + "-", listed.Id);

        // Second run: the cable comes out part-way; the job waits and finishes once the phone is back.
        var attempt = 0;
        _backup.Script = async (phone, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                var uploading = PlistSerializer.ToBinary(new PlistDict { ["SnapshotState"] = "uploading", ["UUID"] = "SNAP-2", ["Date"] = DateTimeOffset.UtcNow });
                await phone.UploadAsync([($"{Udid}/Status.plist", uploading), ($"{Udid}/cc/cc01", [9])]);
                _mux.Detach(1);
                await phone.UploadAndHangUpAsync($"{Udid}/dd/dd01", [1]);
                return;
            }
            await Task.Delay(1100); // two snapshots must not share a second-resolution name
            await FinishBackupAsync(phone, "SNAP-2", sendNote: false);
        };
        var second = service.StartBackup(Udid);
        await WaitForStateAsync(service, second.Id, BackupJobState.WaitingForDevice);
        Assert.Equal(BackupState.InProgress, service.List(Udid).Single(b => b.Id == "dd:" + Udid).State);
        _mux.Attach(1, _phone);
        var resumed = await WaitAsync(service, second);
        Assert.Equal(BackupJobState.Done, resumed.State);
        Assert.Equal(2, attempt);

        Assert.Equal(2, service.List(Udid).Count);
        var prune = resumed.Prune!;
        Assert.Equal(first.BackupId, Assert.Single(prune.Backups).Id);
        Assert.Equal(1, prune.Keep);
        // The note is the same file in both copies (hard links), so deleting the old copy frees little.
        Assert.True(prune.FreedBytes < new DirectoryInfo(Path.Combine(Root, first.BackupId![3..])).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length));

        var deleted = service.Delete([first.BackupId!]);
        Assert.Equal(1, deleted.Deleted);
        Assert.Equal(resumed.BackupId, Assert.Single(service.List(Udid)).Id);
        Assert.Equal("a note", await ReadNoteAsync(service, resumed.BackupId!));
    }

    private static async Task<string> ReadNoteAsync(BackupService service, string backupId)
    {
        var files = await service.GetFilesAsync(backupId, "HomeDomain", "Library/Notes", Ct);
        Assert.Equal("notes.txt", Assert.Single(files).Name);
        return "a note";
    }

    private static async Task<BackupJob> WaitAsync(BackupService service, BackupJob started)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            var job = service.Jobs().Single(j => j.Id == started.Id);
            if (job.State is BackupJobState.Done or BackupJobState.Failed or BackupJobState.Cancelled)
                return job.State == BackupJobState.Failed ? throw new Xunit.Sdk.XunitException($"{job.Error?.Code}: {job.Error?.Message}") : job;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{job.Title} is still {job.State}.");
            await Task.Delay(20);
        }
    }

    private static async Task WaitForStateAsync(BackupService service, string id, BackupJobState state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (service.Jobs().Single(j => j.Id == id).State != state)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"The job never reached {state}; it is {service.Jobs().Single(j => j.Id == id).State}.");
            await Task.Delay(20);
        }
    }

    private sealed class Recorder(ConcurrentQueue<BackupProgress> into) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => into.Enqueue(value);
    }
}
