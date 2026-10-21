using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.WhatsApp;
using DeviceDeck.Ios.Backup.Format;
using Microsoft.Data.Sqlite;

namespace IosProbe;

/// <summary>
/// `whatsapp &lt;backup folder&gt;` (round 4): what DeviceDeck reads of WhatsApp in an iPhone backup on this PC, read-only,
/// as counts and shapes only. It never prints a message, a name or a number: table and column names, row counts,
/// histograms of WhatsApp's type numbers, and how many media files, thumbnails and replies were found. That is enough
/// to check the parts of the schema DeviceDeck reads from public descriptions (see the round 4 notes).
/// No phone is needed; nothing in the backup is changed (it is opened read-only, an encrypted one decrypted in memory).
/// </summary>
internal static class WhatsAppProbe
{
    public static async Task<int> RunAsync(Cli cli, CancellationToken ct)
    {
        if (cli.Positional.Count < 1)
            throw new CliException("usage: iosprobe whatsapp <backup folder> [--password-env NAME]");
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cli.Positional[0]));
        var password = cli.Option("password-env") is { } name ? Environment.GetEnvironmentVariable(name) : null;
        var format = new IosBackupFormat();
        var header = format.ReadHeader(folder) ?? throw new CliException($"{folder} isn't an iPhone backup (no Info.plist / Manifest.plist).");
        Console.WriteLine($"backup: iOS {header.ProductVersion}, {(header.Encrypted ? "encrypted" : "not encrypted")}, {(header.Complete ? "complete" : "NOT complete")}, " +
                          $"{header.Date?.ToLocalTime():yyyy-MM-dd HH:mm}, {header.AppCount} apps");
        if (header.Encrypted && string.IsNullOrEmpty(password))
            throw new CliException("The backup is encrypted: put its password in an environment variable and pass --password-env NAME.");

        // 1. Through the app's own code: BackupService lists the folder as an Apple-layout backup (read-only).
        var clock = Stopwatch.StartNew();
        var scratch = Directory.CreateTempSubdirectory("iosprobe-whatsapp-").FullName;
        try
        {
            var settings = new SettingsStore(Path.Combine(scratch, "settings.json"));
            settings.Update(new JsonObject { ["backupFolder"] = Path.Combine(scratch, "none") });
            await using var hub = new DeviceHub([]);
            await using var service = new BackupService(hub, settings, format, new BackupServiceOptions { AppleBackupFolders = [Path.GetDirectoryName(folder)!] });
            service.Start();
            var backup = service.List().FirstOrDefault(b => string.Equals(b.Path, folder, StringComparison.OrdinalIgnoreCase))
                         ?? throw new CliException("BackupService didn't list that folder as a backup.");
            if (header.Encrypted)
                await service.UnlockAsync(backup.Id, password!, ct);

            var status = await service.GetWhatsAppStatusAsync(backup.Id, ct);
            Console.WriteLine($"opened in {clock.Elapsed.TotalSeconds:0.0} s: found {status.Found}, installed {status.Installed}");
            foreach (var a in status.Accounts)
                Console.WriteLine($"  {a.Account}: {a.Chats} chats, {a.Messages} messages, {a.Media} media ({a.MediaInBackup} in the backup, {Out.Bytes(a.MediaBytes)})");
            Console.WriteLine($"missing ({status.Missing.Count}): {(status.Missing.Count == 0 ? "-" : string.Join(", ", status.Missing))}");
            if (!status.Found)
                return 0;

            clock.Restart();
            var chats = await service.GetWhatsAppChatsAsync(backup.Id, ct);
            Console.WriteLine($"chats: {chats.Count} in {clock.Elapsed.TotalSeconds:0.0} s; groups {chats.Count(c => c.IsGroup)}, archived {chats.Count(c => c.Archived)}, " +
                              $"with a phone number {chats.Count(c => c.Phone is not null)}, titled by number {chats.Count(c => c.Title.StartsWith('+'))}, " +
                              $"without a date {chats.Count(c => c.LastMessageAt is null)}");
            Console.WriteLine($"  messages per chat: max {chats.Max(c => c.MessageCount)}, median {Median(chats.Select(c => c.MessageCount))}; " +
                              $"last message years: {Histogram(chats.Where(c => c.LastMessageAt is not null).Select(c => c.LastMessageAt!.Value.Year.ToString(CultureInfo.InvariantCulture)))}");

            // Every message of every chat, through the same code the pane and the exports use.
            clock.Restart();
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            var media = new Dictionary<string, int>(StringComparer.Ordinal);
            int messages = 0, quotes = 0, quotesResolved = 0, groupMessages = 0, senderNamed = 0, senderNumber = 0, withThumb = 0, inBackup = 0, captions = 0, undated = 0;
            foreach (var chat in chats)
            {
                for (var offset = 0; offset < chat.MessageCount; offset += 1000)
                {
                    var page = await service.GetWhatsAppMessagesAsync(backup.Id, chat.Id, offset, 1000, ct);
                    foreach (var m in page.Items)
                    {
                        messages++;
                        Count(kinds, m.Kind.ToString());
                        undated += m.Date is null ? 1 : 0;
                        if (m.Quote is { } q)
                        {
                            quotes++;
                            quotesResolved += q.Text is not null || q.MediaKind is not null ? 1 : 0;
                        }

                        if (chat.IsGroup && !m.FromMe && m.Kind != WhatsAppMessageKind.Event)
                        {
                            groupMessages++;
                            if (m.SenderName is null || m.SenderName.StartsWith('+') || m.SenderName.All(char.IsAsciiDigit))
                                senderNumber++;
                            else
                                senderNamed++;
                        }

                        if (m.Media is { } item)
                        {
                            // Locations and contact cards have no file to be missing.
                            Count(media, item.Kind + (item.InBackup || item.Kind is WhatsAppMediaKind.Location or WhatsAppMediaKind.Contact ? "" : "(not in backup)"));
                            inBackup += item.InBackup ? 1 : 0;
                            withThumb += item.HasThumbnail ? 1 : 0;
                            captions += m.Text is not null ? 1 : 0;
                        }
                    }
                }
            }
            Console.WriteLine($"messages read: {messages} in {clock.Elapsed.TotalSeconds:0.0} s; kinds {Histogram(kinds)}; without a date {undated}");
            Console.WriteLine($"  media: {Histogram(media)}");
            Console.WriteLine($"  media in the backup {inBackup}, with a picture to show {withThumb}, with a caption {captions}");
            Console.WriteLine($"  replies {quotes}, of which the quoted message was found {quotesResolved}");
            Console.WriteLine($"  group messages from others {groupMessages}: sender named {senderNamed}, shown as a number {senderNumber}");

            // 2. The raw numbers behind the guesses (WhatsApp's own type codes), straight from the database.
            await RawAsync(backup.Id, folder, password, ct);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        return 0;
    }

    private static async Task RawAsync(string backupId, string folder, string? password, CancellationToken ct)
    {
        using var archive = await new IosBackupFormat().OpenAsync(folder, password, ct);
        foreach (var account in new[] { WhatsAppAccount.WhatsApp, WhatsAppAccount.Business })
        {
            var domain = account == WhatsAppAccount.Business ? WhatsAppStore.BusinessDomain : WhatsAppStore.WhatsAppDomain;
            var info = archive.Domains.FirstOrDefault(d => d.Domain == domain);
            if (info is null)
                continue;

            Console.WriteLine($"{account}: app group {info.Files} files, {Out.Bytes(info.Bytes)}");
            foreach (var file in new[] { WhatsAppStore.DatabasePath, WhatsAppStore.DatabasePath + "-wal", WhatsAppStore.DatabasePath + "-shm", WhatsAppStore.ContactsPath })
                Console.WriteLine($"  {file}: {(archive.Find(domain, file) is { } e ? Out.Bytes(e.Size) : "-")}");
            using var image = await BackupService.OpenWhatsAppDatabaseAsync(archive, account, ct);
            if (image is null)
                continue;

            var db = image.Connection;
            foreach (var table in new[] { "ZWACHATSESSION", "ZWAMESSAGE", "ZWAMEDIAITEM", "ZWAGROUPMEMBER", "ZWAPROFILEPUSHNAME", "ZWAMESSAGEINFO", "ZWAGROUPINFO" })
            {
                var columns = Columns(db, table);
                Console.WriteLine(columns.Count == 0 ? $"  table {table}: -" : $"  table {table}: {Scalar(db, $"SELECT COUNT(*) FROM {table}")} rows; {string.Join(' ', columns)}");
            }

            var message = Columns(db, "ZWAMESSAGE");
            var item = Columns(db, "ZWAMEDIAITEM");
            if (message.Contains("ZMESSAGETYPE"))
            {
                var join = message.Contains("ZMEDIAITEM") && item.Count > 0 ? "LEFT JOIN ZWAMEDIAITEM i ON i.Z_PK = m.ZMEDIAITEM" : "LEFT JOIN (SELECT NULL AS Z_PK) i ON 0";
                string Has(string column, string condition) => item.Contains(column) ? $"SUM(CASE WHEN {condition} THEN 1 ELSE 0 END)" : "NULL";
                Console.WriteLine("  ZMESSAGETYPE: count, with text, with a media item, with a file path, with lat/long, with a vCard, file extensions");
                using var command = db.CreateCommand();
                command.CommandText = $"""
                    SELECT m.ZMESSAGETYPE, COUNT(*), SUM(CASE WHEN m.ZTEXT IS NOT NULL AND m.ZTEXT <> '' THEN 1 ELSE 0 END), SUM(CASE WHEN i.Z_PK IS NOT NULL THEN 1 ELSE 0 END),
                           {Has("ZMEDIALOCALPATH", "i.ZMEDIALOCALPATH IS NOT NULL AND i.ZMEDIALOCALPATH <> ''")},
                           {Has("ZLATITUDE", "COALESCE(i.ZLATITUDE, 0) <> 0")},
                           {Has("ZVCARDSTRING", "i.ZVCARDSTRING LIKE 'BEGIN:VCARD%'")}
                    FROM ZWAMESSAGE m {join} GROUP BY m.ZMESSAGETYPE ORDER BY 2 DESC
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    Console.WriteLine($"    {Value(reader, 0),4}: {Value(reader, 1)}, text {Value(reader, 2)}, item {Value(reader, 3)}, path {Value(reader, 4)}, lat/long {Value(reader, 5)}, vCard {Value(reader, 6)}");
                if (item.Contains("ZMEDIALOCALPATH"))
                {
                    var extensions = new Dictionary<string, int>(StringComparer.Ordinal);
                    var forms = new Dictionary<string, int>(StringComparer.Ordinal);
                    using var paths = db.CreateCommand();
                    paths.CommandText = "SELECT ZMEDIALOCALPATH FROM ZWAMEDIAITEM WHERE ZMEDIALOCALPATH IS NOT NULL AND ZMEDIALOCALPATH <> ''";
                    using var rows = paths.ExecuteReader();
                    var found = 0;
                    while (rows.Read())
                    {
                        var path = rows.GetString(0);
                        Count(extensions, Path.GetExtension(path).ToLowerInvariant() is { Length: > 0 and < 8 } ext ? ext : "(other)");
                        Count(forms, path.StartsWith("Media/", StringComparison.Ordinal) ? "Media/…" : path.StartsWith("Message/", StringComparison.Ordinal) ? "Message/…" : path.StartsWith('/') ? "absolute" : "other");
                        found += WhatsAppStore.BackupPathOf(path) is { } inside && archive.Find(domain, inside) is { IsDirectory: false } ? 1 : 0;
                    }
                    Console.WriteLine($"  media paths: {Histogram(forms)}; in the backup {found}; extensions {Histogram(extensions)}");
                }
            }

            if (message.Contains("ZGROUPEVENTTYPE"))
                Console.WriteLine($"  ZGROUPEVENTTYPE of type-6 rows: {Grouped(db, "SELECT ZGROUPEVENTTYPE, COUNT(*) FROM ZWAMESSAGE WHERE ZMESSAGETYPE = 6 GROUP BY 1 ORDER BY 2 DESC")}");
            if (item.Contains("ZMETADATA"))
            {
                int blobs = 0, parsed = 0, matched = 0, firstTags = 0;
                var tags = new Dictionary<string, int>(StringComparer.Ordinal);
                using var command = db.CreateCommand();
                command.CommandText = "SELECT ZMETADATA FROM ZWAMEDIAITEM WHERE ZMETADATA IS NOT NULL";
                using var reader = command.ExecuteReader();
                var ids = new List<string>();
                while (reader.Read())
                {
                    if (reader.GetValue(0) is not byte[] blob || blob.Length == 0)
                        continue;
                    blobs++;
                    Count(tags, $"0x{blob[0]:X2}");
                    firstTags += blob[0] == 0x2A ? 1 : 0;
                    if (WhatsAppStore.QuotedId(blob) is { } id)
                    {
                        parsed++;
                        if (ids.Count < 2000)
                            ids.Add(id);
                    }
                }
                if (message.Contains("ZSTANZAID"))
                {
                    foreach (var id in ids)
                    {
                        using var find = db.CreateCommand();
                        find.CommandText = "SELECT 1 FROM ZWAMESSAGE WHERE ZSTANZAID = $id OR substr(ZSTANZAID, 1, length($id)) = $id LIMIT 1";
                        find.Parameters.AddWithValue("$id", id);
                        matched += find.ExecuteScalar() is not null ? 1 : 0;
                    }
                }
                Console.WriteLine($"  ZMETADATA blobs {blobs} (first byte {Histogram(tags)}); a quoted id read from {parsed}; of the first {ids.Count}, found in ZSTANZAID {matched}");
            }

            if (message.Contains("ZMESSAGEDATE"))
                Console.WriteLine($"  ZMESSAGEDATE: storage {Grouped(db, "SELECT typeof(ZMESSAGEDATE), COUNT(*) FROM ZWAMESSAGE GROUP BY 1")}, " +
                                  $"range {Scalar(db, "SELECT CAST(MIN(ZMESSAGEDATE) AS INTEGER) FROM ZWAMESSAGE WHERE ZMESSAGEDATE > 0")}..{Scalar(db, "SELECT CAST(MAX(ZMESSAGEDATE) AS INTEGER) FROM ZWAMESSAGE")} s since 2001");
            if (Columns(db, "ZWACHATSESSION").Contains("ZSESSIONTYPE"))
                Console.WriteLine($"  ZSESSIONTYPE: {Grouped(db, "SELECT ZSESSIONTYPE, COUNT(*) FROM ZWACHATSESSION GROUP BY 1 ORDER BY 2 DESC")}; " +
                                  $"address kinds {Grouped(db, "SELECT CASE WHEN ZCONTACTJID LIKE '%@g.us' THEN 'group' WHEN ZCONTACTJID LIKE '%@s.whatsapp.net' THEN 'phone' WHEN ZCONTACTJID LIKE '%@lid' THEN 'lid' WHEN ZCONTACTJID LIKE '%broadcast' THEN 'broadcast' ELSE 'other' END, COUNT(*) FROM ZWACHATSESSION GROUP BY 1 ORDER BY 2 DESC")}");
        }
    }

    private static HashSet<string> Columns(SqliteConnection db, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static string Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "-";
    }

    private static string Grouped(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var parts = new List<string>();
        while (reader.Read())
            parts.Add($"{Value(reader, 0)}={Value(reader, 1)}");
        return string.Join(", ", parts);
    }

    private static string Value(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? "null" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;

    private static void Count(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static string Histogram(Dictionary<string, int> counts) =>
        counts.Count == 0 ? "-" : string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));

    private static string Histogram(IEnumerable<string> keys)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
            Count(counts, key);
        return Histogram(counts);
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
