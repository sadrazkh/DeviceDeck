using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>Incremental backup to the PC end to end: queue, handler, ledger, destination index and the bridge methods.</summary>
public sealed class PhotosBackupTests
{
    private static readonly DateTimeOffset Oct = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Aug = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    private static string Month(DateTimeOffset when) => when.ToLocalTime().ToString("yyyy/MM", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task First_backup_copies_everything_into_month_folders_and_the_next_finds_nothing_new()
    {
        var phone = new PhotosTestPhone();
        var a = phone.Library.Add("IMG_0001.HEIC", Aug);
        var b = phone.Library.Add("IMG_0002.JPG", Oct);
        var v = phone.Library.Add("IMG_0003.MOV", Oct, kind: PhotoKind.Video);
        await using var host = await PhotosBackupHost.StartAsync(phone);

        var before = await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((3, 2, 1), (before.NewCount, before.NewPhotos, before.NewVideos));
        Assert.Equal(a.Size + b.Size + v.Size, before.NewBytes);

        var job = await host.BackUpAsync(phone);
        Assert.Equal((3, 0), (job.DoneItems, job.FailedItems));
        Assert.Equal(TransferKind.PhotosBackup, job.Kind);
        Assert.Equal([$"{Month(Aug)}/IMG_0001.HEIC", $"{Month(Oct)}/IMG_0002.JPG", $"{Month(Oct)}/IMG_0003.MOV"], host.Files(phone));
        var copied = Path.Combine(host.Destination(phone), Month(Aug).Replace('/', '\\'), "IMG_0001.HEIC");
        Assert.Equal(phone.Library.FileOf(a.AssetId), await File.ReadAllBytesAsync(copied));
        Assert.Equal(Aug.UtcDateTime, File.GetLastWriteTimeUtc(copied));

        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((PhotoBackupRunState.Done, 3, 3, 0, 0), (run.State, run.Planned, run.Copied, run.Duplicates, run.Failed));
        Assert.Equal(a.Size + b.Size + v.Size, run.Bytes);

        var after = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal(0, after.Count.NewCount);
        Assert.Equal(3, after.BackedUp);
        Assert.Equal(job.Id, after.LastRun!.JobId);
        Assert.Null(after.ActiveJobId);

        var reads = phone.Library.Reads;
        var again = await host.BackUpAsync(phone);
        Assert.Equal(0, again.TotalItems);
        Assert.Equal(reads, phone.Library.Reads);
    }

    [Fact]
    public async Task Only_photos_taken_since_the_last_backup_are_copied()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Aug);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        await host.BackUpAsync(phone);

