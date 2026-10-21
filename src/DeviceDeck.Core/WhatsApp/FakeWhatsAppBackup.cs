using System.Globalization;
using System.Text;
using DeviceDeck.Core.Fake;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.WhatsApp;

/// <summary>
/// WhatsApp in the fake iPhone's backups (when the fake phone has WhatsApp): made-up chats in WhatsApp's Core Data
/// layout, with photos, a voice note, a document, a reply, a location and a group, named through the fake address book.
/// </summary>
internal static class FakeWhatsAppBackup
{
    private const string Domain = WhatsAppStore.WhatsAppDomain;
    private static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Sara = "989123456789@s.whatsapp.net";
    private const string Ali = "989351112233@s.whatsapp.net";
    private const string Maryam = "989174445566@s.whatsapp.net";
    private const string Neda = "989120001122@s.whatsapp.net";
    private const string Anna = "4915123456789@s.whatsapp.net";
    private const string John = "14085550199@s.whatsapp.net";
    private const string Family = "120363000000000001@g.us";
    private const string Team = "120363000000000002@g.us";

    public static IEnumerable<FakeBackupContent.BackupFile> For(FakeDevice device, DateTimeOffset when)
    {
        if (!device.Spec.Apps.Any(a => a.Info.Id == WhatsAppStore.WhatsAppBundle))
        {
            yield break;
        }

        var start = when.AddDays(-12);
        yield return new(Domain, WhatsAppStore.DatabasePath, ChatStorage(start, out var media), when);
        foreach (var (path, content, at) in media)
        {
            yield return new(Domain, "Message/" + path, content, at);
        }
    }

