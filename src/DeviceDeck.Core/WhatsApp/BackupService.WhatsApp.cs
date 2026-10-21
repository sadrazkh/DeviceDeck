using System.Globalization;
using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups.Contacts;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.WhatsApp;

namespace DeviceDeck.Core.Backups;

// Round 4, WhatsApp: chats and media from an iPhone backup, read through the same sessions as contacts and messages
// (so encrypted backups work once unlocked, and nothing decrypted is written to disk), and their exports as jobs.

public sealed partial class BackupService
{
    /// <summary>The largest file whatsapp.media hands to the page (a voice note is a few hundred KB).</summary>
    public const long MaxWhatsAppMediaBytes = 32L * 1024 * 1024;

    /// <summary>Pictures larger than this aren't read just to make a thumbnail.</summary>
    private const long MaxWhatsAppThumbnailSource = 40L * 1024 * 1024;

    /// <summary>Whether the backup holds WhatsApp (and WhatsApp Business), with counts, and what this version lacks.</summary>
    public async Task<WhatsAppStatus> GetWhatsAppStatusAsync(string backupId, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var installed = session.Archive.Domains.Any(d => d.Domain is AppPrefix + WhatsAppStore.WhatsAppBundle or AppPrefix + WhatsAppStore.BusinessBundle)
                        || session.Record.Ios?.AppNames.Keys.Any(k => k is WhatsAppStore.WhatsAppBundle or WhatsAppStore.BusinessBundle) == true;
        return await WithWhatsAppAsync(session, w => new WhatsAppStatus(
            backupId,
            w.Stores.Count > 0,
            installed || w.Stores.Count > 0,
            w.Stores.Select(s => s.Summary()).ToList(),
            w.Stores.SelectMany(s => s.Missing).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()), ct).ConfigureAwait(false);
    }

    /// <summary>Every chat of both apps, newest first.</summary>
    public async Task<IReadOnlyList<WhatsAppChat>> GetWhatsAppChatsAsync(string backupId, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        return await WithWhatsAppAsync(session, w => (IReadOnlyList<WhatsAppChat>)w.Stores.SelectMany(s => s.Chats())
            .OrderByDescending(c => c.LastMessageAt ?? DateTimeOffset.MinValue).ToList(), ct).ConfigureAwait(false);
    }

    public async Task<Page<WhatsAppMessage>> GetWhatsAppMessagesAsync(string backupId, string chatId, int offset, int limit, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        return await WithWhatsAppAsync(session, w => StoreOf(w, chatId).Messages(chatId, offset, limit), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A picture for a media item: WhatsApp's own thumbnail when the backup has it, else the photo or sticker made small
    /// (JPEG, longest side about <paramref name="size"/>). Null when there is nothing to show.
    /// </summary>
    public async Task<byte[]?> GetWhatsAppThumbnailAsync(string backupId, string mediaId, int size, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var (domain, location) = await WithWhatsAppAsync(session, w => StoreOf(w, mediaId) is var s ? (s.Domain, s.Locate(mediaId)) : default, ct).ConfigureAwait(false);
        if (location is not { } where)
        {
            return null;
        }

        var archive = session.Archive;
        try
        {
            if (where.Thumbnail is { } thumb && archive.Find(domain, thumb) is { IsDirectory: false, Size: < 4 * 1024 * 1024 } thumbEntry)
            {
                var bytes = await ReadWholeAsync(archive, thumbEntry, ct).ConfigureAwait(false);
                if (ImageFormats.Sniff(bytes).StartsWith("image/", StringComparison.Ordinal))
                {
                    return bytes;
                }
            }

            if (where.File is { } file && archive.Find(domain, file) is { IsDirectory: false } entry && entry.Size <= MaxWhatsAppThumbnailSource
                && WhatsAppStore.KindOf(null, file, null, false, false) is WhatsAppMediaKind.Image or WhatsAppMediaKind.Sticker)
            {
                var bytes = await ReadWholeAsync(archive, entry, ct).ConfigureAwait(false);
                var type = ImageFormats.Sniff(bytes);
                if (type == "image/webp" || (type is "image/jpeg" or "image/png" or "image/gif" && (bytes.Length <= 512 * 1024 || _options.Transcoder is null)))
                {
                    return bytes;
                }

                if (_options.Transcoder is { } transcoder)
                {
                    using var source = new MemoryStream(bytes, writable: false);
                    return await transcoder.ToJpegAsync(source, size, ct).ConfigureAwait(false);
                }
            }
        }
        catch (DeviceException ex) when (ex.Code == BackupErrors.Missing)
        {
        }
        return null;
    }

    /// <summary>One media file's bytes (a voice note to play in the page); larger files are only exported.</summary>
    public async Task<WhatsAppMediaFile> GetWhatsAppMediaAsync(string backupId, string mediaId, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var (domain, location) = await WithWhatsAppAsync(session, w => StoreOf(w, mediaId) is var s ? (s.Domain, s.Locate(mediaId)) : default, ct).ConfigureAwait(false);
        if (location?.File is not { } file || session.Archive.Find(domain, file) is not { IsDirectory: false } entry)
        {
            throw new DeviceException(BackupErrors.Missing, "That file isn't in this backup.");
        }

        if (entry.Size > MaxWhatsAppMediaBytes)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "This file is too big to open here. Export the chat to keep it with its files.");
        }

        var bytes = await ReadWholeAsync(session.Archive, entry, ct).ConfigureAwait(false);
        return new WhatsAppMediaFile(MimeOf(file, bytes), bytes.Length, bytes);
    }

