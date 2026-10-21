using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Transfers;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Bridge.Tests;

/// <summary>whatsapp.* end to end: a fake iPhone's backup with WhatsApp in it, and a fake Android phone's WhatsApp folders.</summary>
public sealed class WhatsAppBridgeTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 20, 21, 0, 0, TimeSpan.FromHours(3.5));

    private static string[] Keys(JsonNode? node) => node!.AsObject().Select(p => p.Key).ToArray();

    private static FakeDeviceSpec Iphone(bool whatsApp = true)
    {
        var spec = TestPhones.Iphone();
        return new FakeDeviceSpec
        {
            Summary = spec.Summary with { Capabilities = spec.Summary.Capabilities | DeviceCapability.Backup | DeviceCapability.Contacts | DeviceCapability.Messages },
            Storage = spec.Storage,
            Battery = spec.Battery,
            Details = spec.Details,
            Assets = spec.Assets,
            Albums = spec.Albums,
            Roots = spec.Roots,
            Apps = whatsApp
                ? [.. spec.Apps, new FakeApp(new AppInfo("net.whatsapp.WhatsApp", "WhatsApp", "26.20", 100, 200, AppSource.Store, false, null), (37, 211, 102))]
                : spec.Apps,
            Screen = spec.Screen,
        };
    }

    private static FakeDeviceSpec Android(bool whatsApp = true, bool business = true)
    {
        var spec = TestPhones.Android();
        var app = (string package, string name) => Dir(package, Dir(name,
            Dir("Media",
                Dir($"{name} Images", File("IMG-20261020-WA0001.jpg", 120_000, Day), File("IMG-20261020-WA0002.jpg", 90_000, Day)),
                Dir($"{name} Voice Notes", Dir("202642", File("PTT-20261020-WA0003.opus", 9_000, Day))),
                Dir(".Statuses", File("status.jpg", 1_000, Day))),
            Dir("Databases", File("msgstore.db.crypt14", 4_000_000, Day), File("msgstore-2026-10-19.1.db.crypt14", 3_900_000, Day.AddDays(-1))),
            Dir("Backups", File("backup_settings.json.crypt1", 300, Day))));
        var media = new List<FakeFileNode> { Dir("org.telegram.messenger") };
        if (whatsApp)
            media.Add(app("com.whatsapp", "WhatsApp"));
        if (whatsApp && business)
            media.Add(app("com.whatsapp.w4b", "WhatsApp Business"));
        var storage = new FakeRoot(new FileRoot("internal", "Internal storage", FileRootKind.InternalStorage), Dir("",
            Dir("Android", Dir("media", [.. media]), Dir("data", readOnly: true, denied: true)),
            Dir("Download")));
        return new FakeDeviceSpec
        {
            Summary = spec.Summary,
            Storage = spec.Storage,
            Battery = spec.Battery,
            Details = spec.Details,
            Assets = spec.Assets,
            Albums = spec.Albums,
            Roots = [.. spec.Roots, storage],
            Apps = whatsApp ? [.. spec.Apps, new FakeApp(new AppInfo("com.whatsapp", "WhatsApp", "2.26", 100, 200, AppSource.Store, false, null), (37, 211, 102))] : spec.Apps,
            Screen = spec.Screen,
        };
    }

    private static async Task<JsonObject> FinishedAsync(TestHost host, JsonNode? started, string state = "done")
    {
        var id = (string)started!["id"]!;
        JsonObject? job = null;
        await Wait.UntilAsync(() =>
        {
            job = host.Backups.Jobs().Where(j => j.Id == id)
                .Select(j => System.Text.Json.JsonSerializer.SerializeToNode(j, Core.Serialization.DeviceDeckJson.Options)!.AsObject()).Single();
            return (string?)job["state"] is "done" or "failed" or "cancelled";
        }, $"job {id}", 40_000);
        Assert.True((string?)job!["state"] == state, job.ToJsonString());
        return job;
    }

    private static async Task<string> BackUpAsync(TestHost host)
    {
        var job = await FinishedAsync(host, await host.ResultAsync("backup.start", new { deviceId = TestPhones.IphoneId }));
        return (string)job["backupId"]!;
    }

    [Fact]
    public async Task A_backup_reads_as_chats_and_messages_in_contract_shapes()
    {
        await using var host = await TestHost.StartAsync(Iphone());
        var id = await BackUpAsync(host);

        var status = (await host.ResultAsync("whatsapp.status", new { backupId = id }))!;
        Assert.Equal(["backupId", "found", "installed", "accounts", "missing"], Keys(status));
        Assert.Equal((true, true), ((bool)status["found"]!, (bool)status["installed"]!));
        var account = status["accounts"]!.AsArray().Single()!;
        Assert.Equal(["account", "name", "chats", "messages", "media", "mediaInBackup", "mediaBytes"], Keys(account));
        Assert.Equal(("whatsApp", 5), ((string?)account["account"], (int)account["chats"]!));
        Assert.Equal((int)account["media"]! - 1, (int)account["mediaInBackup"]!); // the spreadsheet was never downloaded

        var chats = (await host.ResultAsync("whatsapp.chats", new { backupId = id }))!.AsArray();
        Assert.Equal(["id", "account", "title", "jid", "phone", "isGroup", "messageCount", "mediaCount", "lastMessageAt", "preview", "previewMedia", "archived"], Keys(chats[0]));
        // Named through the backup's address book (Anna, John, Sara), newest first; John's chat is archived.
        Assert.Equal(["DeviceDeck testers", "Anna Schmidt", "خانواده 🏡", "سارا احمدی", "John Appleseed"], chats.Select(c => (string)c!["title"]!));
        var sara = chats.Single(c => (string?)c!["title"] == "سارا احمدی")!;
        Assert.Equal(("+989123456789", false), ((string?)sara["phone"], (bool)sara["isGroup"]!));

        var page = (await host.ResultAsync("whatsapp.messages", new { backupId = id, chatId = (string)sara["id"]!, offset = 0, limit = 100 }))!;
        Assert.Equal(["items", "total"], Keys(page));
        var items = page["items"]!.AsArray();
        Assert.Equal(["id", "kind", "date", "fromMe", "senderJid", "senderName", "text", "starred", "quote", "media"], Keys(items[0]));
        var photo = items.First(m => (string?)m!["media"]?["kind"] == "image")!["media"]!;
        Assert.Equal(["id", "kind", "name", "mimeType", "size", "durationSeconds", "inBackup", "hasThumbnail", "latitude", "longitude"], Keys(photo));
        var reply = items.Single(m => m!["quote"] is not null)!["quote"]!;
        Assert.Equal(["messageId", "fromMe", "senderName", "text", "mediaKind"], Keys(reply));
        Assert.True((bool)reply["fromMe"]!);
        Assert.Contains(items, m => (string?)m!["media"]?["kind"] == "location");

        var thumb = (await host.ResultAsync("whatsapp.thumbnail", new { backupId = id, mediaId = (string)photo["id"]! }))!;
        Assert.StartsWith("data:image/png;base64,", (string?)thumb["dataUrl"]);
        var voice = items.Single(m => (string?)m!["media"]?["kind"] == "voice")!["media"]!;
        var nothing = (await host.ResultAsync("whatsapp.thumbnail", new { backupId = id, mediaId = (string)voice["id"]! }))!;
        Assert.Null((string?)nothing["dataUrl"]);
        var audio = (await host.ResultAsync("whatsapp.media", new { backupId = id, mediaId = (string)voice["id"]! }))!;
        Assert.Equal(["dataUrl", "mimeType", "size"], Keys(audio));
        Assert.StartsWith("data:audio/ogg;base64,T2dnUw", (string?)audio["dataUrl"]);

        var bad = await host.CallAsync("whatsapp.messages", new { backupId = id, chatId = (string)sara["id"]!, offset = 0, limit = 5000 });
        Assert.Equal(-32602, (int)bad["error"]!["code"]!);
        var unknown = await host.CallAsync("whatsapp.messages", new { backupId = id, chatId = "w:999", offset = 0, limit = 5 });
        Assert.Equal("path_not_found", (string?)unknown["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Chats_and_media_export_to_the_pc()
    {
        await using var host = await TestHost.StartAsync(Iphone());
        var id = await BackUpAsync(host);
        var chats = (await host.ResultAsync("whatsapp.chats", new { backupId = id }))!.AsArray();
        var family = (string)chats.Single(c => (string?)c!["title"] == "خانواده 🏡")!["id"]!;
        var pc = host.PcFolder("whatsapp");

        var one = await FinishedAsync(host, await host.ResultAsync("whatsapp.export", new { backupId = id, chatIds = new[] { family }, format = "html", destination = pc }));
        Assert.Equal(("export", Path.Combine(pc, "خانواده 🏡.html")), ((string?)one["kind"], (string?)one["destination"]));
        var html = System.IO.File.ReadAllText((string)one["destination"]!);
        Assert.Contains("Neda ✨", html); // a member who isn't in the address book: her own name
        Assert.Contains("This message was deleted", html);
        Assert.True(System.IO.File.Exists(Path.Combine(pc, "خانواده 🏡 files", "2b3c-cake.jpg")));

        var all = await FinishedAsync(host, await host.ResultAsync("whatsapp.export", new { backupId = id, format = "html", destination = pc }));
        Assert.Equal(5, Directory.GetFiles((string)all["destination"]!, "*.html").Length);

        var media = await FinishedAsync(host, await host.ResultAsync("whatsapp.exportMedia", new { backupId = id, destination = pc }));
        var root = (string)media["destination"]!;
        Assert.StartsWith(Path.Combine(pc, "WhatsApp media - Test iPhone - "), root);
        Assert.Equal(["Anna Schmidt", "خانواده 🏡", "سارا احمدی"], Directory.GetDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(root, "سارا احمدی")).Length); // three photos and a voice note

        var pdf = await host.CallAsync("whatsapp.export", new { backupId = id, format = "pdf", destination = "relative" });
        Assert.True(pdf.ContainsKey("error"));
    }

    [Fact]
    public async Task A_backup_of_a_phone_without_whatsapp_says_so()
    {
        await using var host = await TestHost.StartAsync(Iphone(whatsApp: false));
        var id = await BackUpAsync(host);

        var status = (await host.ResultAsync("whatsapp.status", new { backupId = id }))!;

        Assert.Equal((false, false), ((bool)status["found"]!, (bool)status["installed"]!));
        Assert.Empty((await host.ResultAsync("whatsapp.chats", new { backupId = id }))!.AsArray());
    }

    [Fact]
    public async Task Android_lists_whatsapps_folders_and_copies_them_into_a_dated_folder()
    {
        await using var host = await TestHost.StartAsync(Android());

        var info = (await host.ResultAsync("whatsapp.android", new { deviceId = TestPhones.AndroidId }))!;
        Assert.Equal(["deviceId", "apps"], Keys(info));
        var apps = info["apps"]!.AsArray();
        Assert.Equal(["package", "account", "name", "folder", "hasFolder", "installed", "mediaFiles", "mediaBytes", "chatBackups"], Keys(apps[0]));
        Assert.Equal(["com.whatsapp", "com.whatsapp.w4b"], apps.Select(a => (string)a!["package"]!));
        var whatsApp = apps[0]!;
        Assert.Equal(("/Android/media/com.whatsapp/WhatsApp", true, true), ((string?)whatsApp["folder"], (bool)whatsApp["hasFolder"]!, (bool?)whatsApp["installed"]));
        Assert.Equal(["msgstore.db.crypt14", "msgstore-2026-10-19.1.db.crypt14"], whatsApp["chatBackups"]!.AsArray().Select(b => (string)b!["name"]!));
        Assert.Equal(false, (bool?)apps[1]!["installed"]); // the folder is there, the app is gone

        var pc = host.PcFolder("android");
        var started = (await host.ResultAsync("whatsapp.backupAndroid", new { deviceId = TestPhones.AndroidId, destination = pc }))!;
        Assert.Equal(["jobId", "folder"], Keys(started));
        var folder = (string)started["folder"]!;
        Assert.StartsWith(Path.Combine(pc, "WhatsApp - Test Android - "), folder);
        await Wait.ForStateAsync(host.Queue, (string)started["jobId"]!, TransferState.Done);
        var job = host.Queue.Get((string)started["jobId"]!)!;
        Assert.Equal((TransferKind.WhatsAppMedia, 2 * 7), (job.Kind, job.TotalItems));
        Assert.StartsWith("Backing up WhatsApp media from Test Android to ", job.Title);
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "WhatsApp", "Media", "WhatsApp Voice Notes", "202642", "PTT-20261020-WA0003.opus")));
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "WhatsApp", "Databases", "msgstore.db.crypt14")));
        Assert.True(System.IO.File.Exists(Path.Combine(folder, "WhatsApp Business", "Backups", "backup_settings.json.crypt1")));
        Assert.Equal(4_000_000, new FileInfo(Path.Combine(folder, "WhatsApp", "Databases", "msgstore.db.crypt14")).Length);

        // A second copy the same day goes next to the first.
        var again = (await host.ResultAsync("whatsapp.backupAndroid", new { deviceId = TestPhones.AndroidId, destination = pc }))!;
        Assert.EndsWith(" (1)", (string)again["folder"]!);
        await Wait.ForStateAsync(host.Queue, (string)again["jobId"]!, TransferState.Done);
    }

    [Fact]
    public async Task Android_without_whatsapp_and_an_iphone_are_told_why()
    {
        await using var host = await TestHost.StartAsync(Android(whatsApp: false), TestPhones.Iphone());

        var info = (await host.ResultAsync("whatsapp.android", new { deviceId = TestPhones.AndroidId }))!;
        Assert.Empty(info["apps"]!.AsArray());
        var started = (await host.ResultAsync("whatsapp.backupAndroid", new { deviceId = TestPhones.AndroidId, destination = host.PcFolder("none") }))!;
        await Wait.ForStateAsync(host.Queue, (string)started["jobId"]!, TransferState.Failed);
        Assert.Equal(DeviceErrorCodes.PathNotFound, host.Queue.Get((string)started["jobId"]!)!.Error!.Code);

        var iphone = await host.CallAsync("whatsapp.android", new { deviceId = TestPhones.IphoneId });
        Assert.Equal("not_supported", (string?)iphone["error"]!["data"]!["code"]);
        var relative = await host.CallAsync("whatsapp.backupAndroid", new { deviceId = TestPhones.AndroidId, destination = "relative" });
        Assert.Equal(-32602, (int)relative["error"]!["code"]!);
    }
}
