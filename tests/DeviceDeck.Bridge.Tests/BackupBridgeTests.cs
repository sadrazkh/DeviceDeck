using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Fake;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Bridge.Tests;

/// <summary>backup.*, contacts.* and messages.* end to end: JSON in, fake phones, files on disk, JSON out.</summary>
public sealed class BackupBridgeTests
{
    private const DeviceCapability BackupCaps = DeviceCapability.Backup | DeviceCapability.Restore | DeviceCapability.Contacts | DeviceCapability.Messages;

    private static string[] Keys(JsonNode? node) => node!.AsObject().Select(p => p.Key).ToArray();

    private static FakeDeviceSpec Iphone() => With(TestPhones.Iphone(), BackupCaps);

    private static FakeDeviceSpec Android()
    {
        var phone = TestPhones.Android();
        var storage = new FakeRoot(new FileRoot("internal", "Internal storage", FileRootKind.InternalStorage), Dir("",
            Dir("DCIM",
                Dir("Camera", File("PXL_0001.jpg", 40_000, new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero)), File("PXL_0002.mp4", 90_000, new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero))),
                Dir(".thumbnails", File("1.jpg", 500, DateTimeOffset.UnixEpoch))),
            Dir("Download", File("ticket.pdf", 12_000, new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero)), File(".pending-1-x.pdf", 10, DateTimeOffset.UnixEpoch)),
            Dir("Android", Dir("data", readOnly: true, denied: true, File("secret.db", 100, DateTimeOffset.UnixEpoch))),
            Dir("Alarms")));
        var spec = With(phone, DeviceCapability.Backup);
        return new FakeDeviceSpec
        {
            Summary = spec.Summary,
            Storage = spec.Storage,
            Battery = spec.Battery,
            Details = spec.Details,
            Assets = spec.Assets,
            Albums = spec.Albums,
            Roots = [.. spec.Roots, storage],
            Apps = [.. spec.Apps, new FakeApp(new AppInfo("org.telegram.messenger", "Telegram", "11.2", 60_000, 1_000, AppSource.Store, false, null), (0, 136, 204))],
            Screen = spec.Screen,
        };
    }

    private static FakeDeviceSpec With(FakeDeviceSpec spec, DeviceCapability add) => new()
    {
        Summary = spec.Summary with { Capabilities = spec.Summary.Capabilities | add },
        Storage = spec.Storage,
        Battery = spec.Battery,
        Details = spec.Details,
        Assets = spec.Assets,
        Albums = spec.Albums,
        Roots = spec.Roots,
        Apps = spec.Apps,
        Screen = spec.Screen,
    };

    private static async Task<JsonObject> FinishedAsync(TestHost host, JsonNode? started, string? state = "done")
    {
        var id = (string)started!["id"]!;
        JsonObject? job = null;
        await Wait.UntilAsync(() =>
        {
            job = host.Backups.Jobs().Where(j => j.Id == id).Select(j => System.Text.Json.JsonSerializer.SerializeToNode(j, Core.Serialization.DeviceDeckJson.Options)!.AsObject()).Single();
            return (string?)job["state"] is "done" or "failed" or "cancelled";
        }, $"job {id}", 20_000);
        if (state is not null)
        {
            Assert.True((string?)job!["state"] == state, job.ToJsonString());
        }

        return job!;
    }

    [Fact]
    public async Task IPhone_backup_lists_a_dated_copy_with_files_contacts_and_messages_in_contract_shapes()
    {
        await using var host = await TestHost.StartAsync(Iphone());

        var device = await host.ResultAsync("backup.device", new { deviceId = TestPhones.IphoneId });
        Assert.Equal(["deviceId", "platform", "folder", "keep", "keepsHistory", "freeBytes", "encrypted", "cloudBackupEnabled", "needsHelperApp", "lastBackup"], Keys(device));
        Assert.Equal(("ios", 3, true, false, false), ((string?)device!["platform"], (int)device["keep"]!, (bool)device["keepsHistory"]!, (bool)device["encrypted"]!, (bool)device["needsHelperApp"]!));
        Assert.Null(device["lastBackup"]);

        var started = await host.ResultAsync("backup.start", new { deviceId = TestPhones.IphoneId });
        Assert.Equal(["id", "deviceId", "deviceName", "kind", "title", "state", "phase", "percent", "bytes", "files", "totalBytes", "totalFiles",
            "bytesPerSecond", "backupId", "destination", "prune", "error", "createdAt", "finishedAt"], Keys(started));
        Assert.Equal(("backup", "Test iPhone"), ((string?)started!["kind"], (string?)started["deviceName"]));
        var job = await FinishedAsync(host, started);
        Assert.Equal(100, (double)job["percent"]!);
        Assert.Contains(host.Transport.EventsNamed("backup.updated"), e => (string?)e["params"]!["phase"] == "transferring");

        var list = (await host.ResultAsync("backup.list", new { deviceId = TestPhones.IphoneId }))!.AsArray();
        var backup = Assert.Single(list)!;
        Assert.Equal(["id", "deviceId", "deviceName", "platform", "model", "marketingName", "osVersion", "createdAt", "sizeBytes", "encrypted", "state",
            "source", "path", "apps", "locked"], Keys(backup));
        Assert.Equal((string?)job["backupId"], (string?)backup["id"]);
        Assert.Equal(("ios", "complete", "deviceDeck", false), ((string?)backup["platform"], (string?)backup["state"], (string?)backup["source"], (bool)backup["locked"]!));
        Assert.StartsWith("dd:test-iphone-", (string?)backup["id"]);
        Assert.True(Directory.Exists((string?)backup["path"]));
        var id = (string)backup["id"]!;

        var domains = (await host.ResultAsync("backup.domains", new { backupId = id }))!.AsArray();
        Assert.Equal(["domain", "kind", "appId", "name", "files", "bytes"], Keys(domains[0]));
        var vlc = domains.Single(d => (string?)d!["domain"] == "AppDomain-org.videolan.vlc-ios")!;
        Assert.Equal(("app", "VLC"), ((string?)vlc["kind"], (string?)vlc["name"]));
        Assert.Contains(domains, d => (string?)d!["domain"] == "CameraRollDomain");

        var files = (await host.ResultAsync("backup.files", new { backupId = id, domain = "AppDomain-org.videolan.vlc-ios", path = "Documents" }))!.AsArray();
        Assert.Equal(["domain", "path", "name", "isDirectory", "size", "modified"], Keys(files[0]));
        Assert.Equal("Documents/Read me.txt", (string?)files[0]!["path"]);

        var contacts = (await host.ResultAsync("contacts.list", new { backupId = id }))!.AsArray();
        Assert.Equal(8, contacts.Count);
        Assert.Equal(["id", "displayName", "firstName", "middleName", "lastName", "prefix", "suffix", "nickname", "organization", "department", "jobTitle",
            "note", "birthday", "phones", "emails", "urls", "addresses", "hasPhoto", "modified"], Keys(contacts[0]));

        var conversations = (await host.ResultAsync("messages.conversations", new { backupId = id }))!.AsArray();
        Assert.Equal(4, conversations.Count);
        Assert.Equal(["id", "title", "members", "isGroup", "service", "messageCount", "attachmentCount", "lastMessageAt", "preview"], Keys(conversations[0]));
        var family = conversations.Single(c => (bool)c!["isGroup"]!)!;
        Assert.Equal("خانواده", (string?)family["title"]);
        var page = (await host.ResultAsync("messages.list", new { backupId = id, conversationId = (string)family["id"]!, offset = 0, limit = 50 }))!;
        Assert.Equal(4, (int)page["total"]!);
        var first = page["items"]!.AsArray()[0]!;
        Assert.Equal(["id", "kind", "date", "fromMe", "handle", "senderName", "text", "service", "attachments"], Keys(first));
        Assert.Equal(("سارا احمدی", "text"), ((string?)first["senderName"], (string?)first["kind"]));
        var withPhoto = page["items"]!.AsArray().Single(m => m!["attachments"]!.AsArray().Count > 0)!["attachments"]![0]!;
        Assert.Equal(["id", "name", "mimeType", "size", "inBackup"], Keys(withPhoto));
        Assert.True((bool)withPhoto["inBackup"]!);

        var bad = await host.CallAsync("messages.list", new { backupId = id, conversationId = "1", offset = 0, limit = 5000 });
        Assert.Equal(-32602, (int)bad["error"]!["code"]!);
    }

    [Fact]
    public async Task Extract_and_exports_write_files_on_the_pc()
    {
        await using var host = await TestHost.StartAsync(Iphone());
        var backup = await FinishedAsync(host, await host.ResultAsync("backup.start", new { deviceId = TestPhones.IphoneId }));
        var id = (string)backup["backupId"]!;
        var pc = host.PcFolder("out");

        var extract = await FinishedAsync(host, await host.ResultAsync("backup.extract", new { backupId = id, items = new[] { new { domain = "HomeDomain", path = "Library/Notes" } }, destination = pc }));
        Assert.Equal("extract", (string?)extract["kind"]);
        Assert.StartsWith("Shopping", System.IO.File.ReadAllText(Path.Combine(pc, "Notes", "notes.txt")));

        var vcard = await FinishedAsync(host, await host.ResultAsync("contacts.export", new { backupId = id, format = "vCard", destination = pc }));
        Assert.Contains("FN:سارا احمدی", System.IO.File.ReadAllText((string)vcard["destination"]!));
        var csv = await FinishedAsync(host, await host.ResultAsync("contacts.export", new { backupId = id, format = "csv", destination = pc, contactIds = new[] { "5" } }));
        Assert.Contains("Anna", System.IO.File.ReadAllText((string)csv["destination"]!));

        var conversation = (string)(await host.ResultAsync("messages.conversations", new { backupId = id }))!.AsArray()
            .First(c => (int)c!["attachmentCount"]! > 0)!["id"]!;
        var html = await FinishedAsync(host, await host.ResultAsync("messages.export", new { backupId = id, conversationIds = new[] { conversation }, format = "html", destination = pc }));
        Assert.Equal(pc, (string?)html["destination"]);
        Assert.Single(Directory.EnumerateFiles(pc, "*.html"));
        Assert.Single(Directory.EnumerateFiles(pc, "*.PNG", SearchOption.AllDirectories));

        // The host's shell can't print to PDF in tests.
        var pdf = await host.CallAsync("messages.export", new { backupId = id, conversationIds = new[] { conversation }, format = "pdf", destination = pc });
        Assert.Equal("not_supported", (string?)pdf["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Pulled_cable_pauses_the_backup_until_the_same_phone_is_back()
    {
        await using var host = await TestHost.StartAsync(null, null, TimeSpan.FromSeconds(2), Iphone());
        var started = await host.ResultAsync("backup.start", new { deviceId = TestPhones.IphoneId });
        var jobId = (string)started!["id"]!;
        await Wait.UntilAsync(() => host.Backups.Jobs().Single(j => j.Id == jobId).Phase == BackupPhase.Transferring, "transferring");

        var busy = await host.CallAsync("backup.start", new { deviceId = TestPhones.IphoneId });
        Assert.Equal("busy", (string?)busy["error"]!["data"]!["code"]);

        await host.ResultAsync("dev.disconnect", new { deviceId = TestPhones.IphoneId });
        await Wait.UntilAsync(() => host.Backups.Jobs().Single(j => j.Id == jobId).State == BackupJobState.WaitingForDevice, "waiting for the phone");
        var waiting = Assert.Single((await host.ResultAsync("backup.list"))!.AsArray(), b => (string?)b!["state"] == "inProgress");
        Assert.Equal("dd:test-iphone", (string?)waiting!["id"]);

        await host.ResultAsync("dev.reconnect", new { deviceId = TestPhones.IphoneId });
        var done = await FinishedAsync(host, started);
        Assert.StartsWith("dd:test-iphone-", (string?)done["backupId"]);
    }

    [Fact]
    public async Task Older_backups_beyond_the_keep_count_are_offered_and_go_only_when_deleted()
    {
        await using var host = await TestHost.StartAsync(Iphone());
        await host.ResultAsync("settings.set", new { backupKeep = 1 });
        var first = await FinishedAsync(host, await host.ResultAsync("backup.start", new { deviceId = TestPhones.IphoneId }));
        Assert.Null(first["prune"]);
        await Task.Delay(1100); // dated copies are named to the second
        var second = await FinishedAsync(host, await host.ResultAsync("backup.start", new { deviceId = TestPhones.IphoneId }));

        var prune = second["prune"]!;
        Assert.Equal(["deviceId", "keep", "backups", "freedBytes"], Keys(prune));
        Assert.Equal((string?)first["backupId"], (string?)prune["backups"]!.AsArray().Single()!["id"]);
        // Unchanged files are shared between the two copies; only what changed is freed.
        var oldFolder = (string)prune["backups"]![0]!["path"]!;
        var oldSize = new DirectoryInfo(oldFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        Assert.InRange((long)prune["freedBytes"]!, 1, oldSize / 2);
        Assert.Equal(2, (await host.ResultAsync("backup.list"))!.AsArray().Count);

        var plan = await host.ResultAsync("backup.prunePlan", new { deviceId = TestPhones.IphoneId });
        Assert.Equal((long)prune["freedBytes"]!, (long)plan!["freedBytes"]!);

        var deleted = await host.ResultAsync("backup.delete", new { backupIds = new[] { (string)first["backupId"]! } });
        Assert.Equal(1, (int)deleted!["deleted"]!);
        Assert.Equal((string?)second["backupId"], (string?)Assert.Single((await host.ResultAsync("backup.list"))!.AsArray())!["id"]);
        await Wait.UntilAsync(() => !Directory.Exists(oldFolder) && !Directory.Exists(oldFolder + ".deleting"), "the old copy gone from disk");

        // Deleting the newest copy also removes the folder the phone updates: both are the same backup.
        var lastPlan = await host.ResultAsync("backup.deletePlan", new { backupIds = new[] { (string)second["backupId"]! } });
        Assert.Single(lastPlan!["backups"]!.AsArray());
        await host.ResultAsync("backup.delete", new { backupIds = new[] { (string)second["backupId"]! } });
        Assert.Empty((await host.ResultAsync("backup.list"))!.AsArray());
    }

    [Fact]
    public async Task Restore_needs_the_phones_name_typed_and_then_restarts_the_phone()
    {
        await using var host = await TestHost.StartAsync(Iphone());
        var backup = await FinishedAsync(host, await host.ResultAsync("backup.start", new { deviceId = TestPhones.IphoneId }));
        var id = (string)backup["backupId"]!;

        var wrong = await host.CallAsync("backup.restore", new { deviceId = TestPhones.IphoneId, backupId = id, confirmName = "Other phone" });
        Assert.Equal(-32602, (int)wrong["error"]!["code"]!);
        Assert.DoesNotContain(host.Backups.Jobs(), j => j.Kind == BackupJobKind.Restore);

        var started = await host.ResultAsync("backup.restore", new { deviceId = TestPhones.IphoneId, backupId = id, confirmName = " test iphone " });
        var restored = await FinishedAsync(host, started);
        Assert.Equal(("restore", id), ((string?)restored["kind"], (string?)restored["backupId"]));
        await Wait.UntilAsync(() => host.Transport.EventsNamed("device.removed").Any(), "the phone restarting");
    }

    [Fact]
    public async Task Backup_encryption_is_turned_on_and_off_through_the_phone()
    {
        await using var host = await TestHost.StartAsync(Iphone());
        var on = await FinishedAsync(host, await host.ResultAsync("backup.setEncryption", new { deviceId = TestPhones.IphoneId, enable = true, password = "pw" }));
        Assert.Equal("encryption", (string?)on["kind"]);
        Assert.True((bool)(await host.ResultAsync("backup.device", new { deviceId = TestPhones.IphoneId }))!["encrypted"]!);

        var wrong = await FinishedAsync(host, await host.ResultAsync("backup.setEncryption", new { deviceId = TestPhones.IphoneId, enable = false, oldPassword = "nope" }), "failed");
        Assert.Equal("wrong_password", (string?)wrong["error"]!["code"]);

        await FinishedAsync(host, await host.ResultAsync("backup.setEncryption", new { deviceId = TestPhones.IphoneId, enable = false, oldPassword = "pw" }));
        Assert.False((bool)(await host.ResultAsync("backup.device", new { deviceId = TestPhones.IphoneId }))!["encrypted"]!);

        var missing = await host.CallAsync("backup.setEncryption", new { deviceId = TestPhones.IphoneId, enable = true });
        Assert.Equal(-32602, (int)missing["error"]!["code"]!);
    }

    [Fact]
    public async Task Android_backup_saves_apks_and_media_and_links_unchanged_files_next_time()
    {
        await using var host = await TestHost.StartAsync(Android());
        var device = await host.ResultAsync("backup.device", new { deviceId = TestPhones.AndroidId });
        Assert.True((bool)device!["needsHelperApp"]!);
        Assert.Null(device["encrypted"]);

        var first = await FinishedAsync(host, await host.ResultAsync("backup.start", new { deviceId = TestPhones.AndroidId }));
        Assert.Equal((3 + 2, 3), ((int)first["totalFiles"]!, (int)first["files"]! - 2));
        var backup = Assert.Single((await host.ResultAsync("backup.list"))!.AsArray())!;
        Assert.Equal(("android", "complete", 2), ((string?)backup["platform"], (string?)backup["state"], (int)backup["apps"]!));
        var folder = (string)backup["path"]!;
        Assert.Equal(40_000, new FileInfo(Path.Combine(folder, "media", "DCIM", "Camera", "PXL_0001.jpg")).Length);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), System.IO.File.GetLastWriteTimeUtc(Path.Combine(folder, "media", "DCIM", "Camera", "PXL_0001.jpg")));
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "media", "Download", "ticket.pdf")));
        Assert.False(Directory.Exists(Path.Combine(folder, "media", "DCIM", ".thumbnails")));
        Assert.False(System.IO.File.Exists(Path.Combine(folder, "media", "Download", ".pending-1-x.pdf")));
        Assert.False(Directory.Exists(Path.Combine(folder, "media", "Android")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(folder, "apps", "org.telegram.messenger"), "*.apk"));
        Assert.True(System.IO.File.Exists(Path.Combine(folder, AndroidBackupManifest.FileName)));

        var domains = (await host.ResultAsync("backup.domains", new { backupId = (string)backup["id"]! }))!.AsArray();
        Assert.Equal(["apps", "media"], domains.Select(d => (string)d!["domain"]!).Order());
        var contacts = await host.CallAsync("contacts.list", new { backupId = (string)backup["id"]! });
        Assert.Equal("not_supported", (string?)contacts["error"]!["data"]!["code"]);
        Assert.Contains("helper app", (string?)contacts["error"]!["message"]);

        await Task.Delay(1100);
        var second = await FinishedAsync(host, await host.ResultAsync("backup.start", new { deviceId = TestPhones.AndroidId }));
        Assert.Equal(2, (await host.ResultAsync("backup.list"))!.AsArray().Count);
        var plan = await host.ResultAsync("backup.deletePlan", new { backupIds = new[] { (string)backup["id"]! } });
        // Every file was the same, so the old backup shares all of them with the new one (only its manifest is its own).
        Assert.InRange((long)plan!["freedBytes"]!, 1, 10_000);
        Assert.NotEqual((string?)backup["id"], (string?)second["backupId"]);
    }

    [Fact]
    public async Task Pulled_cable_during_an_android_backup_continues_in_the_same_folder()
    {
        await using var host = await TestHost.StartAsync(200_000, null, Android());
        var started = await host.ResultAsync("backup.start", new { deviceId = TestPhones.AndroidId });
        var jobId = (string)started!["id"]!;
        await Wait.UntilAsync(() => host.Backups.Jobs().Single(j => j.Id == jobId).Files >= 2, "two files copied");
        await host.ResultAsync("dev.disconnect", new { deviceId = TestPhones.AndroidId });
        await Wait.UntilAsync(() => host.Backups.Jobs().Single(j => j.Id == jobId).State == BackupJobState.WaitingForDevice, "waiting");
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(host.Root, "backups")));
        await host.ResultAsync("dev.reconnect", new { deviceId = TestPhones.AndroidId });
        var done = await FinishedAsync(host, started);
        var folder = (string)Assert.Single((await host.ResultAsync("backup.list"))!.AsArray())!["path"]!;
        Assert.Equal(Path.Combine(host.Root, "backups", ((string)done["backupId"]!)[3..]), folder);
        Assert.Empty(Directory.EnumerateFiles(folder, "*.ddpart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Phones_without_a_backup_service_and_unknown_backups_are_refused()
    {
        await using var host = await TestHost.StartAsync(TestPhones.Iphone(), TestPhones.Android());
        var start = await host.CallAsync("backup.start", new { deviceId = TestPhones.IphoneId });
        Assert.Equal("not_supported", (string?)start["error"]!["data"]!["code"]);
        var unknown = await host.CallAsync("backup.domains", new { backupId = "dd:nothing-here" });
        Assert.Equal("path_not_found", (string?)unknown["error"]!["data"]!["code"]);
        var escape = await host.CallAsync("backup.files", new { backupId = "dd:..", domain = "HomeDomain" });
        Assert.Equal("path_not_found", (string?)escape["error"]!["data"]!["code"]);
        Assert.Empty((await host.ResultAsync("backup.jobs"))!.AsArray());
    }
}