    /// <summary>One HTML page (or PDF) per chat with its media in a "&lt;chat&gt; files" folder; all chats when <paramref name="chatIds"/> is empty.</summary>
    public BackupJob StartWhatsAppExport(string backupId, IReadOnlyList<string>? chatIds, MessagesFormat format, string destination, IPdfPrinter? printer)
    {
        RequirePcFolder(destination);
        if (format == MessagesFormat.Pdf && printer is null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "PDF export needs the DeviceDeck window. Save as HTML instead.");
        }

        var record = Find(backupId);
        RequireIosForWhatsApp(record);
        var count = chatIds?.Distinct(StringComparer.Ordinal).Count() ?? 0;
        var name = record.Info.DeviceName;
        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Export,
            DeviceId = null,
            DeviceName = name,
            Title = count switch
            {
                0 => $"Saving all WhatsApp chats from the backup of {name}",
                1 => $"Saving a WhatsApp chat from the backup of {name}",
                _ => $"Saving {count:N0} WhatsApp chats from the backup of {name}",
            },
            CreatedAt = _time.GetLocalNow(),
            BackupId = backupId,
            Destination = destination,
            Work = (j, ct) => RunWhatsAppExportAsync(j, backupId, chatIds ?? [], format, destination, printer, ct),
        };
        return Add(job, exclusive: false);
    }

    /// <summary>Every media file the backup holds for the chats (all when empty), one folder per chat, in a dated folder.</summary>
    public BackupJob StartWhatsAppMediaExport(string backupId, IReadOnlyList<string>? chatIds, string destination)
    {
        RequirePcFolder(destination);
        var record = Find(backupId);
        RequireIosForWhatsApp(record);
        var job = new Job
        {
            Id = NewId(),
            Kind = BackupJobKind.Export,
            DeviceId = null,
            DeviceName = record.Info.DeviceName,
            Title = $"Saving WhatsApp media from the backup of {record.Info.DeviceName}",
            CreatedAt = _time.GetLocalNow(),
            BackupId = backupId,
            Destination = destination,
            Work = (j, ct) => RunWhatsAppMediaExportAsync(j, backupId, chatIds ?? [], destination, ct),
        };
        return Add(job, exclusive: false);
    }

    // ---------------------------------------------------------------- job bodies

    private async Task RunWhatsAppExportAsync(Job job, string backupId, IReadOnlyList<string> chatIds, MessagesFormat format, string destination,
        IPdfPrinter? printer, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var chats = await WithWhatsAppAsync(session, w => ChatsFor(w, chatIds), ct).ConfigureAwait(false);
        if (chats.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, chatIds.Count == 0 ? "This backup has no WhatsApp chats." : "Those chats aren't in this backup.");
        }

        var info = session.Record.Info;
        var made = info.CreatedAt ?? _time.GetLocalNow();
        // One chat goes where the user pointed; several get a folder of their own there.
        var folder = chats.Count == 1 ? destination : UniquePath(Path.Combine(destination, AndroidBackupEngine.Safe($"WhatsApp - {info.DeviceName} - {made.ToLocalTime():yyyy-MM-dd}")));
        Directory.CreateDirectory(folder);
        lock (_gate)
        {
            job.TotalFiles = chats.Count + chats.Sum(c => c.Chat.MediaCount);
            job.TotalBytes = null;
            job.Destination = folder;
        }

        var source = $"From the backup of {info.DeviceName}" + (info.CreatedAt is { } date ? $" made {date.ToLocalTime():d MMM yyyy, HH:mm}" : "") + " · saved with DeviceDeck";
        var files = 0;
        var bytes = 0L;
        var missing = 0;
        foreach (var (store, chat) in chats)
        {
            ct.ThrowIfCancellationRequested();
            var (messages, locations) = await WithWhatsAppAsync(session, _ =>
            {
                var all = store.AllMessages(chat.Id);
                return (all, all.Where(m => m.Media is { InBackup: true }).ToDictionary(m => m.Media!.Id, m => store.Locate(m.Media!.Id)?.File, StringComparer.Ordinal));
            }, ct).ConfigureAwait(false);

            var stem = UniqueStem(folder, Trim(AndroidBackupEngine.Safe(chat.Title), 80), format == MessagesFormat.Pdf ? ".pdf" : ".html");
            var filesFolder = stem + " files";
            var hrefs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                if (message.Media is not { } media || locations.GetValueOrDefault(media.Id) is not { } path || session.Archive.Find(store.Domain, path) is not { IsDirectory: false } entry)
                {
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                var target = await CopyOutAsync(session.Archive, entry, Path.Combine(folder, filesFolder), media.Name, message.Date, ct).ConfigureAwait(false);
                if (target is null)
                {
                    missing++;
                    continue;
                }

                hrefs[media.Id] = filesFolder + "/" + Path.GetFileName(target);
                files++;
                bytes += entry.Size;
                Report(job, BackupPhase.Transferring, Percent(files, job), bytes, files);
            }

            var html = Path.Combine(folder, stem + ".html");
            await using (var writer = new StreamWriter(html, false, new UTF8Encoding(false)))
            {
                WhatsAppHtml.Write(writer, chat, messages, source, m => hrefs.GetValueOrDefault(m.Id), forPrint: format == MessagesFormat.Pdf);
            }

            var page = html;
            if (format == MessagesFormat.Pdf)
            {
                page = Path.Combine(folder, stem + ".pdf");
                await printer!.PrintToPdfAsync(html, page, ct).ConfigureAwait(false);
                File.Delete(html);
            }

            if (chats.Count == 1)
            {
                lock (_gate)
                {
                    job.Destination = page;
                }
            }

            files++;
            Report(job, BackupPhase.Transferring, Percent(files, job), bytes, files);
        }

        if (missing > 0)
        {
            lock (_gate)
            {
                job.Warning = new BackupJobError(BackupErrors.Missing, $"{missing:N0} media files are listed in the backup but their data isn't in it.");
            }
        }
    }

    private async Task RunWhatsAppMediaExportAsync(Job job, string backupId, IReadOnlyList<string> chatIds, string destination, CancellationToken ct)
    {
        var session = await GetSessionAsync(backupId, ct).ConfigureAwait(false);
        var plan = await WithWhatsAppAsync(session, w => ChatsFor(w, chatIds)
            .Select(c => (c.Store, c.Chat, Files: c.Store.AllMessages(c.Chat.Id)
                .Where(m => m.Media is { InBackup: true })
                .Select(m => (m.Media!, m.Date, Path: c.Store.Locate(m.Media!.Id)?.File))
                .Where(f => f.Path is not null)
                .Select(f => (Media: f.Item1, f.Date, Entry: session.Archive.Find(c.Store.Domain, f.Path!)))
                .Where(f => f.Entry is { IsDirectory: false })
                .ToList()))
            .Where(c => c.Files.Count > 0)
            .ToList(), ct).ConfigureAwait(false);
        if (plan.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "This backup holds no WhatsApp media for those chats.");
        }

        var info = session.Record.Info;
        var made = info.CreatedAt ?? _time.GetLocalNow();
        var root = UniquePath(Path.Combine(destination, AndroidBackupEngine.Safe($"WhatsApp media - {info.DeviceName} - {made.ToLocalTime():yyyy-MM-dd}")));
        Directory.CreateDirectory(root);
        lock (_gate)
        {
            job.TotalFiles = plan.Sum(p => p.Files.Count);
            job.TotalBytes = plan.Sum(p => p.Files.Sum(f => f.Entry!.Size));
            job.Destination = root;
        }

        var files = 0;
        var bytes = 0L;
        var missing = 0;
        foreach (var (_, chat, chatFiles) in plan)
        {
            var folder = UniquePath(Path.Combine(root, Trim(AndroidBackupEngine.Safe(chat.Title), 80)));
            foreach (var (media, date, entry) in chatFiles)
            {
                ct.ThrowIfCancellationRequested();
                if (await CopyOutAsync(session.Archive, entry!, folder, media.Name, date, ct).ConfigureAwait(false) is null)
                {
                    missing++;
                }

                files++;
                bytes += entry!.Size;
                Report(job, BackupPhase.Transferring, Percent(bytes, job), bytes, files);
            }
        }

        if (missing == files)
        {
            throw new DeviceException(BackupErrors.Missing, "The backup lists WhatsApp's media, but their data isn't in it.");
        }

        if (missing > 0)
        {
            lock (_gate)
            {
                job.Warning = new BackupJobError(BackupErrors.Missing, $"{missing:N0} of {files:N0} media files are listed in the backup but their data isn't in it.");
            }
        }
    }

    /// <summary>Copies one file out of the backup into <paramref name="folder"/> under a free name, dated like its message. Null when the backup lacks its data.</summary>
    private static async Task<string?> CopyOutAsync(IBackupArchive archive, BackupEntry entry, string folder, string name, DateTimeOffset? date, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var target = UniquePath(Path.Combine(folder, AndroidBackupEngine.Safe(name)));
        var temp = target + ".ddpart";
        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                await archive.CopyToAsync(entry, output, null, ct).ConfigureAwait(false);
            }

            if ((date ?? entry.Modified) is { } when)
            {
                File.SetLastWriteTimeUtc(temp, when.UtcDateTime);
            }

            File.Move(temp, target);
            return target;
        }
        catch (DeviceException ex) when (ex.Code == BackupErrors.Missing)
        {
            File.Delete(temp);
            return null;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    // ---------------------------------------------------------------- opening

    /// <summary>The WhatsApp databases of one opened backup: a store per app found, and the images to close with the session.</summary>
    private sealed class WhatsAppBackup : IDisposable
    {
        public List<WhatsAppStore> Stores { get; } = [];
        public List<SqliteImage> Images { get; } = [];

        public void Dispose()
        {
            foreach (var image in Images)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>Runs <paramref name="read"/> with WhatsApp's databases open (opened once per session; SQLite needs one caller at a time).</summary>
    private async Task<T> WithWhatsAppAsync<T>(Session session, Func<WhatsAppBackup, T> read, CancellationToken ct)
    {
        RequireIosForWhatsApp(session.Record);
        await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (session.WhatsApp is null)
            {
                var contacts = session.Loaded ? session.Contacts ?? [] : await ReadAddressBookAsync(session.Archive, ct).ConfigureAwait(false);
                var data = new WhatsAppBackup();
                try
                {
                    foreach (var account in new[] { WhatsAppAccount.WhatsApp, WhatsAppAccount.Business })
                    {
                        var domain = account == WhatsAppAccount.Business ? WhatsAppStore.BusinessDomain : WhatsAppStore.WhatsAppDomain;
                        if (await OpenWithLogAsync(session.Archive, domain, WhatsAppStore.DatabasePath, ct).ConfigureAwait(false) is not { } chats)
                        {
                            continue;
                        }

                        data.Images.Add(chats);
                        SqliteImage? book = null;
                        try
                        {
                            book = await OpenWithLogAsync(session.Archive, domain, WhatsAppStore.ContactsPath, ct).ConfigureAwait(false);
                        }
                        catch (DeviceException)
                        {
                            // WhatsApp's copy of the address book only adds names; the chats read without it.
                        }

                        if (book is not null)
                        {
                            data.Images.Add(book);
                        }

                        var archive = session.Archive;
                        data.Stores.Add(Guard(() => new WhatsAppStore(account, chats.Connection, path => archive.Find(domain, path), book?.Connection, contacts)));
                    }
                }
                catch
                {
                    data.Dispose();
                    throw;
                }
                session.WhatsApp = data;
            }
            return Guard(() => read(session.WhatsApp));
        }
        finally
        {
            session.Gate.Release();
        }
    }

    /// <summary>The phone's contacts, only to name WhatsApp numbers; a backup without a readable address book names none.</summary>
    private static async Task<IReadOnlyList<Contact>> ReadAddressBookAsync(IBackupArchive archive, CancellationToken ct)
    {
        try
        {
            using var book = await OpenDatabaseAsync(archive, AddressBookReader.Domain, AddressBookReader.DatabasePath, ct).ConfigureAwait(false);
            return book is null ? [] : Guard(() => AddressBookReader.Read(book.Connection));
        }
        catch (DeviceException)
        {
            return [];
        }
    }

    /// <summary>
    /// A database with its write-ahead log when the backup holds one ("-wal" next to it): the newest messages may be only
    /// there. The two are joined in memory; without a log the database opens as contacts and messages do.
    /// </summary>
    private static async Task<SqliteImage?> OpenWithLogAsync(IBackupArchive archive, string domain, string path, CancellationToken ct)
    {
        if (archive.Find(domain, path) is not { IsDirectory: false } entry)
        {
            return null;
        }

        if (archive.Find(domain, path + "-wal") is not { IsDirectory: false, Size: > 32 } log)
        {
            return await OpenDatabaseAsync(archive, domain, path, ct).ConfigureAwait(false);
        }

        byte[] wal;
        try
        {
            wal = await ReadWholeAsync(archive, log, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code == BackupErrors.Missing)
        {
            return await OpenDatabaseAsync(archive, domain, path, ct).ConfigureAwait(false);
        }

        var image = await ReadWholeAsync(archive, entry, ct).ConfigureAwait(false);
        return Guard(() => SqliteImage.FromBytes(SqliteWal.Apply(image, wal)));
    }

    private static async Task<byte[]> ReadWholeAsync(IBackupArchive archive, BackupEntry entry, CancellationToken ct)
    {
        if (archive.PlainPath(entry) is { } plain)
        {
            return await File.ReadAllBytesAsync(plain, ct).ConfigureAwait(false);
        }

        using var memory = new MemoryStream(entry.Size is > 0 and < int.MaxValue ? (int)entry.Size : 0);
        await archive.CopyToAsync(entry, memory, null, ct).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static WhatsAppStore StoreOf(WhatsAppBackup whatsApp, string id) =>
        whatsApp.Stores.FirstOrDefault(s => id.StartsWith(s.Prefix + ":", StringComparison.Ordinal))
        ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That chat isn't in this backup.");

    private static List<(WhatsAppStore Store, WhatsAppChat Chat)> ChatsFor(WhatsAppBackup whatsApp, IReadOnlyList<string> chatIds)
    {
        if (chatIds.Count == 0)
        {
            return whatsApp.Stores.SelectMany(s => s.Chats().Select(c => (s, c))).OrderByDescending(p => p.c.LastMessageAt ?? DateTimeOffset.MinValue).ToList();
        }

        return chatIds.Distinct(StringComparer.Ordinal)
            .Select(id => whatsApp.Stores.FirstOrDefault(s => id.StartsWith(s.Prefix + ":", StringComparison.Ordinal)) is { } store && store.Find(id) is { } chat ? (store, chat) : default)
            .Where(p => p.chat is not null)
            .ToList();
    }

    private static void RequireIosForWhatsApp(BackupRecord record)
    {
        if (record.Android is not null)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported,
                "Android backups don't hold WhatsApp's chats: WhatsApp keeps them encrypted with a key only the phone has. Back up WhatsApp's media from the phone instead.");
        }
    }

    private static string MimeOf(string file, byte[] bytes) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".opus" or ".ogg" => "audio/ogg",
        ".m4a" or ".aac" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".amr" => "audio/amr",
        ".wav" => "audio/wav",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".pdf" => "application/pdf",
        _ => ImageFormats.Sniff(bytes),
    };
}
