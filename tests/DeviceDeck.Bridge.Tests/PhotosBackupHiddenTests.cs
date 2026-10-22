using System.Text.Json;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Serialization;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Bridge.Tests;

/// <summary>
/// The iPhone's Hidden album in the photo backup (round 4): left out, backed up too or alone, into a folder of its own
/// (dated inside) or with the others; the migration from the old switch, and the backup staying incremental across changes.
/// </summary>
public sealed class PhotosBackupHiddenTests
{
    private static readonly DateTimeOffset Oct = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static string Month => Oct.ToLocalTime().ToString("yyyy/MM", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<PhotosBackupHost> StartWithAsync(PhotosTestPhone phone, PhotoBackupHidden hidden, PhotoBackupHiddenLayout layout = PhotoBackupHiddenLayout.Separate,
        string? folder = null)
    {
        var host = await PhotosBackupHost.StartAsync(phone);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, hidden: hidden, hiddenLayout: layout, hiddenFolder: folder);
        return host;
    }

    public static TheoryData<PhotoBackupHidden, PhotoBackupHiddenLayout, string[], (int New, int Hidden, int HiddenLeftOut, int NormalLeftOut)> Combinations => new()
    {
        { PhotoBackupHidden.Exclude, PhotoBackupHiddenLayout.Separate, ["{m}/IMG_0001.HEIC"], (1, 0, 2, 0) },
        { PhotoBackupHidden.Include, PhotoBackupHiddenLayout.Separate, ["{m}/IMG_0001.HEIC", "Hidden/{m}/IMG_0002.HEIC", "Hidden/Undated/IMG_0003.HEIC"], (3, 2, 0, 0) },
        { PhotoBackupHidden.Include, PhotoBackupHiddenLayout.Together, ["{m}/IMG_0001.HEIC", "{m}/IMG_0002.HEIC", "Undated/IMG_0003.HEIC"], (3, 2, 0, 0) },
        { PhotoBackupHidden.Only, PhotoBackupHiddenLayout.Separate, ["Hidden/{m}/IMG_0002.HEIC", "Hidden/Undated/IMG_0003.HEIC"], (2, 2, 0, 1) },
        { PhotoBackupHidden.Only, PhotoBackupHiddenLayout.Together, ["{m}/IMG_0002.HEIC", "Undated/IMG_0003.HEIC"], (2, 2, 0, 1) },
    };

    [Theory]
    [MemberData(nameof(Combinations))]
    public async Task Every_combination_backs_up_the_right_photos_into_the_right_tree(PhotoBackupHidden hidden, PhotoBackupHiddenLayout layout, string[] expected,
        (int New, int Hidden, int HiddenLeftOut, int NormalLeftOut) counts)
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        phone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        phone.Library.MakeUndated(phone.Library.Add("IMG_0003.HEIC", Oct, hidden: true));
        await using var host = await StartWithAsync(phone, hidden, layout);

        var status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((hidden, layout, "Hidden"), (status.Options.Hidden, status.Options.HiddenLayout, status.Options.HiddenFolder));
        Assert.True(status.Customized);
        var count = status.Count;
        Assert.Equal(counts, (count.NewCount, count.NewHidden, count.HiddenLeftOut, count.NormalLeftOut));

        var job = await host.BackUpAsync(phone);
        Assert.Equal((expected.Length, 0), (job.DoneItems, job.FailedItems));
        Assert.Equal(expected.Select(e => e.Replace("{m}", Month)).Order(StringComparer.Ordinal), host.Files(phone));

