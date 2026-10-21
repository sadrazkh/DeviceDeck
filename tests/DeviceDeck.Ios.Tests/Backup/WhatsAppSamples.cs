using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests.Backup;

/// <summary>
/// Synthetic WhatsApp data in the shape of WhatsApp's Core Data store (ChatStorage.sqlite, WAL mode as on the phone),
/// its address book copy (ContactsV2.sqlite) and media under Message/Media. Made up here; no real chat is used.
/// </summary>
internal static class WhatsAppSamples
{
    public const string Domain = "AppDomainGroup-group.net.whatsapp.WhatsApp.shared";
    public const string BusinessDomain = "AppDomainGroup-group.net.whatsapp.WhatsAppSMB.shared";
    public const string SaraJid = "989123456789@s.whatsapp.net";
    public const string GroupJid = "120363012345678901@g.us";
    public const string QuotedStanza = "3EB0A1B2C3D4E5F6A7B8";

    public static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    public static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8, 0x00, 9, 9, 9, 9, 0xFF, 0xD9];
    public static readonly byte[] Thumb = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8, 0x00, 7, 7, 0xFF, 0xD9];
    public static readonly byte[] Opus = [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, .. "OpusHead"u8, 1, 1, 0x38, 1];
    public static readonly byte[] Webp = [.. "RIFF"u8, 20, 0, 0, 0, .. "WEBP"u8, .. "VP8L"u8, 8, 0, 0, 0, 0x2F, 0, 0, 0, 0, 0, 0, 0];
    public static readonly byte[] Mp4 = [0, 0, 0, 0x18, .. "ftypmp42"u8, 0, 0, 0, 0, .. "isommp42"u8, 1, 2, 3];

    private const string SaraMedia = "Media/" + SaraJid;
    private const string GroupMedia = "Media/" + GroupJid;

    /// <summary>WhatsApp's chats, media (one file offloaded) and its address book copy.</summary>
    public static BackupFixtureBuilder AddWhatsApp(this BackupFixtureBuilder b) => b
        .File(Domain, "ChatStorage.sqlite", ChatStorage())
        .File(Domain, "ContactsV2.sqlite", ContactsV2())
        .Dir(Domain, "Message")
        .Dir(Domain, "Message/Media")
        .File(Domain, $"Message/{SaraMedia}/4/a/4a1b-photo.jpg", Photo, modified: new DateTimeOffset(2026, 10, 1, 9, 2, 0, TimeSpan.Zero))
        .File(Domain, $"Message/{SaraMedia}/4/a/4a1b-photo.thumb", Thumb)
        .File(Domain, $"Message/{SaraMedia}/7/c/7c2d-voice.opus", Opus)
        .File(Domain, $"Message/{GroupMedia}/1/e/1e3f-sticker.webp", Webp)
        .File(Domain, $"Message/{GroupMedia}/2/f/2f40-clip.mp4", Mp4)
        .File(Domain, $"Message/{GroupMedia}/2/f/2f40-clip.thumb", Thumb);

    /// <summary>WhatsApp Business next to WhatsApp, as on a phone with both.</summary>
    public static BackupFixtureBuilder AddWhatsAppBusiness(this BackupFixtureBuilder b)
    {
        b.AppNames["net.whatsapp.WhatsAppSMB"] = "WA Business";
        return b.File(BusinessDomain, "ChatStorage.sqlite", BusinessChatStorage());
    }

    public const string Schema = """
        CREATE TABLE ZWACHATSESSION ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZARCHIVED INTEGER, ZCONTACTABID INTEGER, ZFLAGS INTEGER,
          ZHIDDEN INTEGER, ZMESSAGECOUNTER INTEGER, ZREMOVED INTEGER, ZSESSIONTYPE INTEGER, ZSPOTLIGHTSTATUS INTEGER, ZUNREADCOUNT INTEGER,
          ZGROUPINFO INTEGER, ZLASTMESSAGE INTEGER, ZPROPERTIES INTEGER, ZLASTMESSAGEDATE TIMESTAMP, ZLOCATIONSHARINGENDDATE TIMESTAMP,
          ZCONTACTIDENTIFIER VARCHAR, ZCONTACTJID VARCHAR, ZETAG VARCHAR, ZLASTMESSAGETEXT VARCHAR, ZPARTNERNAME VARCHAR, ZSAVEDINPUT VARCHAR );
        CREATE TABLE ZWAMESSAGE ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZCHILDMESSAGESDELIVEREDCOUNT INTEGER, ZDOCID INTEGER,
          ZENCRETRYCOUNT INTEGER, ZFLAGS INTEGER, ZGROUPEVENTTYPE INTEGER, ZISFROMME INTEGER, ZMESSAGEERRORSTATUS INTEGER, ZMESSAGESTATUS INTEGER,
          ZMESSAGETYPE INTEGER, ZSORT INTEGER, ZSPOTLIGHTSTATUS INTEGER, ZSTARRED INTEGER, ZCHATSESSION INTEGER, ZGROUPMEMBER INTEGER,
          ZLASTSESSION INTEGER, ZMEDIAITEM INTEGER, ZMESSAGEINFO INTEGER, ZPARENTMESSAGE INTEGER, ZMESSAGEDATE TIMESTAMP, ZSENTDATE TIMESTAMP,
          ZFROMJID VARCHAR, ZMEDIASECTIONID VARCHAR, ZPHASH VARCHAR, ZPUSHNAME VARCHAR, ZSTANZAID VARCHAR, ZTEXT VARCHAR, ZTOJID VARCHAR );
        CREATE INDEX ZWAMESSAGE_ZCHATSESSION_INDEX ON ZWAMESSAGE (ZCHATSESSION);
        CREATE TABLE ZWAMEDIAITEM ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZCLOUDSTATUS INTEGER, ZFILESIZE INTEGER,
          ZMEDIAORIGIN INTEGER, ZMOVIEDURATION INTEGER, ZMESSAGE INTEGER, ZASPECTRATIO FLOAT, ZHACCURACY FLOAT, ZLATITUDE FLOAT, ZLONGITUDE FLOAT,
          ZMEDIAURLDATE TIMESTAMP, ZAUTHORNAME VARCHAR, ZCOLLECTIONNAME VARCHAR, ZMEDIALOCALPATH VARCHAR, ZMEDIAURL VARCHAR,
          ZTHUMBNAILLOCALPATH VARCHAR, ZTITLE VARCHAR, ZVCARDNAME VARCHAR, ZVCARDSTRING VARCHAR, ZXMPPTHUMBPATH VARCHAR, ZMEDIAKEY BLOB, ZMETADATA BLOB );
        CREATE TABLE ZWAGROUPMEMBER ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZCONTACTABID INTEGER, ZISACTIVE INTEGER, ZISADMIN INTEGER,
          ZCHATSESSION INTEGER, ZCONTACTIDENTIFIER VARCHAR, ZCONTACTNAME VARCHAR, ZFIRSTNAME VARCHAR, ZMEMBERJID VARCHAR );
        CREATE TABLE ZWAPROFILEPUSHNAME ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZJID VARCHAR, ZPUSHNAME VARCHAR );
        CREATE TABLE Z_PRIMARYKEY (Z_ENT INTEGER PRIMARY KEY, Z_NAME VARCHAR, Z_SUPER INTEGER, Z_MAX INTEGER);
        CREATE TABLE Z_METADATA (Z_VERSION INTEGER PRIMARY KEY, Z_UUID VARCHAR(255), Z_PLIST BLOB);
        """;

    /// <summary>
    /// Five chats and a status update: Sara (text, photo with caption, voice note, a reply, an offloaded document, location,
    /// a deleted message, a poll, a starred one), the family group (event, two members, sticker, video), Tom (named by
    /// WhatsApp's address book), John (archived, named by the iPhone's), an empty chat and status@broadcast.
    /// </summary>
    public static byte[] ChatStorage() => Database(Fill, wal: true);

    public static void Fill(SqliteConnection db)
    {
        Exec(db, Schema);
        Session(db, 1, SaraJid, "سارا احمدی", type: 0);
        Session(db, 2, GroupJid, "خانواده", type: 1);
        Session(db, 3, "447700900123@s.whatsapp.net", null, type: 0);
        Session(db, 4, "status@broadcast", null, type: 3);
        Session(db, 5, "15550102030@s.whatsapp.net", null, type: 0, archived: true);
        Session(db, 6, "989000000000@s.whatsapp.net", "Nobody", type: 0);
        Exec(db, $"""
            INSERT INTO ZWAGROUPMEMBER (Z_PK, ZCHATSESSION, ZMEMBERJID, ZCONTACTNAME, ZFIRSTNAME) VALUES
              (1, 2, '{SaraJid}', NULL, NULL), (2, 2, '447700900999@s.whatsapp.net', NULL, NULL);
            INSERT INTO ZWAPROFILEPUSHNAME (ZJID, ZPUSHNAME) VALUES ('447700900999@s.whatsapp.net', 'Mina'), ('447700900123@s.whatsapp.net', 'Tom');
            """);

        // Sara
        Message(db, 1, chat: 1, type: 0, T0, fromMe: false, "سلام! خوبی؟", from: SaraJid);
        Message(db, 2, chat: 1, type: 0, T0.AddMinutes(1), fromMe: true, "Hi Sara 👋", stanza: QuotedStanza);
        Message(db, 3, chat: 1, type: 1, T0.AddMinutes(2), fromMe: false, null, from: SaraJid, media: 1);
        Media(db, 1, message: 3, $"{SaraMedia}/4/a/4a1b-photo.jpg", title: "Sunset", size: Photo.Length, mime: "image/jpeg", xmppThumb: $"{SaraMedia}/4/a/4a1b-photo.thumb");
        Message(db, 4, chat: 1, type: 3, T0.AddMinutes(3), fromMe: true, null, media: 2);
        Media(db, 2, message: 4, $"{SaraMedia}/7/c/7c2d-voice.opus", size: Opus.Length, duration: 7);
        Message(db, 5, chat: 1, type: 0, T0.AddMinutes(4), fromMe: false, "Lovely!", from: SaraJid, media: 3);
        // A reply: its media item has no file, only the link to the quoted message (protobuf field 5).
        Media(db, 3, message: 5, null, metadata: [0x08, 0x01, 0x2A, (byte)QuotedStanza.Length, .. Encoding.ASCII.GetBytes(QuotedStanza), 0x32, 0x03, .. "abc"u8]);
        Message(db, 6, chat: 1, type: 8, T0.AddMinutes(5), fromMe: true, null, media: 4);
        Media(db, 4, message: 6, $"{SaraMedia}/9/9/9999-invoice.pdf", title: "Invoice 0921.pdf", size: 210_000, mime: "application/pdf");
        Message(db, 7, chat: 1, type: 5, T0.AddDays(1), fromMe: false, null, from: SaraJid, media: 5);
        Media(db, 5, message: 7, null, title: "Tehran", latitude: 35.6892, longitude: 51.389);
        Message(db, 8, chat: 1, type: 14, T0.AddDays(1).AddMinutes(1), fromMe: false, null, from: SaraJid);
        Message(db, 9, chat: 1, type: 46, T0.AddDays(1).AddMinutes(2), fromMe: false, null, from: SaraJid);
        Message(db, 10, chat: 1, type: 0, T0.AddDays(1).AddMinutes(3), fromMe: true, "Don't forget", starred: true);

        // The family group
        Message(db, 11, chat: 2, type: 6, T0.AddDays(1).AddHours(2), fromMe: false, "خانواده", from: GroupJid);
        Message(db, 12, chat: 2, type: 0, T0.AddDays(1).AddHours(3), fromMe: false, "Dinner at 8?", from: GroupJid, member: 1);
        Message(db, 13, chat: 2, type: 0, T0.AddDays(1).AddHours(4), fromMe: false, "Yes!", from: GroupJid, member: 2, pushName: "Mina M");
        Message(db, 14, chat: 2, type: 15, T0.AddDays(1).AddHours(5), fromMe: true, null, media: 6);
        Media(db, 6, message: 14, $"{GroupMedia}/1/e/1e3f-sticker.webp", size: Webp.Length, mime: "image/webp");
        Message(db, 15, chat: 2, type: 2, T0.AddDays(1).AddHours(6), fromMe: false, null, from: GroupJid, member: 2, media: 7);
        Media(db, 7, message: 15, $"{GroupMedia}/2/f/2f40-clip.mp4", size: Mp4.Length, duration: 12, mime: "video/mp4");

        // Tom, John, status
        Message(db, 16, chat: 3, type: 0, T0.AddDays(2), fromMe: false, "Hello from London", from: "447700900123@s.whatsapp.net");
        Message(db, 17, chat: 4, type: 1, T0.AddDays(2).AddHours(1), fromMe: true, null);
        Message(db, 18, chat: 5, type: 0, new DateTime(2025, 3, 1, 12, 0, 0, DateTimeKind.Utc), fromMe: false, "Old message", from: "15550102030@s.whatsapp.net");
    }

    /// <summary>WhatsApp Business: one customer chat.</summary>
    public static byte[] BusinessChatStorage() => Database(db =>
    {
        Exec(db, Schema);
        Session(db, 1, "989350000001@s.whatsapp.net", "Customer", type: 0);
        Message(db, 1, chat: 1, type: 0, T0.AddDays(3), fromMe: false, "Is the shop open?", from: "989350000001@s.whatsapp.net");
        Message(db, 2, chat: 1, type: 0, T0.AddDays(3).AddMinutes(5), fromMe: true, "Yes, until 9");
    }, wal: false);

    public static byte[] ContactsV2() => Database(db => Exec(db, """
        CREATE TABLE ZWAADDRESSBOOKCONTACT ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZFULLNAME VARCHAR, ZGIVENNAME VARCHAR,
          ZPHONENUMBER VARCHAR, ZWHATSAPPID VARCHAR, ZABOUTTEXT VARCHAR );
        INSERT INTO ZWAADDRESSBOOKCONTACT (ZFULLNAME, ZGIVENNAME, ZPHONENUMBER, ZWHATSAPPID) VALUES
          ('Thomas Green', 'Thomas', '+44 7700 900123', '447700900123@s.whatsapp.net');
        """), wal: true);

    /// <summary>
    /// A chat database whose newest message is only in its write-ahead log, as a backup taken while WhatsApp was writing
    /// would hold it: the database file and the "-wal" file next to it.
    /// </summary>
    public static (byte[] Database, byte[] Wal) ChatStorageWithLog()
    {
        var path = Path.Combine(Path.GetTempPath(), "devicedeck-wa-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Pooling=False");
            db.Open();
            Exec(db, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
            Fill(db);
            Exec(db, "PRAGMA wal_checkpoint(TRUNCATE);");
            Message(db, 19, chat: 3, type: 0, T0.AddDays(5), fromMe: true, "Only in the log", stanza: "LOG1");
            return (ReadShared(path), ReadShared(path + "-wal"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                File.Delete(file);
        }
    }

    /// <summary>An older-looking layout: no group members, push names, stars or reply links; media items point at their message.</summary>
    public static byte[] OldChatStorage() => Database(db =>
    {
        Exec(db, """
            CREATE TABLE ZWACHATSESSION ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZSESSIONTYPE INTEGER, ZCONTACTJID VARCHAR, ZPARTNERNAME VARCHAR );
            CREATE TABLE ZWAMESSAGE ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZISFROMME INTEGER, ZMESSAGETYPE INTEGER,
              ZCHATSESSION INTEGER, ZMESSAGEDATE TIMESTAMP, ZFROMJID VARCHAR, ZTEXT VARCHAR );
            CREATE TABLE ZWAMEDIAITEM ( Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZMESSAGE INTEGER, ZFILESIZE INTEGER, ZMEDIALOCALPATH VARCHAR, ZTITLE VARCHAR );
            INSERT INTO ZWACHATSESSION VALUES (1, 1, 1, 0, '989123456789@s.whatsapp.net', 'Sara');
            INSERT INTO ZWAMEDIAITEM VALUES (1, 2, 1, 2, 22, 'Media/989123456789@s.whatsapp.net/4/a/4a1b-photo.jpg', NULL);
            """);
        Exec(db, $"""
            INSERT INTO ZWAMESSAGE VALUES (1, 3, 1, 0, 0, 1, {Seconds(T0)}, '{SaraJid}', 'Old version hello');
            INSERT INTO ZWAMESSAGE VALUES (2, 3, 1, 1, 1, 1, {Seconds(T0.AddMinutes(1))}, NULL, NULL);
            """);
    }, wal: false);

    private static void Session(SqliteConnection db, long pk, string jid, string? partner, int type, bool archived = false)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO ZWACHATSESSION (Z_PK, Z_ENT, Z_OPT, ZCONTACTJID, ZPARTNERNAME, ZSESSIONTYPE, ZARCHIVED, ZREMOVED, ZHIDDEN)
            VALUES ($pk, 2, 1, $jid, $partner, $type, $archived, 0, 0)
            """;
        insert.Parameters.AddWithValue("$pk", pk);
        insert.Parameters.AddWithValue("$jid", jid);
        insert.Parameters.AddWithValue("$partner", (object?)partner ?? DBNull.Value);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    private static void Message(SqliteConnection db, long pk, long chat, int type, DateTime when, bool fromMe, string? text, string? from = null,
        long? member = null, long? media = null, string? stanza = null, bool starred = false, string? pushName = null)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO ZWAMESSAGE (Z_PK, Z_ENT, Z_OPT, ZCHATSESSION, ZMESSAGETYPE, ZMESSAGEDATE, ZSENTDATE, ZISFROMME, ZTEXT, ZFROMJID, ZGROUPMEMBER,
              ZMEDIAITEM, ZSTANZAID, ZSTARRED, ZPUSHNAME, ZGROUPEVENTTYPE)
            VALUES ($pk, 9, 1, $chat, $type, $date, $date, $me, $text, $from, $member, $media, $stanza, $starred, $push, $event)
            """;
        insert.Parameters.AddWithValue("$pk", pk);
        insert.Parameters.AddWithValue("$chat", chat);
        insert.Parameters.AddWithValue("$type", type);
        insert.Parameters.AddWithValue("$date", Seconds(when) + 0.25);
        insert.Parameters.AddWithValue("$me", fromMe ? 1 : 0);
        insert.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        insert.Parameters.AddWithValue("$from", (object?)from ?? DBNull.Value);
        insert.Parameters.AddWithValue("$member", (object?)member ?? DBNull.Value);
        insert.Parameters.AddWithValue("$media", (object?)media ?? DBNull.Value);
        insert.Parameters.AddWithValue("$stanza", (object?)stanza ?? ("S" + pk.ToString(CultureInfo.InvariantCulture)));
        insert.Parameters.AddWithValue("$starred", starred ? 1 : 0);
        insert.Parameters.AddWithValue("$push", (object?)pushName ?? DBNull.Value);
        insert.Parameters.AddWithValue("$event", type == 6 ? 1 : 0);
        insert.ExecuteNonQuery();
    }

    private static void Media(SqliteConnection db, long pk, long message, string? path, string? title = null, long size = 0, int? duration = null,
        double? latitude = null, double? longitude = null, string? mime = null, string? xmppThumb = null, byte[]? metadata = null)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO ZWAMEDIAITEM (Z_PK, Z_ENT, Z_OPT, ZMESSAGE, ZMEDIALOCALPATH, ZTITLE, ZFILESIZE, ZMOVIEDURATION, ZLATITUDE, ZLONGITUDE,
              ZVCARDSTRING, ZXMPPTHUMBPATH, ZMETADATA)
            VALUES ($pk, 8, 1, $message, $path, $title, $size, $duration, $lat, $lon, $mime, $thumb, $metadata)
            """;
        insert.Parameters.AddWithValue("$pk", pk);
        insert.Parameters.AddWithValue("$message", message);
        insert.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        insert.Parameters.AddWithValue("$size", size);
        insert.Parameters.AddWithValue("$duration", (object?)duration ?? DBNull.Value);
        insert.Parameters.AddWithValue("$lat", (object?)latitude ?? 0.0);
        insert.Parameters.AddWithValue("$lon", (object?)longitude ?? 0.0);
        insert.Parameters.AddWithValue("$mime", (object?)mime ?? DBNull.Value);
        insert.Parameters.AddWithValue("$thumb", (object?)xmppThumb ?? DBNull.Value);
        insert.Parameters.AddWithValue("$metadata", (object?)metadata ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    public static double Seconds(DateTime utc) => (utc - BackupSamples.Epoch2001.UtcDateTime).TotalSeconds;

    internal static void Exec(SqliteConnection db, string sql) => BackupFixtureBuilder.Exec(db, sql);

    /// <summary>A database file's bytes; with <paramref name="wal"/> it is in WAL mode, as the phone keeps WhatsApp's databases.</summary>
    private static byte[] Database(Action<SqliteConnection> fill, bool wal)
    {
        var path = Path.Combine(Path.GetTempPath(), "devicedeck-wa-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                if (wal)
                    Exec(db, "PRAGMA journal_mode=WAL;");
                fill(db);
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                File.Delete(file);
        }
    }

    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        file.CopyTo(memory);
        return memory.ToArray();
    }
}