    private static byte[] ChatStorage(DateTimeOffset start, out List<(string Path, byte[] Content, DateTimeOffset At)> media)
    {
        var files = new List<(string, byte[], DateTimeOffset)>();
        var bytes = Database(db =>
        {
            Exec(db, """
                CREATE TABLE ZWACHATSESSION ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZARCHIVED INTEGER, ZSESSIONTYPE INTEGER,
                  ZLASTMESSAGEDATE TIMESTAMP, ZCONTACTJID VARCHAR, ZPARTNERNAME VARCHAR );
                CREATE TABLE ZWAMESSAGE ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZISFROMME INTEGER, ZMESSAGETYPE INTEGER, ZSTARRED INTEGER,
                  ZGROUPEVENTTYPE INTEGER, ZCHATSESSION INTEGER, ZGROUPMEMBER INTEGER, ZMEDIAITEM INTEGER, ZMESSAGEDATE TIMESTAMP, ZFROMJID VARCHAR,
                  ZPUSHNAME VARCHAR, ZSTANZAID VARCHAR, ZTEXT VARCHAR );
                CREATE TABLE ZWAMEDIAITEM ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZFILESIZE INTEGER, ZMOVIEDURATION INTEGER, ZMESSAGE INTEGER,
                  ZLATITUDE FLOAT, ZLONGITUDE FLOAT, ZMEDIALOCALPATH VARCHAR, ZTITLE VARCHAR, ZVCARDNAME VARCHAR, ZVCARDSTRING VARCHAR,
                  ZXMPPTHUMBPATH VARCHAR, ZMETADATA BLOB );
                CREATE TABLE ZWAGROUPMEMBER ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZCHATSESSION INTEGER, ZCONTACTNAME VARCHAR,
                  ZFIRSTNAME VARCHAR, ZMEMBERJID VARCHAR );
                CREATE TABLE ZWAPROFILEPUSHNAME ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZJID VARCHAR, ZPUSHNAME VARCHAR );
                """);
            Run(db, "INSERT INTO ZWACHATSESSION (Z_PK, ZSESSIONTYPE, ZARCHIVED, ZCONTACTJID, ZPARTNERNAME) VALUES ($pk, $type, $archived, $jid, $name)",
                [("$pk", 1), ("$type", 0), ("$archived", 0), ("$jid", Sara), ("$name", null)]);
            Run(db, "INSERT INTO ZWACHATSESSION (Z_PK, ZSESSIONTYPE, ZARCHIVED, ZCONTACTJID, ZPARTNERNAME) VALUES ($pk, $type, $archived, $jid, $name)",
                [("$pk", 2), ("$type", 1), ("$archived", 0), ("$jid", Family), ("$name", "خانواده 🏡")]);
            Run(db, "INSERT INTO ZWACHATSESSION (Z_PK, ZSESSIONTYPE, ZARCHIVED, ZCONTACTJID, ZPARTNERNAME) VALUES ($pk, $type, $archived, $jid, $name)",
                [("$pk", 3), ("$type", 0), ("$archived", 0), ("$jid", Anna), ("$name", null)]);
            Run(db, "INSERT INTO ZWACHATSESSION (Z_PK, ZSESSIONTYPE, ZARCHIVED, ZCONTACTJID, ZPARTNERNAME) VALUES ($pk, $type, $archived, $jid, $name)",
                [("$pk", 4), ("$type", 1), ("$archived", 0), ("$jid", Team), ("$name", "DeviceDeck testers")]);
            Run(db, "INSERT INTO ZWACHATSESSION (Z_PK, ZSESSIONTYPE, ZARCHIVED, ZCONTACTJID, ZPARTNERNAME) VALUES ($pk, $type, $archived, $jid, $name)",
                [("$pk", 5), ("$type", 0), ("$archived", 1), ("$jid", John), ("$name", null)]);
            Exec(db, $"""
                INSERT INTO ZWAGROUPMEMBER (Z_PK, ZCHATSESSION, ZMEMBERJID) VALUES (1, 2, '{Ali}'), (2, 2, '{Maryam}'), (3, 2, '{Neda}'),
                  (4, 4, '{Anna}'), (5, 4, '{John}');
                INSERT INTO ZWAPROFILEPUSHNAME (ZJID, ZPUSHNAME) VALUES ('{Neda}', 'Neda ✨');
                """);

            var pk = 0;
            var mediaPk = 0;
            var at = start;
            long Say(int chat, bool fromMe, string? text, string? from = null, int? member = null, int type = 0, int? mediaItem = null, bool starred = false, int minutes = 7)
            {
                at = at.AddMinutes(minutes);
                pk++;
                Run(db, """
                    INSERT INTO ZWAMESSAGE (Z_PK, ZCHATSESSION, ZISFROMME, ZMESSAGETYPE, ZTEXT, ZFROMJID, ZGROUPMEMBER, ZMEDIAITEM, ZSTARRED, ZMESSAGEDATE, ZSTANZAID, ZGROUPEVENTTYPE)
                    VALUES ($pk, $chat, $me, $type, $text, $from, $member, $media, $starred, $date, $stanza, $event)
                    """,
                    [("$pk", pk), ("$chat", chat), ("$me", fromMe ? 1 : 0), ("$type", type), ("$text", text), ("$from", from), ("$member", member),
                     ("$media", mediaItem), ("$starred", starred ? 1 : 0), ("$date", (at - Epoch2001).TotalSeconds), ("$stanza", "3EB0" + pk.ToString("X8", CultureInfo.InvariantCulture)),
                     ("$event", type == 6 ? 1 : 0)]);
                return pk;
            }

            int Media(string? path, byte[]? content, string? title = null, int? duration = null, double? lat = null, double? lon = null, string? mime = null,
                string? thumb = null, byte[]? thumbContent = null, byte[]? metadata = null)
            {
                mediaPk++;
                Run(db, """
                    INSERT INTO ZWAMEDIAITEM (Z_PK, ZMESSAGE, ZMEDIALOCALPATH, ZFILESIZE, ZTITLE, ZMOVIEDURATION, ZLATITUDE, ZLONGITUDE, ZVCARDSTRING, ZXMPPTHUMBPATH, ZMETADATA)
                    VALUES ($pk, $message, $path, $size, $title, $duration, $lat, $lon, $mime, $thumb, $metadata)
                    """,
                    [("$pk", mediaPk), ("$message", pk + 1), ("$path", path), ("$size", content?.Length ?? 0), ("$title", title), ("$duration", duration),
                     ("$lat", lat ?? 0.0), ("$lon", lon ?? 0.0), ("$mime", mime), ("$thumb", thumb), ("$metadata", metadata)]);
                if (path is not null && content is not null)
                {
                    files.Add((path, content, at));
                }

                if (thumb is not null && thumbContent is not null)
                {
                    files.Add((thumb, thumbContent, at));
                }

                return mediaPk;
            }

            static byte[] Quote(long message) => [0x2A, 12, .. Encoding.ASCII.GetBytes("3EB0" + message.ToString("X8", CultureInfo.InvariantCulture))];

            // Sara
            Say(1, false, "سلام! خوبی؟ 🌷", Sara);
            var asked = Say(1, true, "سلام سارا، مرسی. تو چطوری؟");
            Say(1, false, "عکس‌های سفر رو فرستادم", Sara);
            for (var i = 1; i <= 3; i++)
            {
                var name = $"Media/{Sara}/{i}/a/{i}a{i}b-trip-{i}.jpg";
                Say(1, false, i == 1 ? "دریاچه 😍" : null, Sara, type: 1, mediaItem: Media(name, FakeArt.Photo("wa-trip-" + i, 1024, 768, i == 2), mime: "image/jpeg",
                    thumb: Path.ChangeExtension(name, ".thumb"), thumbContent: FakeArt.Photo("wa-trip-" + i, 160, 120, i == 2)), minutes: 1);
            }

            Say(1, false, null, Sara, type: 3, mediaItem: Media($"Media/{Sara}/7/c/7c2d-voice.opus", FakeOpus(), duration: 9));
            Say(1, true, "خیلی قشنگه! کجاست؟", mediaItem: Media(null, null, metadata: Quote(asked)));
            Say(1, false, null, Sara, type: 5, mediaItem: Media(null, null, title: "Chitgar Lake", lat: 35.7456, lon: 51.2069));
            Say(1, true, "فردا ساعت ۵ همون‌جا؟", starred: true);
            Say(1, false, "قبول 👍", Sara);

            // The family group
            Say(2, false, "خانواده 🏡", Family, type: 6, minutes: 60);
            Say(2, false, "شام جمعه خونهٔ ما", Family, member: 1);
            Say(2, false, "عالیه، من کیک میارم 🎂", Family, member: 2);
            Say(2, true, "منم هستم");
            Say(2, false, "سلام به همه!", Family, member: 3);
            Say(2, false, null, Family, member: 2, type: 1, mediaItem: Media($"Media/{Family}/2/b/2b3c-cake.jpg", FakeArt.Photo("wa-cake", 900, 900, false), mime: "image/jpeg"));
            Say(2, false, null, Family, type: 14, minutes: 3);

            // Anna
            Say(3, false, "Hi! Are the slides ready for Monday?", Anna, minutes: 300);
            Say(3, true, "Almost — sending them tonight.");
            Say(3, true, null, type: 8, mediaItem: Media($"Media/{Anna}/5/e/5e6f-slides.pdf", FakePdf(), title: "Quarterly review.pdf", mime: "application/pdf"));
            Say(3, false, "Perfect, thanks! 🙏", Anna);
            Say(3, false, null, Anna, type: 8, mediaItem: Media($"Media/{Anna}/6/f/6f70-budget.xlsx", null, title: "Budget 2027.xlsx",
                mime: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

            // DeviceDeck testers
            Say(4, true, "DeviceDeck testers", type: 6, minutes: 200);
            Say(4, false, "Round 4 build is up 🚀", Team, member: 4);
            Say(4, false, "WhatsApp export works on my backup", Team, member: 5);
            Say(4, true, "Great — try the media export too");

            // John, archived
            at = start.AddDays(-200);
            Say(5, false, "Happy new year!", John);
            Say(5, true, "You too! 🎉");
        });
        media = files;
        return bytes;
    }

    /// <summary>Bytes that start like an Ogg Opus file; not playable, enough for the file paths.</summary>
    private static byte[] FakeOpus() => [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0x44, 0x44, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 19, .. "OpusHead"u8, 1, 1, 0x38, 1, 0x80, 0xBB, 0, 0, 0, 0, 0];

    private static byte[] FakePdf() => Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [] /Count 0 >> endobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");

    private static void Exec(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Run(SqliteConnection db, string sql, (string Name, object? Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    private static byte[] Database(Action<SqliteConnection> fill)
    {
        var path = Path.Combine(Path.GetTempPath(), "devicedeck-fake-wa-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                Exec(db, "PRAGMA journal_mode=WAL;");
                fill(db);
            }

            return File.ReadAllBytes(path);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                File.Delete(file);
            }
        }
    }
}