        // The next look finds nothing new, whatever the combination.
        var again = await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((0, 0), (again.NewCount, again.NewHidden));
    }

    [Fact]
    public async Task The_hidden_folder_can_be_named_and_a_flat_pattern_puts_them_straight_into_it()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        phone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        await using var host = await StartWithAsync(phone, PhotoBackupHidden.Include, folder: "  پنهان ");
        host.Backup.Configure(phone.Summary.Id, null, "", null, null);

        Assert.Equal("پنهان", (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Options.HiddenFolder);
        await host.BackUpAsync(phone);
        Assert.Equal(["IMG_0001.HEIC", "پنهان/IMG_0002.HEIC"], host.Files(phone));
    }

    [Fact]
    public async Task Live_photos_and_edited_versions_follow_their_photo_into_the_hidden_tree()
    {
        var phone = new PhotosTestPhone();
        var live = phone.Library.Add("IMG_0001.HEIC", Oct, motion: true, hidden: true);
        var edited = phone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        phone.Library.Edit(edited, "stamp-1", PhotosTestLibrary.ContentFor("render one", 30_000));
        phone.Library.Add("IMG_0003.HEIC", Oct, motion: true);
        await using var host = await StartWithAsync(phone, PhotoBackupHidden.Include);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, saveEdited: true);

        await host.BackUpAsync(phone);
        Assert.Equal(
            [$"{Month}/IMG_0003.HEIC", $"{Month}/IMG_0003.MOV", $"Hidden/{Month}/IMG_0001.HEIC", $"Hidden/{Month}/IMG_0001.MOV",
                $"Hidden/{Month}/IMG_0002 (edited).heic", $"Hidden/{Month}/IMG_0002.HEIC"],
            host.Files(phone));
        var record = host.Backup.Ledger.Find(phone.Summary.Id, live.Key)!;
        Assert.Equal(Path.Combine(host.Destination(phone), "Hidden", Month.Replace('/', '\\'), "IMG_0001.MOV"), record.MotionPcPath);

        // A later edit of the hidden photo replaces its edited version there, next to it.
        phone.Library.Edit(edited, "stamp-2", PhotosTestLibrary.ContentFor("render two", 30_000));
        await host.BackUpAsync(phone);
        Assert.Equal(6, host.Files(phone).Length);
        Assert.Equal(PhotosTestLibrary.ContentFor("render two", 30_000),
            await File.ReadAllBytesAsync(Path.Combine(host.Destination(phone), "Hidden", Month.Replace('/', '\\'), "IMG_0002 (edited).heic")));
    }

    [Fact]
    public async Task Switching_from_together_to_separate_keeps_copied_files_where_they_are_and_only_new_ones_go_to_hidden()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct, hidden: true);
        await using var host = await StartWithAsync(phone, PhotoBackupHidden.Include, PhotoBackupHiddenLayout.Together);
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_0001.HEIC"], host.Files(phone));

        host.Backup.Configure(phone.Summary.Id, null, null, null, null, hiddenLayout: PhotoBackupHiddenLayout.Separate);
        Assert.Equal(0, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewCount);
        phone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        Assert.Equal((1, 1), Counts(await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)));

        var reads = phone.Library.Reads;
        var job = await host.BackUpAsync(phone);
        Assert.Equal(1, job.TotalItems);
        Assert.Equal(reads + 1, phone.Library.Reads);
        Assert.Equal([$"{Month}/IMG_0001.HEIC", $"Hidden/{Month}/IMG_0002.HEIC"], host.Files(phone));

        // And back: the hidden folder's photos stay in it.
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, hiddenLayout: PhotoBackupHiddenLayout.Together);
        phone.Library.Add("IMG_0003.HEIC", Oct, hidden: true);
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_0001.HEIC", $"{Month}/IMG_0003.HEIC", $"Hidden/{Month}/IMG_0002.HEIC"], host.Files(phone));
    }

    [Fact]
    public async Task A_photo_hidden_or_unhidden_after_its_backup_is_not_copied_again_and_not_moved()
    {
        var phone = new PhotosTestPhone();
        var shown = phone.Library.Add("IMG_0001.HEIC", Oct);
        var hidden = phone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        await using var host = await StartWithAsync(phone, PhotoBackupHidden.Include);
        await host.BackUpAsync(phone);
        string[] files = [$"{Month}/IMG_0001.HEIC", $"Hidden/{Month}/IMG_0002.HEIC"];
        Assert.Equal(files, host.Files(phone));

        phone.Library.SetHidden(shown, true);
        phone.Library.SetHidden(hidden, false);
        foreach (var mode in new[] { PhotoBackupHidden.Include, PhotoBackupHidden.Only, PhotoBackupHidden.Exclude })
        {
            host.Backup.Configure(phone.Summary.Id, null, null, null, null, hidden: mode);
            var count = await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None);
            Assert.Equal((0, 0, 0, 0), (count.NewCount, count.NewHidden, count.HiddenLeftOut, count.NormalLeftOut));
        }

        var reads = phone.Library.Reads;
        var job = await host.BackUpAsync(phone);
        Assert.Equal(0, job.TotalItems);
        Assert.Equal(reads, phone.Library.Reads);
        Assert.Equal(files, host.Files(phone));

        // Its new edit goes next to the copy it has, not into the other tree.
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, saveEdited: true, hidden: PhotoBackupHidden.Include);
        phone.Library.Edit(shown, "stamp-1", PhotosTestLibrary.ContentFor("render", 30_000));
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_0001 (edited).heic", .. files], host.Files(phone));
    }

    [Fact]
    public async Task Duplicates_are_found_across_both_trees()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct, hidden: true);
        phone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        await using var host = await StartWithAsync(phone, PhotoBackupHidden.Include, PhotoBackupHiddenLayout.Together);
        await host.BackUpAsync(phone);
        string[] files = [$"{Month}/IMG_0001.HEIC", $"{Month}/IMG_0002.HEIC"];

        // History forgotten, now apart: the copies with the others are found, nothing goes into Hidden.
        host.Backup.Forget(phone.Summary.Id);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, hiddenLayout: PhotoBackupHiddenLayout.Separate);
        var job = await host.BackUpAsync(phone);
        Assert.Equal((0, 2), (host.Backup.Ledger.GetRun(job.Id)!.Copied, host.Backup.Ledger.GetRun(job.Id)!.Duplicates));
        Assert.Equal(files, host.Files(phone));

        // The other way round: copies in the hidden folder are found when they'd go with the others now, whole files
        // compared too (a phone that can't read part of a file).
        var other = new PhotosTestPhone("other-iphone");
        var a = other.Library.Add("IMG_0003.HEIC", Oct, hidden: true);
        await using var second = await StartWithAsync(other, PhotoBackupHidden.Include);
        await second.BackUpAsync(other);
        Assert.Equal([$"Hidden/{Month}/IMG_0003.HEIC"], second.Files(other));
        second.Backup.Forget(other.Summary.Id);
        other.Library.SetHidden(a, false);
        other.Library.CanReadRanges = false;
        job = await second.BackUpAsync(other);
        Assert.Equal((0, 1), (second.Backup.Ledger.GetRun(job.Id)!.Copied, second.Backup.Ledger.GetRun(job.Id)!.Duplicates));
        Assert.Equal([$"Hidden/{Month}/IMG_0003.HEIC"], second.Files(other));
    }

    [Fact]
    public async Task Counts_and_the_free_space_check_take_only_what_will_be_copied()
    {
        var phone = new PhotosTestPhone();
        var normal = phone.Library.Add("IMG_0001.HEIC", Oct);
        var big = phone.Library.Add("IMG_0002.MOV", Oct, hidden: true, kind: PhotoKind.Video, content: PhotosTestLibrary.ContentFor("hidden video", 3_000_000));
        await using var host = await PhotosBackupHost.StartAsync(phone);
        // Room for the margin and the small photo, not for the hidden video.
        host.DiskSpace = _ => new PcDiskSpace(PhotoBackupSpaces.MinimumMargin + 1_000_000, 500_000_000_000);
        var id = phone.Summary.Id;

        var status = await host.Backup.GetStatusAsync(id, CancellationToken.None);
        Assert.Equal((1, 1, 0, normal.Size, 1), (status.Count.NewCount, status.Count.NewPhotos, status.Count.NewVideos, status.Count.NewBytes, status.Count.HiddenLeftOut));
        Assert.True(status.Space.Enough);

        host.Backup.Configure(id, null, null, null, null, hidden: PhotoBackupHidden.Include);
        status = await host.Backup.GetStatusAsync(id, CancellationToken.None);
        Assert.Equal((2, 1, 1, normal.Size + big.Size, 1), (status.Count.NewCount, status.Count.NewPhotos, status.Count.NewVideos, status.Count.NewBytes, status.Count.NewHidden));
        Assert.False(status.Space.Enough);
        var refused = await Assert.ThrowsAsync<DeviceException>(() => host.Backup.StartAsync(id, CancellationToken.None));
        Assert.Equal(BackupErrors.NoSpace, refused.Code);

        host.Backup.Configure(id, null, null, null, null, hidden: PhotoBackupHidden.Only);
        status = await host.Backup.GetStatusAsync(id, CancellationToken.None);
        Assert.Equal((1, 0, 1, big.Size, 1, 1), (status.Count.NewCount, status.Count.NewPhotos, status.Count.NewVideos, status.Count.NewBytes, status.Count.NewHidden,
            status.Count.NormalLeftOut));
        Assert.False(status.Space.Enough);

        host.Backup.Configure(id, null, null, null, null, hidden: PhotoBackupHidden.Exclude);
        Assert.Equal(1, (await host.Backup.CountNewAsync(id, CancellationToken.None)).NewCount); // the badge's count
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_0001.HEIC"], host.Files(phone));
    }

    [Fact]
    public async Task An_automatic_backup_on_connect_uses_the_same_hidden_options()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", DateTimeOffset.Now.AddDays(-1));
        phone.Library.Add("IMG_0002.HEIC", DateTimeOffset.Now.AddDays(-1), hidden: true);
        await using var host = await StartWithAsync(phone, PhotoBackupHidden.Only);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, onConnect: PhotoBackupOnConnect.BackUp);
        var notifications = new RecordingNotificationService();
        using var watcher = new PhotoBackupWatcher(host.Hub, host.Backup, notifications, host.Settings, new PhotoBackupWatcherOptions { Settle = TimeSpan.Zero });

        watcher.Arm();
        await Wait.UntilAsync(() => notifications.Shown.Any(n => n.Tag == PhotoBackupWatcher.FinishedTagFor(phone.Summary.Id)), "the backup to finish");
        Assert.Equal("1 photo backed up", notifications.Shown.Single(n => n.Tag == PhotoBackupWatcher.FinishedTagFor(phone.Summary.Id)).Title);
        var file = Assert.Single(host.Files(phone));
        Assert.StartsWith("Hidden/", file);
        Assert.EndsWith("/IMG_0002.HEIC", file);
    }

    [Fact]
    public async Task The_settings_defaults_apply_to_an_iphone_and_never_to_an_android_phone()
    {
        var iphone = new PhotosTestPhone();
        iphone.Library.Add("IMG_0001.HEIC", Oct);
        iphone.Library.Add("IMG_0002.HEIC", Oct, hidden: true);
        var android = new PhotosTestPhone("android-1", DevicePlatform.Android, "Pixel");
        android.Library.Add("PXL_0001.jpg", Oct);
        await using var host = await PhotosBackupHost.StartAsync(iphone, more: new PhotosTestProvider(android) { Id = "photos-test-2" });
        host.Settings.Update(new System.Text.Json.Nodes.JsonObject
        {
            ["photoBackupHidden"] = "only",
            ["photoBackupHiddenLayout"] = "separate",
            ["photoBackupHiddenFolder"] = "Private",
        });

        var status = await host.Backup.GetStatusAsync(iphone.Summary.Id, CancellationToken.None);
        Assert.Equal((PhotoBackupHidden.Only, "Private", false), (status.Options.Hidden, status.Options.HiddenFolder, status.Customized));
        Assert.Equal((1, 1), (status.Count.NewCount, status.Count.NormalLeftOut));

        var phone = await host.Backup.GetStatusAsync(android.Summary.Id, CancellationToken.None);
        Assert.Equal((PhotoBackupHidden.Exclude, 1, 0), (phone.Options.Hidden, phone.Count.NewCount, phone.Count.NormalLeftOut));
        await host.BackUpAsync(iphone);
        Assert.Equal([$"Private/{Month}/IMG_0002.HEIC"], host.Files(iphone));
    }

    [Fact]
    public void The_old_include_hidden_switch_becomes_together_and_off_stays_out()
    {
        var folder = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", "ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "photo-backup.db");
        try
        {
            // A version 2 file as v0.4.0 left it: include_hidden on for one phone, off for another, unset for a third.
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE devices (device_id TEXT PRIMARY KEY, destination TEXT NULL, pattern TEXT NULL, convert_heic INTEGER NULL,
                        include_hidden INTEGER NULL, range_kind TEXT NULL, range_days INTEGER NULL, range_since TEXT NULL, on_connect TEXT NULL,
                        save_edited INTEGER NULL);
                    CREATE TABLE assets (device_id TEXT NOT NULL, asset_key TEXT NOT NULL, source_path TEXT NULL, file_name TEXT NOT NULL,
                        size INTEGER NOT NULL, taken_at INTEGER NULL, pc_path TEXT NOT NULL, motion_pc_path TEXT NULL, duplicate INTEGER NOT NULL,
                        job_id TEXT NULL, backed_up_at TEXT NOT NULL, edit_stamp TEXT NULL, edit_pc_path TEXT NULL, PRIMARY KEY (device_id, asset_key));
                    CREATE TABLE runs (job_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, destination TEXT NOT NULL, state TEXT NOT NULL,
                        planned INTEGER NOT NULL DEFAULT 0, copied INTEGER NOT NULL DEFAULT 0, duplicates INTEGER NOT NULL DEFAULT 0,
                        failed INTEGER NOT NULL DEFAULT 0, bytes INTEGER NOT NULL DEFAULT 0, kept_heic INTEGER NOT NULL DEFAULT 0, started_at TEXT NOT NULL,
                        finished_at TEXT NULL, started_by TEXT NULL, edited INTEGER NOT NULL DEFAULT 0, edits_missing INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE pc_hashes (path TEXT PRIMARY KEY COLLATE NOCASE, size INTEGER NOT NULL, modified INTEGER NOT NULL, sha256 BLOB NOT NULL,
                        origin_size INTEGER NULL, origin_sample BLOB NULL);
                    INSERT INTO devices (device_id, pattern, include_hidden) VALUES ('on', 'yyyy', 1), ('off', NULL, 0), ('unset', 'yyyy', NULL);
                    PRAGMA user_version = 2;
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var ledger = new PhotoBackupLedger(path))
            {
                Assert.Equal(new PhotoBackupOwnOptions(Pattern: "yyyy", Hidden: PhotoBackupHidden.Include, HiddenLayout: PhotoBackupHiddenLayout.Together),
                    ledger.GetOptions("on"));
                Assert.Equal(new PhotoBackupOwnOptions(Hidden: PhotoBackupHidden.Exclude), ledger.GetOptions("off"));
                Assert.Equal(new PhotoBackupOwnOptions(Pattern: "yyyy"), ledger.GetOptions("unset"));

                // Written back with the old column kept up to date, for an older copy of the app.
                ledger.SetOptions("unset", new PhotoBackupOwnOptions(Hidden: PhotoBackupHidden.Only, HiddenFolder: "Private"));
            }

            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT include_hidden, hidden, hidden_folder FROM devices WHERE device_id = 'unset'; ";
                using var r = cmd.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal((1L, "Only", "Private"), (r.GetInt64(0), r.GetString(1), r.GetString(2)));
                r.Close();
                cmd.CommandText = "PRAGMA user_version;";
                Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
            }

            // Opening it again changes nothing.
            using (var ledger = new PhotoBackupLedger(path))
            {
                Assert.Equal(PhotoBackupHiddenLayout.Together, ledger.GetOptions("on").HiddenLayout);
                Assert.Equal(PhotoBackupHidden.Only, ledger.GetOptions("unset").Hidden);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task The_old_switch_still_works_through_the_bridge_and_a_job_queued_before_mixes_hidden_ones_in()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct, hidden: true);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var bridge = new BridgeCalls(host.Transport);

        var status = (await bridge.ResultAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, includeHidden = true }))!.AsObject();
        Assert.Equal(("include", "together", "Hidden"),
            ((string?)status["options"]!["hidden"], (string?)status["options"]!["hiddenLayout"], (string?)status["options"]!["hiddenFolder"]));
        Assert.Equal(1, (int)status["count"]!["newHidden"]!);
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_0001.HEIC"], host.Files(phone));

        // A request written before the hidden options existed.
        var legacy = JsonSerializer.Deserialize<PhotoBackupRequest>(
            """{"destination":"D:\\Backup","folderPattern":"yyyy/MM","convertHeic":false,"includeHidden":true}""", DeviceDeckJson.Options)!;
        Assert.Equal((PhotoBackupHidden.Include, (string?)null), (legacy.HiddenMode(), legacy.HiddenRoot()));
        var off = legacy with { IncludeHidden = false };
        Assert.Equal(PhotoBackupHidden.Exclude, off.HiddenMode());
        var now = new PhotoBackupRequest(@"D:\Backup", "yyyy/MM", false, true, Hidden: PhotoBackupHidden.Only, HiddenLayout: PhotoBackupHiddenLayout.Separate,
            HiddenFolder: "پنهان");
        Assert.Equal(@"D:\Backup\پنهان", now.HiddenRoot());
    }

    [Fact]
    public async Task The_bridge_takes_the_options_and_refuses_a_bad_folder_name()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct, hidden: true);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var bridge = new BridgeCalls(host.Transport);

        var status = (await bridge.ResultAsync("photos.backupStatus", new { deviceId = phone.Summary.Id }))!.AsObject();
        Assert.Equal(["destination", "folderPattern", "convertHeic", "range", "saveEdited", "hidden", "hiddenLayout", "hiddenFolder"],
            status["options"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["deviceId", "newCount", "newPhotos", "newVideos", "newBytes", "cloudOnly", "hiddenLeftOut", "outOfRange", "newEdits", "newHidden", "normalLeftOut"],
            status["count"]!.AsObject().Select(p => p.Key));
        Assert.Equal(("exclude", "separate"), ((string?)status["options"]!["hidden"], (string?)status["options"]!["hiddenLayout"]));

        var changed = (await bridge.ResultAsync("photos.backupConfigure",
            new { deviceId = phone.Summary.Id, hidden = "only", hiddenLayout = "separate", hiddenFolder = "Private" }))!.AsObject();
        Assert.Equal(("only", "Private", 1), ((string?)changed["options"]!["hidden"], (string?)changed["options"]!["hiddenFolder"], (int)changed["count"]!["newCount"]!));

        foreach (var bad in new[] { "a\\b", "2026", "CON", "Undated", "x.", "" })
        {
            var error = await bridge.CallAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, hiddenFolder = bad });
            Assert.Equal(-32602, (int)error["error"]!["code"]!);
        }

        var wrong = await bridge.CallAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, hidden = "some" });
        Assert.Equal(-32602, (int)wrong["error"]!["code"]!);
        Assert.Equal("Private", (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Options.HiddenFolder);

        // "Use the Settings defaults" brings the Settings' hidden options back.
        await bridge.ResultAsync("photos.backupConfigure", new { deviceId = phone.Summary.Id, useDefaults = true });
        var reset = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((PhotoBackupHidden.Exclude, "Hidden", false), (reset.Options.Hidden, reset.Options.HiddenFolder, reset.Customized));
    }

    [Theory]
    [InlineData("Hidden")]
    [InlineData("پنهان")]
    [InlineData("عکس‌های پنهان")] // with a zero-width non-joiner, as Persian writes it
    [InlineData("My hidden photos")]
    [InlineData("Hidden 2026")]
    [InlineData(".hidden")]
    public void Folder_names_windows_takes_are_accepted(string name)
    {
        Assert.True(PhotoBackupHiddenRules.IsValidFolder(name));
        Assert.Equal(name, PhotoBackupHiddenRules.ValidateFolder(" " + name + " "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("what?")]
    [InlineData("a*")]
    [InlineData("<x>")]
    [InlineData("a|b")]
    [InlineData("\"q\"")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("Hidden.")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL")]
    [InlineData("com1")]
    [InlineData("LPT9")]
    [InlineData("Undated")]
    [InlineData("2026")]
    [InlineData("2026-10")]
    [InlineData("10.2026")]
    [InlineData("abc\u202Edef")]
    [InlineData("tab\tname")]
    public void Anything_else_is_refused(string name)
    {
        Assert.False(PhotoBackupHiddenRules.IsValidFolder(name));
        Assert.Throws<ArgumentException>(() => PhotoBackupHiddenRules.ValidateFolder(name));
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused_and_settings_check_theirs()
    {
        Assert.False(PhotoBackupHiddenRules.IsValidFolder(new string('a', PhotoBackupHiddenRules.MaxFolderLength + 1)));
        Assert.True(PhotoBackupHiddenRules.IsValidFolder(new string('a', PhotoBackupHiddenRules.MaxFolderLength)));

        var json = JsonSerializer.SerializeToNode(new AppSettings(), DeviceDeckJson.Options)!.AsObject();
        Assert.Equal(["photoBackupHidden", "photoBackupHiddenLayout", "photoBackupHiddenFolder"], json.Select(p => p.Key).TakeLast(3));
        Assert.Equal(("exclude", "separate", "Hidden"),
            ((string?)json["photoBackupHidden"], (string?)json["photoBackupHiddenLayout"], (string?)json["photoBackupHiddenFolder"]));
        Assert.Throws<ArgumentException>(() => new AppSettings { PhotoBackupHiddenFolder = "a/b" }.Validate());
        Assert.Throws<ArgumentException>(() => new AppSettings { PhotoBackupHiddenFolder = " Hidden" }.Validate());
        Assert.Throws<ArgumentException>(() => new AppSettings { PhotoBackupHidden = (PhotoBackupHidden)7 }.Validate());
        new AppSettings { PhotoBackupHiddenFolder = "پنهان", PhotoBackupHidden = PhotoBackupHidden.Only }.Validate();
    }

    private static (int New, int Hidden) Counts(PhotoBackupCount count) => (count.NewCount, count.NewHidden);
}