        phone.Library.Add("IMG_0002.HEIC", Oct);
        Assert.Equal(1, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewCount);
        var reads = phone.Library.Reads;
        var job = await host.BackUpAsync(phone);
        Assert.Equal(1, job.TotalItems);
        Assert.Equal(reads + 1, phone.Library.Reads);
        Assert.Equal([$"{Month(Aug)}/IMG_0001.HEIC", $"{Month(Oct)}/IMG_0002.HEIC"], host.Files(phone));
    }

    [Fact]
    public async Task Bytes_already_in_the_destination_are_remembered_not_written_again()
    {
        var phone = new PhotosTestPhone();
        var a = phone.Library.Add("IMG_0001.HEIC", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        // The same photo, copied earlier by hand under another name and folder.
        var earlier = Path.Combine(host.Destination(phone), "From the old PC", "holiday.heic");
        Directory.CreateDirectory(Path.GetDirectoryName(earlier)!);
        await File.WriteAllBytesAsync(earlier, phone.Library.FileOf(a.AssetId));
        // Same size, other bytes: must not count as the same photo.
        var lookalike = Path.Combine(host.Destination(phone), "From the old PC", "other.heic");
        var other = phone.Library.FileOf(a.AssetId).ToArray();
        other[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(lookalike, other);

        var job = await host.BackUpAsync(phone);
        Assert.Equal(1, job.DoneItems);
        Assert.Equal(["From the old PC/holiday.heic", "From the old PC/other.heic"], host.Files(phone));
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((0, 1), (run.Copied, run.Duplicates));
        var record = host.Backup.Ledger.Find(phone.Summary.Id, a.Key)!;
        Assert.True(record.Duplicate);
        Assert.Equal(earlier, record.PcPath);
        Assert.Equal(0, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewCount);
    }

    [Fact]
    public async Task A_different_file_under_the_same_name_makes_a_numbered_copy()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        phone.Library.Add("IMG_0001.HEIC", Oct.AddDays(9)); // IMG_0001 came round again on the phone
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var taken = Path.Combine(host.Destination(phone), Month(Oct).Replace('/', '\\'), "IMG_0001.HEIC");
        Directory.CreateDirectory(Path.GetDirectoryName(taken)!);
        await File.WriteAllBytesAsync(taken, [1, 2, 3]);

        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month(Oct)}/IMG_0001 (1).HEIC", $"{Month(Oct)}/IMG_0001 (2).HEIC", $"{Month(Oct)}/IMG_0001.HEIC"], host.Files(phone));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(taken));
    }

    [Fact]
    public async Task Both_halves_of_a_live_photo_get_one_name_even_when_it_is_taken()
    {
        var phone = new PhotosTestPhone();
        var live = phone.Library.Add("IMG_0002.HEIC", Oct, motion: true);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        // Only the video's name is taken (by something else): both move on to "(1)" together.
        var folder = Path.Combine(host.Destination(phone), Month(Oct).Replace('/', '\\'));
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "IMG_0002.MOV"), [7, 7, 7]);

        var job = await host.BackUpAsync(phone);
        Assert.Equal(1, job.TotalItems);
        Assert.Equal(live.Size + live.Motion!.Size, job.TotalBytes);
        Assert.Equal([$"{Month(Oct)}/IMG_0002 (1).HEIC", $"{Month(Oct)}/IMG_0002 (1).MOV", $"{Month(Oct)}/IMG_0002.MOV"], host.Files(phone));
        Assert.Equal(phone.Library.FileOf(live.Motion.AssetId), await File.ReadAllBytesAsync(Path.Combine(folder, "IMG_0002 (1).MOV")));
        var record = host.Backup.Ledger.Find(phone.Summary.Id, live.Key)!;
        Assert.EndsWith("IMG_0002 (1).MOV", record.MotionPcPath);
    }

    [Fact]
    public async Task A_live_photo_already_there_gets_its_missing_video_next_to_it()
    {
        var phone = new PhotosTestPhone();
        var live = phone.Library.Add("IMG_0005.HEIC", Oct, motion: true);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var old = Path.Combine(host.Destination(phone), "old", "IMG_0005.HEIC");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        await File.WriteAllBytesAsync(old, phone.Library.FileOf(live.AssetId));

        var job = await host.BackUpAsync(phone);
        Assert.Equal(["old/IMG_0005.HEIC", "old/IMG_0005.MOV"], host.Files(phone));
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((1, 0, live.Motion!.Size), (run.Copied, run.Duplicates, run.Bytes));
    }

    [Fact]
    public async Task A_live_photo_whose_video_is_gone_backs_up_the_still()
    {
        var phone = new PhotosTestPhone();
        var live = phone.Library.Add("IMG_0009.HEIC", Oct, motion: true);
        phone.Library.DropFile(live.Motion!.AssetId);
        await using var host = await PhotosBackupHost.StartAsync(phone);

        var job = await host.BackUpAsync(phone);
        Assert.Equal((1, 0), (job.DoneItems, job.FailedItems));
        Assert.Equal([$"{Month(Oct)}/IMG_0009.HEIC"], host.Files(phone));
        Assert.Null(host.Backup.Ledger.Find(phone.Summary.Id, live.Key)!.MotionPcPath);
    }

    [Fact]
    public async Task Hidden_and_icloud_only_photos_are_counted_apart_and_hidden_ones_come_only_when_asked()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        phone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        phone.Library.Add("IMG_0003.HEIC", Oct, cloudOnly: true);
        await using var host = await PhotosBackupHost.StartAsync(phone);

        var count = await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((1, 1, 1), (count.NewCount, count.HiddenLeftOut, count.CloudOnly));
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month(Oct)}/IMG_0001.HEIC"], host.Files(phone));

        host.Backup.Configure(phone.Summary.Id, null, null, null, includeHidden: true);
        count = await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((1, 0, 1), (count.NewCount, count.HiddenLeftOut, count.CloudOnly));
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month(Oct)}/IMG_0001.HEIC", $"{Month(Oct)}/IMG_0002.HEIC"], host.Files(phone));
    }

    [Fact]
    public async Task Sizes_the_list_left_out_are_completed_before_planning()
    {
        var phone = new PhotosTestPhone();
        var live = phone.Library.Add("IMG_0001.HEIC", Oct, motion: true);
        phone.Library.SizesNeedCompleting = true;
        await using var host = await PhotosBackupHost.StartAsync(phone);

        Assert.Equal(0, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewBytes);
        Assert.Equal(0, phone.Library.CompleteCalls); // counting stays cheap
        var job = await host.BackUpAsync(phone);
        Assert.Equal(1, phone.Library.CompleteCalls);
        Assert.Equal(live.Size + live.Motion!.Size, job.TotalBytes);
    }

    [Fact]
    public async Task Pause_and_resume_finish_the_backup_without_leftovers()
    {
        var phone = new PhotosTestPhone();
        for (var i = 0; i < 4; i++)
        {
            phone.Library.Add($"IMG_{i:D4}.HEIC", Oct, content: PhotosTestLibrary.ContentFor($"p{i}", 160_000));
        }

        phone.Library.BytesPerSecond = 400_000;
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var job = await host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None);
        await Wait.UntilAsync(() => host.Queue.Get(job.Id) is { State: TransferState.Running, DoneBytes: > 0 }, "the backup to be under way");

        host.Queue.Pause(job.Id);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Paused);
        Assert.Null(host.Backup.Ledger.GetRun(job.Id)!.FinishedAt);
        Assert.Equal(job.Id, (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).ActiveJobId);
        // Asking for a backup again while one is paused gives that one back.
        Assert.Equal(job.Id, (await host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None)).Id);

        phone.Library.BytesPerSecond = null;
        host.Queue.Resume(job.Id);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Done);
        Assert.Equal(4, host.Files(phone).Length);
        Assert.DoesNotContain(Directory.EnumerateFiles(host.Destination(phone), "*", SearchOption.AllDirectories), f => f.EndsWith(".ddpart", StringComparison.Ordinal));
        await Wait.UntilAsync(() => host.Backup.Ledger.GetRun(job.Id)!.State == PhotoBackupRunState.Done, "the run to close");
        Assert.Equal(4, host.Backup.Ledger.GetRun(job.Id)!.Copied);
    }

    [Fact]
    public async Task An_unplugged_phone_waits_and_the_backup_carries_on_when_it_is_back()
    {
        var phone = new PhotosTestPhone();
        for (var i = 0; i < 3; i++)
        {
            phone.Library.Add($"IMG_{i:D4}.HEIC", Oct, content: PhotosTestLibrary.ContentFor($"u{i}", 160_000));
        }

        phone.Library.BytesPerSecond = 400_000;
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var job = await host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None);
        await Wait.UntilAsync(() => host.Queue.Get(job.Id) is { State: TransferState.Running, DoneBytes: > 0 }, "the backup to be under way");

        host.Provider.Unplug(phone);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.WaitingForDevice);
        phone.Library.BytesPerSecond = null;
        host.Provider.PlugBack(phone);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Done);
        Assert.Equal(3, host.Files(phone).Length);
        Assert.Equal(0, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewCount);
    }

    [Fact]
    public async Task A_backup_cut_short_by_closing_the_app_finishes_after_a_restart_without_copying_twice()
    {
        var phone = new PhotosTestPhone();
        for (var i = 0; i < 4; i++)
        {
            phone.Library.Add($"IMG_{i:D4}.HEIC", Oct, content: PhotosTestLibrary.ContentFor($"r{i}", 160_000));
        }

        phone.Library.BytesPerSecond = 400_000;
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var job = await host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None);
        await Wait.UntilAsync(() => host.Queue.Get(job.Id) is { DoneItems: >= 1, State: TransferState.Running }, "one photo to be copied");

        await host.RestartAsync();
        phone.Library.BytesPerSecond = null;
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Done);
        Assert.Equal(4, host.Files(phone).Length);
        Assert.All(host.Files(phone), f => Assert.DoesNotContain("(1)", f));
        Assert.Equal(4, host.Backup.Ledger.CountBackedUp(phone.Summary.Id));
        await Wait.UntilAsync(() => host.Backup.Ledger.GetRun(job.Id)!.State == PhotoBackupRunState.Done, "the run to close after the restart");
    }

    [Fact]
    public async Task Heic_becomes_jpg_with_the_photos_exif_and_stays_heic_without_a_codec()
    {
        var phone = new PhotosTestPhone();
        var heic = phone.Library.Add("IMG_0001.HEIC", Oct, content: TestHeif.Make(TestTiff.Make(orientation: 6), imageBytes: 301));
        phone.Library.Add("IMG_0002.JPG", Oct);
        var transcoder = new FakeJpegTranscoder();
        await using (var host = await PhotosBackupHost.StartAsync(phone, transcoder))
        {
            host.Backup.Configure(phone.Summary.Id, null, "", convertHeic: true, null);
            await host.BackUpAsync(phone);
            Assert.Equal(["IMG_0001.jpg", "IMG_0002.JPG"], host.Files(phone));
            Assert.Equal(1, transcoder.Calls); // the JPG is copied as it is

            var jpeg = await File.ReadAllBytesAsync(Path.Combine(host.Destination(phone), "IMG_0001.jpg"));
            var exif = PhotosHeicMetadata.ReadJpegExif(jpeg)!;
            Assert.Equal(1u, PhotosHeicMetadata.ReadTag(exif, 0x0112));                   // upright now
            Assert.Equal(3024u, PhotosHeicMetadata.ReadTag(exif, 0xA002, exifIfd: true)); // the JPEG's own size
            Assert.Equal(4032u, PhotosHeicMetadata.ReadTag(exif, 0xA003, exifIfd: true));
            Assert.True(exif.AsSpan().IndexOf("2026:10:15 13:00:00"u8) >= 0, "DateTimeOriginal came along");
            Assert.Equal(HeicConversion.Available, host.Backup.HeicConversion);
            Assert.Equal(heic.Key, host.Backup.Ledger.Find(phone.Summary.Id, heic.Key)!.Key);
        }

        var phone2 = new PhotosTestPhone("photos-iphone-2", name: "Second iPhone");
        phone2.Library.Add("IMG_0003.HEIC", Oct, content: TestHeif.Make(TestTiff.Make(orientation: 1), imageBytes: 302));
        await using (var host = await PhotosBackupHost.StartAsync(phone2, new FakeJpegTranscoder(works: false)))
        {
            host.Backup.Configure(phone2.Summary.Id, null, "", convertHeic: true, null);
            var job = await host.BackUpAsync(phone2);
            Assert.Equal(["IMG_0003.HEIC"], host.Files(phone2));
            Assert.Equal(1, host.Backup.Ledger.GetRun(job.Id)!.KeptHeic);
            Assert.Equal(HeicConversion.MissingCodec, host.Backup.HeicConversion);
        }
    }

    [Fact]
    public async Task Exporting_heic_as_jpg_also_keeps_the_exif()
    {
        var phone = new PhotosTestPhone();
        var heic = phone.Library.Add("IMG_0042.HEIC", Oct, content: TestHeif.Make(TestTiff.Make(orientation: 6), imageBytes: 303));
        await using var host = await PhotosBackupHost.StartAsync(phone, new FakeJpegTranscoder());
        var folder = Path.Combine(host.Root, "pc", "Export");
        var job = await host.Queue.EnqueueAsync(phone.Summary.Id, TransferKind.PhotosExport, new PhotosExportRequest([heic.AssetId], folder, true, ""), ConflictRule.Ask, CancellationToken.None);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Done);

        var exif = PhotosHeicMetadata.ReadJpegExif(await File.ReadAllBytesAsync(Path.Combine(folder, "IMG_0042.jpg")))!;
        Assert.Equal(1u, PhotosHeicMetadata.ReadTag(exif, 0x0112));
        Assert.True(exif.AsSpan().IndexOf("2026:10:15 13:00:00"u8) >= 0);
    }

    [Fact]
    public async Task Options_are_per_phone_fall_back_to_settings_and_the_destination_is_pinned_on_first_backup()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);

        var status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.False(status.Customized);
        Assert.Equal(host.Destination(phone), status.Options.Destination);
        Assert.Equal("yyyy/MM", status.Options.FolderPattern);

        host.Settings.Update(new JsonObject { ["photoBackupPattern"] = "yyyy/MM/dd" });
        Assert.Equal("yyyy/MM/dd", (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Options.FolderPattern);
        Assert.Throws<ArgumentException>(() => host.Settings.Update(new JsonObject { ["photoBackupPattern"] = "MMMM yyyy" }));
        Assert.Throws<ArgumentException>(() => host.Backup.Configure(phone.Summary.Id, "relative\\folder", null, null, null));
        Assert.Throws<ArgumentException>(() => host.Backup.Configure(phone.Summary.Id, null, "yyyy/hh", null, null));

        await host.BackUpAsync(phone);
        var day = Oct.ToLocalTime().ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal([$"{day}/IMG_0001.HEIC"], host.Files(phone));

        // A new default folder in Settings doesn't move a phone that already has backups.
        host.Settings.Update(new JsonObject { ["photoBackupFolder"] = Path.Combine(host.Root, "pc", "Elsewhere") });
        status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal(host.Destination(phone), status.Options.Destination);
        Assert.True(status.Customized);

        host.Backup.Configure(phone.Summary.Id, null, null, null, null, useDefaults: true);
        status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal(Path.Combine(host.Root, "pc", "Elsewhere", phone.Summary.Name), status.Options.Destination);
        Assert.False(status.Customized);
    }

    [Fact]
    public async Task Forgetting_the_history_keeps_the_files_and_the_next_backup_only_remembers_them()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        phone.Library.Add("IMG_0002.HEIC", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        await host.BackUpAsync(phone);

        Assert.Equal(2, host.Backup.Forget(phone.Summary.Id));
        Assert.Equal(2, host.Files(phone).Length);
        Assert.Equal(2, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewCount);

        var job = await host.BackUpAsync(phone);
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((0, 2), (run.Copied, run.Duplicates));
        Assert.Equal(2, host.Files(phone).Length);
    }

    [Fact]
    public async Task The_ledger_outlives_a_restart()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var job = await host.BackUpAsync(phone);

        await host.RestartAsync();
        var status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((0, 1), (status.Count.NewCount, status.BackedUp));
        Assert.Equal(job.Id, status.LastRun!.JobId);
        Assert.Equal(PhotoBackupRunState.Done, status.LastRun.State);
    }

    [Fact]
    public async Task Bridge_methods_count_configure_start_and_forget()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        phone.Library.Add("IMG_0002.HEIC", Oct, cloudOnly: true);
        await using var host = await PhotosBackupHost.StartAsync(phone, new FakeJpegTranscoder());
        var bridge = new BridgeCalls(host.Transport);

        var count = (await bridge.ResultAsync("photos.newCount", new { deviceId = phone.Summary.Id }))!.AsObject();
        Assert.Equal(["deviceId", "newCount", "newPhotos", "newVideos", "newBytes", "cloudOnly", "hiddenLeftOut", "outOfRange", "newEdits"], count.Select(p => p.Key));
        Assert.Equal(1, (int)count["newCount"]!);

        var status = (await bridge.ResultAsync("photos.backupStatus", new { deviceId = phone.Summary.Id }))!.AsObject();
        Assert.Equal(["deviceId", "count", "options", "customized", "backedUp", "lastRun", "activeJobId", "heicConversion", "space", "onConnect"], status.Select(p => p.Key));
        Assert.Equal(["destination", "folderPattern", "convertHeic", "includeHidden", "range", "saveEdited"], status["options"]!.AsObject().Select(p => p.Key));
        Assert.Equal("available", (string?)status["heicConversion"]);

        var configured = (await bridge.ResultAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, folderPattern = "" }))!.AsObject();
        Assert.Equal("", (string?)configured["options"]!["folderPattern"]);
        Assert.True((bool)configured["customized"]!);
        var bad = await bridge.CallAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, folderPattern = "yyyy/MMMM" });
        Assert.Equal(-32602, (int)bad["error"]!["code"]!);

        var started = (await bridge.ResultAsync("photos.backup", new { deviceId = phone.Summary.Id }))!;
        var jobId = (string)started["jobId"]!;
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        await Wait.UntilAsync(() => host.Backup.Ledger.GetRun(jobId)?.State == PhotoBackupRunState.Done, "the run to close");
        var last = (await bridge.ResultAsync("photos.backupStatus", new { deviceId = phone.Summary.Id }))!["lastRun"]!.AsObject();
        Assert.Equal(["jobId", "destination", "state", "planned", "copied", "duplicates", "failed", "bytes", "keptHeic", "startedAt", "finishedAt", "trigger", "edited", "editsMissing"], last.Select(p => p.Key));
        Assert.Equal("done", (string?)last["state"]);
        Assert.Equal(1, (int)last["copied"]!);
        await Wait.UntilAsync(() => host.Transport.EventsNamed("transfer.updated").Any(e => (string?)e["params"]!["kind"] == "photosBackup"),
            "a transfer.updated event for the backup");

        var forgot = (await bridge.ResultAsync("photos.backupForget", new { deviceId = phone.Summary.Id }))!;
        Assert.Equal(1, (int)forgot["forgotten"]!);

        var missing = await bridge.CallAsync("photos.newCount", new { deviceId = "nope" });
        Assert.Equal("device_not_found", (string?)missing["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Photos_go_into_the_chosen_apps_documents_and_never_replace_a_file_there()
    {
        var phone = new PhotosTestPhone();
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var pc = Path.Combine(host.Root, "pc", "Pick");
        Directory.CreateDirectory(Path.Combine(pc, "Trip", "Day 2"));
        await File.WriteAllBytesAsync(Path.Combine(pc, "beach.jpg"), [1, 2, 3]);
        await File.WriteAllBytesAsync(Path.Combine(pc, "Trip", "a.heic"), [4, 5]);
        await File.WriteAllBytesAsync(Path.Combine(pc, "Trip", "Day 2", "clip.mov"), [6]);
        await File.WriteAllBytesAsync(Path.Combine(pc, "Trip", "notes.txt"), [9]);
        phone.Documents["/beach.jpg"] = [42];
        // Round 3: each send gets a dated folder of its own; one of today's name is taken already.
        var today = "DeviceDeck " + DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        phone.Documents["/" + today] = [];

        var bridge = new BridgeCalls(host.Transport);
        var response = await bridge.CallAsync("photos.importToApp",
            new { deviceId = phone.Summary.Id, appId = PhotosTestPhone.AppId, files = Array.Empty<string>() },
            [Path.Combine(pc, "beach.jpg"), Path.Combine(pc, "Trip")]);
        var jobId = (string)response["result"]!["jobId"]!;
        var folder = (string)response["result"]!["folder"]!;
        Assert.StartsWith("DeviceDeck 20", folder);
        Assert.EndsWith(" (2)", folder);
        Assert.Equal(folder, (string?)response["result"]!["parts"]![0]!["path"]);
        Assert.Equal(3, (int)response["result"]!["parts"]![0]!["count"]!);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);

        var job = host.Queue.Get(jobId)!;
        Assert.Equal(TransferKind.PhotosToApp, job.Kind);
        Assert.Equal($"Adding 2 items to Documents › {folder} on Test iPhone", job.Title);
        Assert.Equal(["/" + today, $"/{folder}/a.heic", $"/{folder}/beach.jpg", $"/{folder}/clip.mov", "/beach.jpg"],
            phone.Documents.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([42], phone.Documents["/beach.jpg"]);
        Assert.Equal([1, 2, 3], phone.Documents[$"/{folder}/beach.jpg"]);

        var wrongApp = await bridge.CallAsync("photos.importToApp", new { deviceId = phone.Summary.Id, appId = "com.example.none", files = new[] { Path.Combine(pc, "beach.jpg") } });
        Assert.Equal("path_not_found", (string?)wrongApp["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Adding_photos_to_an_app_is_for_iPhones_only_since_Android_has_its_gallery()
    {
        // Android phones share app folders too now, so the capability alone must not let this through.
        var phone = new PhotosTestPhone("photos-android", DevicePlatform.Android, "Test Android");
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var picture = Path.Combine(host.Root, "pc", "beach.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        await File.WriteAllBytesAsync(picture, [1, 2, 3]);

        var response = await new BridgeCalls(host.Transport).CallAsync("photos.importToApp",
            new { deviceId = phone.Summary.Id, appId = PhotosTestPhone.AppId, files = new[] { picture } });

        Assert.Equal("not_supported", (string?)response["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task A_library_without_backup_support_is_read_through_its_pages()
    {
        var fake = new FakeDeviceProvider(new FakeOptions { ArrivalDelay = TimeSpan.Zero, IndexDuration = TimeSpan.Zero, Devices = [TestPhones.Android(photos: 3)] });
        await using var host = await PhotosBackupHost.StartAsync(new PhotosTestPhone(), more: fake);
        await Wait.UntilAsync(() => host.Hub.TryGetDevice(TestPhones.AndroidId, out _), "the fake Android phone");

        Assert.Equal(3, (await host.Backup.CountNewAsync(TestPhones.AndroidId, CancellationToken.None)).NewCount);
        var job = await host.Backup.StartAsync(TestPhones.AndroidId, CancellationToken.None);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Done);
        var root = Path.Combine(host.Root, "pc", "Backup", "Test Android");
        Assert.Equal(3, Directory.EnumerateFiles(root, "*.jpg", SearchOption.AllDirectories).Count());
        Assert.Equal(0, (await host.Backup.CountNewAsync(TestPhones.AndroidId, CancellationToken.None)).NewCount);
    }
}

/// <summary>JSON-RPC calls straight through a <see cref="MemoryTransport"/>.</summary>
internal sealed class BridgeCalls(MemoryTransport transport)
{
    private int _id;

    public Task<JsonObject> CallAsync(string method, object parameters, IReadOnlyList<string>? files = null)
    {
        var id = Interlocked.Increment(ref _id);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = System.Text.Json.JsonSerializer.SerializeToNode(parameters, Core.Serialization.DeviceDeckJson.Options),
        };
        return transport.RequestAsync(request.ToJsonString(), id, files ?? []);
    }

    public async Task<JsonNode?> ResultAsync(string method, object parameters)
    {
        var response = await CallAsync(method, parameters);
        Assert.True(response.ContainsKey("result"), $"{method} failed: {response.ToJsonString()}");
        return response["result"];
    }
}
