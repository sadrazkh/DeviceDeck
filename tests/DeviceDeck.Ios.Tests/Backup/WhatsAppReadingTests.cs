using System.Buffers.Binary;
using System.Text.Json.Nodes;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.WhatsApp;
using DeviceDeck.Ios.Backup.Format;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests.Backup;

/// <summary>WhatsApp from backups written the way an iPhone writes them (WhatsAppSamples): chats, messages, media, exports.</summary>
public sealed class WhatsAppReadingTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Timeout();
    private const string Udid = BackupFixtureBuilder.Udid;
    private const string Id = "dd:" + Udid;

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-whatsapp-").FullName;
    private readonly DeviceHub _hub = new([]);
    private BackupService _service = null!;

    private string Root => Path.Combine(_dir, "Backups");

    public Task InitializeAsync()
    {
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        settings.Update(new JsonObject { ["backupFolder"] = Root });
        _service = new BackupService(_hub, settings, new IosBackupFormat(), new BackupServiceOptions { AppleBackupFolders = [Path.Combine(_dir, "Apple")] });
        _service.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        await _hub.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Build(Action<BackupFixtureBuilder>? more = null, string? password = null)
    {
        var builder = new BackupFixtureBuilder { Password = password }.AddStandardContent();
        more?.Invoke(builder);
        builder.Build(Path.Combine(Root, Udid));
    }

    private async Task<BackupJob> WaitForAsync(BackupJob started)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            var job = _service.Jobs().Single(j => j.Id == started.Id);
            if (job.State is BackupJobState.Done or BackupJobState.Failed or BackupJobState.Cancelled)
                return job;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Job {job.Title} is still {job.State}.");
            await Task.Delay(20);
        }
    }

    private static DateTimeOffset At(DateTime utc) => new DateTimeOffset(utc).AddSeconds(0.25);

    [Fact]
    public async Task Status_counts_both_apps_and_lists_nothing_missing()
    {
        Build(b => b.AddWhatsAppBusiness());

        var status = await _service.GetWhatsAppStatusAsync(Id, Ct);

        Assert.True(status.Found);
        Assert.True(status.Installed);
        Assert.Empty(status.Missing);
        var whatsApp = status.Accounts.Single(a => a.Account == WhatsAppAccount.WhatsApp);
        // Sara 10, family 5, Tom 1, John 1; the empty chat and status@broadcast are left out.
        Assert.Equal(("WhatsApp", 4, 17), (whatsApp.Name, whatsApp.Chats, whatsApp.Messages));
        // Photo, voice note, document, sticker, video have files; the document isn't in the backup.
        Assert.Equal((5, 4), (whatsApp.Media, whatsApp.MediaInBackup));
        Assert.Equal(WhatsAppSamples.Photo.Length + WhatsAppSamples.Opus.Length + WhatsAppSamples.Webp.Length + WhatsAppSamples.Mp4.Length, whatsApp.MediaBytes);
        var business = status.Accounts.Single(a => a.Account == WhatsAppAccount.Business);
        Assert.Equal(("WhatsApp Business", 1, 2, 0), (business.Name, business.Chats, business.Messages, business.Media));
    }

    [Fact]
    public async Task Chats_are_named_like_the_phone_names_them_newest_first()
    {
        Build(b => b.AddWhatsAppBusiness());

        var chats = await _service.GetWhatsAppChatsAsync(Id, Ct);

        Assert.Equal(["Customer", "Thomas Green", "خانواده", "سارا احمدی", "John Appleseed"], chats.Select(c => c.Title));
        Assert.Equal(["b:1", "w:3", "w:2", "w:1", "w:5"], chats.Select(c => c.Id));
        var group = chats.Single(c => c.Id == "w:2");
        Assert.Equal((true, (string?)null, 5, 2), (group.IsGroup, group.Phone, group.MessageCount, group.MediaCount));
        Assert.Equal(WhatsAppMediaKind.Video, group.PreviewMedia);
        Assert.Null(group.Preview);
        var sara = chats.Single(c => c.Id == "w:1");
        Assert.Equal(("+989123456789", false, 10, 3, "Don't forget"), (sara.Phone, sara.IsGroup, sara.MessageCount, sara.MediaCount, sara.Preview));
        Assert.Equal(At(WhatsAppSamples.T0.AddDays(1).AddMinutes(3)), sara.LastMessageAt);
        Assert.True(chats.Single(c => c.Title == "John Appleseed").Archived);
        Assert.Equal(WhatsAppAccount.Business, chats[0].Account);
    }

    [Fact]
    public async Task A_chat_reads_with_media_replies_and_what_it_cant_show()
    {
        Build();

        var page = await _service.GetWhatsAppMessagesAsync(Id, "w:1", 0, 50, Ct);

        Assert.Equal(10, page.Total);
        var m = page.Items;
        Assert.Equal([WhatsAppMessageKind.Text, WhatsAppMessageKind.Text, WhatsAppMessageKind.Media, WhatsAppMessageKind.Media, WhatsAppMessageKind.Text,
            WhatsAppMessageKind.Media, WhatsAppMessageKind.Media, WhatsAppMessageKind.Deleted, WhatsAppMessageKind.Other, WhatsAppMessageKind.Text], m.Select(x => x.Kind));
        Assert.Equal(("سلام! خوبی؟", false, At(WhatsAppSamples.T0)), (m[0].Text, m[0].FromMe, m[0].Date));
        Assert.Null(m[0].SenderName); // one-to-one: no sender line

        var photo = m[2].Media!;
        Assert.Equal((WhatsAppMediaKind.Image, "4a1b-photo.jpg", "image/jpeg", true, true), (photo.Kind, photo.Name, photo.MimeType, photo.InBackup, photo.HasThumbnail));
        Assert.Equal("Sunset", m[2].Text); // the caption, kept in the media item's title

        var voice = m[3].Media!;
        Assert.Equal((WhatsAppMediaKind.Voice, 7.0, true, false), (voice.Kind, voice.DurationSeconds, voice.InBackup, voice.HasThumbnail));
        Assert.True(m[3].FromMe);

        Assert.Equal("Lovely!", m[4].Text);
        Assert.Null(m[4].Media);
        Assert.Equal(new WhatsAppQuote("w:2", true, null, "Hi Sara 👋", null), m[4].Quote);

        var document = m[5].Media!;
        Assert.Equal((WhatsAppMediaKind.Document, "Invoice 0921.pdf", "application/pdf", 210_000L, false), (document.Kind, document.Name, document.MimeType, document.Size, document.InBackup));

        var place = m[6].Media!;
        Assert.Equal((WhatsAppMediaKind.Location, "Tehran", 35.6892, 51.389), (place.Kind, place.Name, place.Latitude, place.Longitude));

        Assert.Null(m[7].Text);
        Assert.Null(m[8].Text);
        Assert.True(m[9].Starred);

        var second = await _service.GetWhatsAppMessagesAsync(Id, "w:1", 4, 2, Ct);
        Assert.Equal(["w:5", "w:6"], second.Items.Select(x => x.Id));
        Assert.Equal("w:2", second.Items[0].Quote!.MessageId);
    }

    [Fact]
    public async Task Groups_name_each_sender_and_show_events()
    {
        Build();

        var page = await _service.GetWhatsAppMessagesAsync(Id, "w:2", 0, 50, Ct);

        var m = page.Items;
        Assert.Equal((WhatsAppMessageKind.Event, "خانواده"), (m[0].Kind, m[0].Text));
        Assert.Equal(("سارا احمدی", WhatsAppSamples.SaraJid), (m[1].SenderName, m[1].SenderJid)); // the iPhone's address book
        Assert.Equal("Mina", m[2].SenderName); // the name she gave herself
        Assert.Null(m[3].SenderName);
        Assert.Equal(WhatsAppMediaKind.Sticker, m[3].Media!.Kind);
        Assert.Equal((WhatsAppMediaKind.Video, 12.0, true), (m[4].Media!.Kind, m[4].Media!.DurationSeconds, m[4].Media!.HasThumbnail));
    }

    [Fact]
    public async Task Thumbnails_and_voice_notes_come_out_of_the_backup()
    {
        Build();
        var messages = (await _service.GetWhatsAppMessagesAsync(Id, "w:1", 0, 50, Ct)).Items;
        var group = (await _service.GetWhatsAppMessagesAsync(Id, "w:2", 0, 50, Ct)).Items;

        Assert.Equal(WhatsAppSamples.Thumb, await _service.GetWhatsAppThumbnailAsync(Id, messages[2].Media!.Id, 256, Ct));
        Assert.Equal(WhatsAppSamples.Thumb, await _service.GetWhatsAppThumbnailAsync(Id, group[4].Media!.Id, 256, Ct)); // clip.thumb next to clip.mp4
        Assert.Equal(WhatsAppSamples.Webp, await _service.GetWhatsAppThumbnailAsync(Id, group[3].Media!.Id, 256, Ct));
        Assert.Null(await _service.GetWhatsAppThumbnailAsync(Id, messages[3].Media!.Id, 256, Ct));
        Assert.Null(await _service.GetWhatsAppThumbnailAsync(Id, "w:999", 256, Ct));

        var voice = await _service.GetWhatsAppMediaAsync(Id, messages[3].Media!.Id, Ct);
        Assert.Equal("audio/ogg", voice.MimeType);
        Assert.Equal(WhatsAppSamples.Opus, voice.Bytes);
        var offloaded = await Assert.ThrowsAsync<DeviceException>(() => _service.GetWhatsAppMediaAsync(Id, messages[5].Media!.Id, Ct));
        Assert.Equal(BackupErrors.Missing, offloaded.Code);
        var unknown = await Assert.ThrowsAsync<DeviceException>(() => _service.GetWhatsAppMessagesAsync(Id, "x:1", 0, 5, Ct));
        Assert.Equal(DeviceErrorCodes.PathNotFound, unknown.Code);
    }

    [Fact]
    public async Task One_chat_exports_as_html_with_its_files_next_to_it()
    {
        Build();
        var target = Path.Combine(_dir, "export");

        var job = await WaitForAsync(_service.StartWhatsAppExport(Id, ["w:1"], MessagesFormat.Html, target, null));

        Assert.Equal(BackupJobState.Done, job.State);
        Assert.Equal("Saving a WhatsApp chat from the backup of Nima’s iPhone", job.Title);
        Assert.Equal(Path.Combine(target, "سارا احمدی.html"), job.Destination);
        var html = File.ReadAllText(job.Destination!);
        Assert.Contains("<h1 dir=\"auto\">سارا احمدی</h1>", html);
        Assert.Contains("<p class=\"meta\" dir=\"ltr\">+989123456789</p>", html);
        Assert.Contains("10 messages · WhatsApp", html);
        Assert.Contains("<img class=\"photo\" src=\"%D8%B3%D8%A7%D8%B1%D8%A7%20%D8%A7%D8%AD%D9%85%D8%AF%DB%8C%20files/4a1b-photo.jpg\"", html);
        Assert.Contains("<audio controls preload=\"none\" src=\"%D8%B3%D8%A7%D8%B1%D8%A7%20%D8%A7%D8%AD%D9%85%D8%AF%DB%8C%20files/7c2d-voice.opus\">", html);
        Assert.Contains("<div class=\"quote\"><div class=\"quote-from\" dir=\"auto\">You</div><div dir=\"auto\">Hi Sara 👋</div></div>", html);
        Assert.Contains("Invoice 0921.pdf · 205.1 KB — not in this backup", html);
        Assert.Contains("📍 Tehran (35.6892,51.389)", html);
        Assert.Contains("This message was deleted", html);
        Assert.Contains("★ ", html);
        Assert.Equal(WhatsAppSamples.Photo, File.ReadAllBytes(Path.Combine(target, "سارا احمدی files", "4a1b-photo.jpg")));
        Assert.Equal(WhatsAppSamples.Opus, File.ReadAllBytes(Path.Combine(target, "سارا احمدی files", "7c2d-voice.opus")));
        Assert.Equal(At(WhatsAppSamples.T0.AddMinutes(2)).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(target, "سارا احمدی files", "4a1b-photo.jpg")));
        Assert.Empty(Directory.EnumerateFiles(target, "*.ddpart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task All_chats_export_into_a_folder_of_their_own_and_print_to_pdf()
    {
        Build(b => b.AddWhatsAppBusiness());
        var target = Path.Combine(_dir, "all");
        var printer = new RecordingPrinter();

        var job = await WaitForAsync(_service.StartWhatsAppExport(Id, [], MessagesFormat.Pdf, target, printer));

        Assert.Equal(BackupJobState.Done, job.State);
        Assert.Equal("Saving all WhatsApp chats from the backup of Nima’s iPhone", job.Title);
        var folder = Path.Combine(target, "WhatsApp - Nima’s iPhone - " + new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero).ToLocalTime().ToString("yyyy-MM-dd"));
        Assert.Equal(folder, job.Destination);
        Assert.Equal(["Customer.pdf", "John Appleseed.pdf", "Thomas Green.pdf", "خانواده.pdf", "سارا احمدی.pdf"],
            Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetFiles(folder, "*.html"));
        Assert.Contains(printer.Html, h => h.Contains("Dinner at 8?") && h.Contains("<div class=\"from\" dir=\"auto\">Mina</div>"));
        Assert.True(File.Exists(Path.Combine(folder, "خانواده files", "1e3f-sticker.webp")));

        var again = await WaitForAsync(_service.StartWhatsAppExport(Id, ["w:3", "nope"], MessagesFormat.Html, target, null));
        Assert.Equal(Path.Combine(target, "Thomas Green.html"), again.Destination);
        var none = await WaitForAsync(_service.StartWhatsAppExport(Id, ["nope"], MessagesFormat.Html, target, null));
        Assert.Equal((BackupJobState.Failed, DeviceErrorCodes.PathNotFound), (none.State, none.Error!.Code));
        var pdf = Assert.Throws<DeviceException>(() => _service.StartWhatsAppExport(Id, ["w:1"], MessagesFormat.Pdf, target, null));
        Assert.Equal(DeviceErrorCodes.NotSupported, pdf.Code);
    }

    [Fact]
    public async Task All_media_exports_one_folder_per_chat()
    {
        Build();
        var target = Path.Combine(_dir, "media");

        var job = await WaitForAsync(_service.StartWhatsAppMediaExport(Id, [], target));

        Assert.Equal(BackupJobState.Done, job.State);
        Assert.Equal((4, 4), (job.Files, job.TotalFiles));
        var root = job.Destination!;
        Assert.StartsWith(Path.Combine(target, "WhatsApp media - Nima’s iPhone - "), root);
        Assert.Equal(["4a1b-photo.jpg", "7c2d-voice.opus"], Directory.GetFiles(Path.Combine(root, "سارا احمدی")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["1e3f-sticker.webp", "2f40-clip.mp4"], Directory.GetFiles(Path.Combine(root, "خانواده")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(At(WhatsAppSamples.T0.AddDays(1).AddHours(6)).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(root, "خانواده", "2f40-clip.mp4")));

        var chosen = await WaitForAsync(_service.StartWhatsAppMediaExport(Id, ["w:3"], target));
        Assert.Equal((BackupJobState.Failed, DeviceErrorCodes.PathNotFound), (chosen.State, chosen.Error!.Code)); // Tom sent no media
    }

    [Fact]
    public async Task An_encrypted_backup_reads_once_unlocked()
    {
        Build(password: "s3cret pass");

        var locked = await Assert.ThrowsAsync<DeviceException>(() => _service.GetWhatsAppChatsAsync(Id, Ct));
        Assert.Equal(BackupErrors.PasswordRequired, locked.Code);

        await _service.UnlockAsync(Id, "s3cret pass", Ct);
        // The databases are in WAL mode, as on the phone, and decrypted into memory only.
        Assert.Equal(4, (await _service.GetWhatsAppChatsAsync(Id, Ct)).Count);
        Assert.Equal(4, (await _service.GetContactsAsync(Id, Ct)).Count);
        var messages = (await _service.GetWhatsAppMessagesAsync(Id, "w:1", 0, 50, Ct)).Items;
        Assert.Equal(WhatsAppSamples.Opus, (await _service.GetWhatsAppMediaAsync(Id, messages[3].Media!.Id, Ct)).Bytes);
        Assert.Equal("Thomas Green", (await _service.GetWhatsAppChatsAsync(Id, Ct)).Single(c => c.Id == "w:3").Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Messages_still_in_the_write_ahead_log_are_read(bool encrypted)
    {
        var (database, wal) = WhatsAppSamples.ChatStorageWithLog();
        var builder = new BackupFixtureBuilder { Password = encrypted ? "pw" : null }
            .File(WhatsAppSamples.Domain, "ChatStorage.sqlite", database)
            .File(WhatsAppSamples.Domain, "ChatStorage.sqlite-wal", wal);
        builder.Build(Path.Combine(Root, Udid));
        if (encrypted)
            await _service.UnlockAsync(Id, "pw", Ct);

        var tom = (await _service.GetWhatsAppChatsAsync(Id, Ct)).Single(c => c.Id == "w:3");

        Assert.Equal((2, "Only in the log"), (tom.MessageCount, tom.Preview));
        Assert.Equal("Tom", tom.Title); // no address book in this backup: the name he gave himself
    }

    [Fact]
    public async Task Another_version_without_some_columns_shows_less_and_says_what_is_missing()
    {
        new BackupFixtureBuilder()
            .File(WhatsAppSamples.Domain, "ChatStorage.sqlite", WhatsAppSamples.OldChatStorage())
            .File(WhatsAppSamples.Domain, "Message/Media/989123456789@s.whatsapp.net/4/a/4a1b-photo.jpg", WhatsAppSamples.Photo)
            .Build(Path.Combine(Root, Udid));

        var status = await _service.GetWhatsAppStatusAsync(Id, Ct);
        var chat = Assert.Single(await _service.GetWhatsAppChatsAsync(Id, Ct));
        var messages = (await _service.GetWhatsAppMessagesAsync(Id, chat.Id, 0, 10, Ct)).Items;

        Assert.True(status.Found);
        Assert.Contains("ZWAGROUPMEMBER", status.Missing);
        Assert.Contains("ZWAPROFILEPUSHNAME", status.Missing);
        Assert.Contains("ZWAMESSAGE.ZSTARRED", status.Missing);
        Assert.Contains("ZWAMEDIAITEM.ZMETADATA", status.Missing);
        Assert.DoesNotContain("ZWAMESSAGE.ZMEDIAITEM", status.Missing); // media found through ZWAMEDIAITEM.ZMESSAGE instead
        Assert.Equal(("Sara", 2, 1), (chat.Title, chat.MessageCount, chat.MediaCount));
        Assert.Equal("Old version hello", messages[0].Text);
        Assert.Equal((WhatsAppMediaKind.Image, true), (messages[1].Media!.Kind, messages[1].Media!.InBackup));
    }

    [Fact]
    public async Task A_backup_without_whatsapp_says_so()
    {
        new BackupFixtureBuilder().Text("AppDomain-net.whatsapp.WhatsApp", "Documents/x.txt", "x").Build(Path.Combine(Root, Udid));

        var status = await _service.GetWhatsAppStatusAsync(Id, Ct);

        Assert.Equal((false, true), (status.Found, status.Installed));
        Assert.Empty(status.Accounts);
        Assert.Empty(await _service.GetWhatsAppChatsAsync(Id, Ct));
    }

    [Fact]
    public async Task A_layout_without_messages_is_reported_as_unreadable()
    {
        var empty = Path.Combine(_dir, "empty.sqlite");
        using (var db = new SqliteConnection($"Data Source={empty};Pooling=False"))
        {
            db.Open();
            BackupFixtureBuilder.Exec(db, "CREATE TABLE ZWACHATSESSION (Z_PK INTEGER PRIMARY KEY)");
        }
        new BackupFixtureBuilder().File(WhatsAppSamples.Domain, "ChatStorage.sqlite", File.ReadAllBytes(empty)).Build(Path.Combine(Root, Udid));

        var error = await Assert.ThrowsAsync<DeviceException>(() => _service.GetWhatsAppStatusAsync(Id, Ct));
        Assert.Equal(BackupErrors.Corrupt, error.Code);
    }

    [Fact]
    public void Values_are_read_the_way_whatsapp_writes_them()
    {
        Assert.Equal("abcDEF123", WhatsAppStore.QuotedId([0x2A, 9, .. "abcDEF123"u8]));
        Assert.Equal("ID1", WhatsAppStore.QuotedId([0x08, 0x96, 0x01, 0x11, 1, 2, 3, 4, 5, 6, 7, 8, 0x0A, 2, 0x41, 0x42, 0x2A, 3, .. "ID1"u8]));
        Assert.Null(WhatsAppStore.QuotedId([0x2A, 50, 1, 2]));
        Assert.Null(WhatsAppStore.QuotedId([0x2A, 3, .. "a b"u8]));
        Assert.Null(WhatsAppStore.QuotedId([0x0A, 2, 0x41, 0x42]));
        Assert.Null(WhatsAppStore.QuotedId(null));

        Assert.Equal("+989121234567", WhatsAppStore.PhoneOf("989121234567@s.whatsapp.net"));
        Assert.Equal("+989121234567", WhatsAppStore.PhoneOf("989121234567:12@s.whatsapp.net"));
        Assert.Null(WhatsAppStore.PhoneOf("120363012345678901@g.us"));
        Assert.Null(WhatsAppStore.PhoneOf("82741956123@lid"));

        Assert.Equal("Message/Media/a/b.jpg", WhatsAppStore.BackupPathOf("Media/a/b.jpg"));
        Assert.Equal("Message/Media/a/b.jpg", WhatsAppStore.BackupPathOf("/var/mobile/Containers/Shared/AppGroup/X/Message/Media/a/b.jpg"));
        Assert.Equal("Message/Media/a/b.jpg", WhatsAppStore.BackupPathOf("Message/Media/a/b.jpg"));
        Assert.Null(WhatsAppStore.BackupPathOf("Media/../../etc"));
        Assert.Null(WhatsAppStore.BackupPathOf(" "));

        Assert.Equal(WhatsAppMediaKind.Voice, WhatsAppStore.KindOf(3, "a.opus", null, false, false));
        Assert.Equal(WhatsAppMediaKind.Audio, WhatsAppStore.KindOf(3, "a.m4a", null, false, false));
        Assert.Equal(WhatsAppMediaKind.Sticker, WhatsAppStore.KindOf(null, "a.webp", null, false, false));
        Assert.Equal(WhatsAppMediaKind.Video, WhatsAppStore.KindOf(99, "a.bin", "video/mp4", false, false));
        Assert.Equal(WhatsAppMediaKind.Document, WhatsAppStore.KindOf(99, "a.zip", null, false, false));
        Assert.Equal(WhatsAppMediaKind.Location, WhatsAppStore.KindOf(99, null, null, true, false));
        Assert.Equal(WhatsAppMediaKind.Contact, WhatsAppStore.KindOf(99, null, null, false, true));
    }

    [Fact]
    public void A_damaged_log_leaves_the_database_as_it_is()
    {
        var (database, wal) = WhatsAppSamples.ChatStorageWithLog();
        Assert.NotSame(database, SqliteWal.Apply(database, wal));

        var bent = (byte[])wal.Clone();
        bent[32 + 24 + 100] ^= 0xFF; // the first frame's page no longer matches its checksum
        Assert.Same(database, SqliteWal.Apply(database, bent));
        var otherSalt = (byte[])wal.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(otherSalt.AsSpan(32 + 8), 12345);
        Assert.Same(database, SqliteWal.Apply(database, otherSalt));
        Assert.Same(database, SqliteWal.Apply(database, wal.AsSpan(0, 40)));
        Assert.Same(database, SqliteWal.Apply(database, new byte[100]));
    }

    private sealed class RecordingPrinter : IPdfPrinter
    {
        public List<string> Html { get; } = [];

        public Task PrintToPdfAsync(string htmlPath, string pdfPath, CancellationToken ct)
        {
            Html.Add(File.ReadAllText(htmlPath));
            File.WriteAllText(pdfPath, "%PDF-1.7");
            return Task.CompletedTask;
        }
    }
}
