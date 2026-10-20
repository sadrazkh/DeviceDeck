using System.Text;
using System.Text.Json.Nodes;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Backups.Messages;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Ios.Backup.Format;

namespace DeviceDeck.Ios.Tests.Backup;

/// <summary>Backups written the way an iPhone writes them, read back through BackupService: files, contacts, messages, exports.</summary>
public sealed class BackupReadingTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Timeout();
    private const string Udid = BackupFixtureBuilder.Udid;

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-backup-").FullName;
    private readonly DeviceHub _hub = new([]);
    private BackupService _service = null!;

    private string Root => Path.Combine(_dir, "Backups");
    private string AppleRoot => Path.Combine(_dir, "Apple");

    public Task InitializeAsync()
    {
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        settings.Update(new JsonObject { ["backupFolder"] = Root });
        _service = new BackupService(_hub, settings, new IosBackupFormat(), new BackupServiceOptions { AppleBackupFolders = [AppleRoot] });
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

    private string Plain(Action<BackupFixtureBuilder>? more = null)
    {
        var builder = new BackupFixtureBuilder().AddStandardContent();
        more?.Invoke(builder);
        return builder.Build(Path.Combine(Root, Udid));
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

    [Fact]
    public void Aes_key_wrap_matches_RFC_3394()
    {
        var kek128 = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var key128 = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");
        var wrapped = AesKeyWrap.Wrap(kek128, key128);
        Assert.Equal("1FA68B0A8112B447AEF34BD8FB5A7B829D3E862371D2CFE5", Convert.ToHexString(wrapped));
        Assert.Equal(key128, AesKeyWrap.Unwrap(kek128, wrapped));

        var kek256 = Convert.FromHexString("000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F");
        var key256 = Convert.FromHexString("00112233445566778899AABBCCDDEEFF000102030405060708090A0B0C0D0E0F");
        Assert.Equal("28C9F404C4B810F4CBCCB35CFB87F8263F5786E2D80ED326CBC7F0E71A99F43BFB988B9B7A02DD21",
            Convert.ToHexString(AesKeyWrap.Wrap(kek256, key256)));

        Assert.Null(AesKeyWrap.Unwrap(kek256, wrapped));
    }

    [Fact]
    public void Header_says_who_when_and_whether_encrypted()
    {
        var folder = Plain();
        var header = new IosBackupFormat().ReadHeader(folder)!;

        Assert.Equal(Udid, header.DeviceId);
        Assert.Equal("Nima’s iPhone", header.DeviceName);
        Assert.Equal("iPhone 16", header.MarketingName);
        Assert.Equal("18.4", header.ProductVersion);
        Assert.False(header.Encrypted);
        Assert.True(header.Complete);
        Assert.Equal(new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero), header.Date);
        Assert.Equal("WhatsApp", header.AppNames["net.whatsapp.WhatsApp"]);
        Assert.Equal(2, header.AppCount);
        Assert.Null(new IosBackupFormat().ReadHeader(_dir));
    }

    [Fact]
    public async Task Domains_are_grouped_by_app_and_folders_listed()
    {
        Plain();
        var backup = Assert.Single(_service.List());
        Assert.Equal((BackupState.Complete, BackupSource.DeviceDeck, DevicePlatform.Ios, false), (backup.State, backup.Source, backup.Platform, backup.Locked));
        Assert.Equal("dd:" + Udid, backup.Id);

        var domains = await _service.GetDomainsAsync(backup.Id, Ct);
        var whatsapp = domains.Single(d => d.Domain == "AppDomain-net.whatsapp.WhatsApp");
        Assert.Equal((BackupDomainKind.App, "net.whatsapp.WhatsApp", "WhatsApp", 2), (whatsapp.Kind, whatsapp.AppId, whatsapp.Name, whatsapp.Files));
        var group = domains.Single(d => d.Kind == BackupDomainKind.AppGroup);
        Assert.Equal(("net.whatsapp.WhatsApp", "WhatsApp"), (group.AppId, group.Name));
        var plugin = domains.Single(d => d.Kind == BackupDomainKind.AppPlugin);
        Assert.Equal("net.whatsapp.WhatsApp", plugin.AppId);
        Assert.Equal(BackupDomainKind.System, domains.Single(d => d.Domain == "HomeDomain").Kind);
        Assert.True(domains.ToList().FindIndex(d => d.Kind == BackupDomainKind.System) > domains.ToList().FindIndex(d => d.Kind == BackupDomainKind.App));

        var top = await _service.GetFilesAsync(backup.Id, "AppDomain-net.whatsapp.WhatsApp", "", Ct);
        Assert.Equal(["Documents"], top.Select(e => e.Name));
        var documents = await _service.GetFilesAsync(backup.Id, "AppDomain-net.whatsapp.WhatsApp", "Documents", Ct);
        Assert.Equal(["Sub folder", "notes.txt"], documents.Select(e => e.Name));
        Assert.True(documents[0].IsDirectory);
        Assert.Equal(19, documents[1].Size);

        // Folders with no row of their own are made up from the paths under them.
        var sms = await _service.GetFilesAsync(backup.Id, "HomeDomain", "Library", Ct);
        Assert.Contains(sms, e => e is { Name: "SMS", IsDirectory: true });

        var missing = await Assert.ThrowsAsync<DeviceException>(() => _service.GetFilesAsync(backup.Id, "NoSuchDomain", "", Ct));
        Assert.Equal(DeviceErrorCodes.PathNotFound, missing.Code);
    }

    [Fact]
    public async Task Extract_copies_files_and_keeps_both_when_a_name_is_taken()
    {
        Plain();
        var id = "dd:" + Udid;
        var target = Path.Combine(_dir, "out");
        var first = await WaitForAsync(_service.StartExtract(id, [new BackupItemRef("AppDomain-net.whatsapp.WhatsApp", "Documents")], target));
        Assert.Equal(BackupJobState.Done, first.State);
        Assert.Equal((2, 23L), (first.Files, first.Bytes));
        Assert.Equal("hello from whatsapp", File.ReadAllText(Path.Combine(target, "Documents", "notes.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(target, "Documents", "Sub folder", "deep.txt")));
        Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(target, "Documents", "notes.txt")));

        var again = await WaitForAsync(_service.StartExtract(id, [new BackupItemRef("AppDomain-net.whatsapp.WhatsApp", "Documents/notes.txt")], Path.Combine(target, "Documents")));
        Assert.Equal(Path.Combine(target, "Documents", "notes (2).txt"), again.Destination);
        Assert.Equal("hello from whatsapp", File.ReadAllText(again.Destination!));

        var whole = await WaitForAsync(_service.StartExtract(id, [new BackupItemRef("AppDomain-com.apple.Pages", "")], target));
        Assert.True(File.Exists(Path.Combine(target, "Pages", "Documents", "Report.pages")), whole.Error?.Message);
        Assert.Empty(Directory.EnumerateFiles(target, "*.ddpart", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Contacts_come_with_labels_birthdays_addresses_and_photos()
    {
        Plain();
        var contacts = await _service.GetContactsAsync("dd:" + Udid, Ct);

        Assert.Equal(["John Appleseed", "Leila", "Pizza Place", "سارا احمدی"], contacts.Select(c => c.DisplayName));
        var sara = contacts.Single(c => c.FirstName == "سارا");
        Assert.Equal(("mobile", "+98 912 345 6789"), (sara.Phones[0].Label, sara.Phones[0].Value));
        Assert.Equal(("home", "sara@example.com"), (sara.Emails[0].Label, sara.Emails[0].Value));
        Assert.Equal("1990-05-01", sara.Birthday);
        Assert.Equal(("خیابان ولیعصر ۱۲", "تهران", "1415", "Iran"), (sara.Addresses[0].Street, sara.Addresses[0].City, sara.Addresses[0].PostalCode, sara.Addresses[0].Country));
        Assert.True(sara.HasPhoto);

        var john = contacts.Single(c => c.LastName == "Appleseed");
        Assert.Equal(["iphone", "Gym"], john.Phones.Select(p => p.Label));
        Assert.Equal(("Apple", "Engineer"), (john.Organization, john.JobTitle));
        Assert.Equal("https://example.com/john", john.Urls.Single().Value);
        Assert.False(john.HasPhoto);
        Assert.Equal("--03-21", contacts.Single(c => c.FirstName == "Leila").Birthday);
        Assert.Equal("main", contacts.Single(c => c.Organization == "Pizza Place").Phones[0].Label);
    }

    [Fact]
    public async Task Conversations_and_messages_read_like_the_phone_shows_them()
    {
        Plain();
        var id = "dd:" + Udid;
        var conversations = await _service.GetConversationsAsync(id, Ct);

        Assert.Equal(["سارا احمدی", "Family", "Pizza Place"], conversations.Select(c => c.Title));
        var sara = conversations[0];
        Assert.Equal((4, 3, false, "iMessage", "Photos"), (sara.MessageCount, sara.AttachmentCount, sara.IsGroup, sara.Service, sara.Preview));
        var family = conversations[1];
        Assert.True(family.IsGroup);
        Assert.Equal(["سارا احمدی", "John Appleseed"], family.Members.Select(m => m.Name));
        Assert.Equal("12345", conversations[2].Members.Single().Handle);

        var page = await _service.GetMessagesAsync(id, sara.Id, 0, 10, Ct);
        Assert.Equal(4, page.Total);
        Assert.Equal(["سلام! خوبی؟", "Hi Sara 👋", "Text only in attributedBody", "Photos"], page.Items.Select(m => m.Text));
        Assert.Equal("سارا احمدی", page.Items[0].SenderName);
        Assert.True(page.Items[1].FromMe);
        Assert.Null(page.Items[1].Handle);
        var photos = page.Items[3].Attachments;
        Assert.Equal(["IMG_0001.JPG", "IMG_0002.HEIC", "clip.mov"], photos.Select(a => a.Name));
        Assert.Equal([true, true, false], photos.Select(a => a.InBackup));
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero), page.Items[0].Date);

        var second = await _service.GetMessagesAsync(id, sara.Id, 2, 1, Ct);
        Assert.Equal("Text only in attributedBody", Assert.Single(second.Items).Text);

        var group = await _service.GetMessagesAsync(id, family.Id, 0, 10, Ct);
        Assert.Equal((MessageKind.Event, "Family"), (group.Items[0].Kind, group.Items[0].Text));
        Assert.Equal("John Appleseed", group.Items[1].SenderName);

        var sms = await _service.GetMessagesAsync(id, conversations[2].Id, 0, 10, Ct);
        Assert.Equal(new DateTimeOffset(2016, 11, 5, 0, 53, 20, TimeSpan.Zero), sms.Items[0].Date);
    }

    [Fact]
    public async Task Contacts_export_as_vcard_and_csv()
    {
        Plain();
        var id = "dd:" + Udid;
        var target = Path.Combine(_dir, "contacts");

        var vcard = await WaitForAsync(_service.StartContactsExport(id, null, ContactsFormat.VCard, target));
        Assert.Equal(BackupJobState.Done, vcard.State);
        Assert.EndsWith(".vcf", vcard.Destination);
        var text = File.ReadAllText(vcard.Destination!);
        Assert.Equal(4, text.Split("BEGIN:VCARD").Length - 1);
        Assert.Contains("N:احمدی;سارا;;;\r\n", text);
        Assert.Contains("TEL;TYPE=CELL:+98 912 345 6789\r\n", text);
        Assert.Contains("TEL;TYPE=CELL,IPHONE:+1 (555) 010-2030\r\n", text);
        Assert.Contains("item1.TEL:+1 555 999 0000\r\nitem1.X-ABLabel:Gym\r\n", text);
        Assert.Contains("NOTE:Line one\\nLine two\\; with\\, commas\r\n", text);
        Assert.Contains("BDAY:--03-21\r\n", text);
        Assert.Contains("PHOTO;ENCODING=b;TYPE=JPEG:" + Convert.ToBase64String(BackupSamples.Jpeg)[..20], text.Replace("\r\n ", ""));
        Assert.All(text.Split("\r\n"), line => Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line));
        Assert.False(File.ReadAllBytes(vcard.Destination!).AsSpan().StartsWith(Encoding.UTF8.Preamble));

        var csv = await WaitForAsync(_service.StartContactsExport(id, ["2"], ContactsFormat.Csv, target));
        var bytes = File.ReadAllBytes(csv.Destination!);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Name,Given Name,Additional Name,Family Name", lines[0]);
        Assert.Contains("\"Line one\nLine two; with, commas\"", lines[1]);
        Assert.Contains("Mobile,+1 (555) 010-2030,Gym,+1 555 999 0000", lines[1]);
    }

    [Fact]
    public async Task Messages_export_as_html_with_attachments_next_to_it()
    {
        Plain();
        var id = "dd:" + Udid;
        var sara = (await _service.GetConversationsAsync(id, Ct))[0];
        var target = Path.Combine(_dir, "messages");

        var job = await WaitForAsync(_service.StartMessagesExport(id, [sara.Id], MessagesFormat.Html, target, null));
        Assert.Equal(BackupJobState.Done, job.State);
        var html = File.ReadAllText(Path.Combine(target, "سارا احمدی.html"));
        Assert.Contains("<h1 dir=\"auto\">سارا احمدی</h1>", html);
        Assert.Contains("Hi Sara 👋", html);
        Assert.DoesNotContain("Loved", html);
        Assert.Contains("<img src=\"%D8%B3%D8%A7%D8%B1%D8%A7%20%D8%A7%D8%AD%D9%85%D8%AF%DB%8C%20files/IMG_0001.JPG\"", html);
        Assert.Contains("IMG_0002.HEIC", html);
        Assert.Contains("clip.mov · 4 KB — not in this backup", html);
        Assert.Equal(BackupSamples.Jpeg, File.ReadAllBytes(Path.Combine(target, "سارا احمدی files", "IMG_0001.JPG")));
        Assert.True(File.Exists(Path.Combine(target, "سارا احمدی files", "IMG_0002.HEIC")));

        var pdf = await Assert.ThrowsAsync<DeviceException>(() => Task.FromResult(_service.StartMessagesExport(id, [sara.Id], MessagesFormat.Pdf, target, null)));
        Assert.Equal(DeviceErrorCodes.NotSupported, pdf.Code);
    }

    [Fact]
    public async Task Messages_export_to_pdf_prints_the_page_and_keeps_the_attachments()
    {
        Plain();
        var id = "dd:" + Udid;
        var family = (await _service.GetConversationsAsync(id, Ct))[1];
        var target = Path.Combine(_dir, "pdf");
        var printer = new RecordingPrinter();

        var job = await WaitForAsync(_service.StartMessagesExport(id, [family.Id], MessagesFormat.Pdf, target, printer));
        Assert.Equal(BackupJobState.Done, job.State);
        var (html, pdf) = Assert.Single(printer.Printed);
        Assert.Equal(Path.Combine(target, "Family.pdf"), pdf);
        Assert.Contains("Dinner at 8?", printer.Html);
        Assert.False(File.Exists(html));
        Assert.True(File.Exists(pdf));
    }

    [Fact]
    public async Task Encrypted_backup_opens_only_with_its_password()
    {
        new BackupFixtureBuilder { Password = "s3cret pass" }.AddStandardContent().Build(Path.Combine(Root, Udid));
        var id = "dd:" + Udid;
        var listed = Assert.Single(_service.List());
        Assert.Equal((true, true), (listed.Encrypted, listed.Locked));

        var locked = await Assert.ThrowsAsync<DeviceException>(() => _service.GetContactsAsync(id, Ct));
        Assert.Equal(BackupErrors.PasswordRequired, locked.Code);
        var wrong = await Assert.ThrowsAsync<DeviceException>(() => _service.UnlockAsync(id, "guess", Ct));
        Assert.Equal(BackupErrors.WrongPassword, wrong.Code);

        await _service.UnlockAsync(id, "s3cret pass", Ct);
        Assert.False(Assert.Single(_service.List()).Locked);
        Assert.Equal(4, (await _service.GetContactsAsync(id, Ct)).Count);
        Assert.Equal("Text only in attributedBody", (await _service.GetMessagesAsync(id, "1", 2, 1, Ct)).Items[0].Text);

        var target = Path.Combine(_dir, "decrypted");
        var job = await WaitForAsync(_service.StartExtract(id, [new BackupItemRef("MediaDomain", "Library/SMS/Attachments/aa/10/A1/IMG_0001.JPG")], target));
        Assert.Equal(BackupSamples.Jpeg, File.ReadAllBytes(job.Destination!));

        _service.Lock(id);
        Assert.True(Assert.Single(_service.List()).Locked);
        await Assert.ThrowsAsync<DeviceException>(() => _service.GetDomainsAsync(id, Ct));
    }

    [Fact]
    public async Task Unfinished_backup_is_listed_but_not_opened()
    {
        new BackupFixtureBuilder { SnapshotState = "uploading" }.AddStandardContent().Build(Path.Combine(Root, Udid));
        var backup = Assert.Single(_service.List());
        Assert.Equal(BackupState.Incomplete, backup.State);
        var ex = await Assert.ThrowsAsync<DeviceException>(() => _service.GetDomainsAsync(backup.Id, Ct));
        Assert.Equal(BackupErrors.Corrupt, ex.Code);
    }

    [Fact]
    public async Task Apple_folder_backups_are_read_but_never_deleted()
    {
        new BackupFixtureBuilder { DeviceId = "00008110-AAAAAAAAAAAAAAAA", DeviceName = "Old iPhone" }.AddStandardContent()
            .Build(Path.Combine(AppleRoot, "00008110-AAAAAAAAAAAAAAAA"));
        var backup = Assert.Single(_service.List());
        Assert.Equal((BackupSource.Apple, "apple0:00008110-AAAAAAAAAAAAAAAA"), (backup.Source, backup.Id));
        Assert.Equal(4, (await _service.GetContactsAsync(backup.Id, Ct)).Count);

        var refused = Assert.Throws<DeviceException>(() => _service.GetDeletePlan([backup.Id]));
        Assert.Equal(DeviceErrorCodes.PermissionDenied, refused.Code);
        Assert.Throws<DeviceException>(() => _service.Delete([backup.Id]));
        Assert.True(Directory.Exists(Path.Combine(AppleRoot, "00008110-AAAAAAAAAAAAAAAA")));

        Assert.Equal(DeviceErrorCodes.PathNotFound, Assert.Throws<DeviceException>(() => _service.GetDeletePlan(["apple0:..\\x"])).Code);
        Assert.Equal(DeviceErrorCodes.PathNotFound, Assert.Throws<DeviceException>(() => _service.GetDeletePlan(["apple7:x"])).Code);
    }

    [Fact]
    public async Task A_backup_in_a_deep_folder_is_read_like_any_other()
    {
        // Its databases end up past 260 characters. DeviceDeck.exe isn't long-path aware, so there SQLite's own file
        // calls fail at that length (seen in the app); the test runner is, so this only checks deep folders work.
        var deep = Path.Combine(_dir, new string('d', 120), new string('e', 60));
        var settings = new SettingsStore(Path.Combine(_dir, "deep-settings.json"));
        settings.Update(new JsonObject { ["backupFolder"] = deep });
        await using var service = new BackupService(_hub, settings, new IosBackupFormat(), new BackupServiceOptions { AppleBackupFolders = [] });
        service.Start();
        var folder = new BackupFixtureBuilder().AddStandardContent().Build(Path.Combine(deep, Udid));
        Assert.Contains(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories), f => f.Length > 260);

        Assert.Equal(4, (await service.GetContactsAsync("dd:" + Udid, Ct)).Count);
        Assert.Equal(3, (await service.GetConversationsAsync("dd:" + Udid, Ct)).Count);
    }

    [Fact]
    public void Text_inside_attributedBody_is_found()
    {
        Assert.Equal("Short", MessageStore.TypedStreamText(BackupSamples.TypedStream("Short")));
        var longText = new string('x', 300) + " پایان";
        Assert.Equal(longText, MessageStore.TypedStreamText(BackupSamples.TypedStream(longText)));
        Assert.Null(MessageStore.TypedStreamText("no string here"u8));
    }

    private sealed class RecordingPrinter : IPdfPrinter
    {
        public List<(string Html, string Pdf)> Printed { get; } = [];
        public string Html { get; private set; } = "";

        public Task PrintToPdfAsync(string htmlPath, string pdfPath, CancellationToken ct)
        {
            Printed.Add((htmlPath, pdfPath));
            Html = File.ReadAllText(htmlPath);
            File.WriteAllText(pdfPath, "%PDF-1.7");
            return Task.CompletedTask;
        }
    }
}
