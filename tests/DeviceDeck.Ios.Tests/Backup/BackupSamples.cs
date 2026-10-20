using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests.Backup;

/// <summary>Sample databases in the iPhone's own schemas (the columns DeviceDeck reads, and a few it ignores).</summary>
internal static class BackupSamples
{
    public static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, .. "JFIF"u8, 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xD9];
    public static readonly byte[] Heic = [0x00, 0x00, 0x00, 0x18, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1heic"u8];

    /// <summary>The usual content: contacts with a photo, three conversations with attachments, an app with documents.</summary>
    public static BackupFixtureBuilder AddStandardContent(this BackupFixtureBuilder b)
    {
        b.AppNames["net.whatsapp.WhatsApp"] = "WhatsApp";
        b.AppNames["com.apple.Pages"] = "Pages";
        return b
            .Dir("HomeDomain", "Library")
            .Dir("HomeDomain", "Library/AddressBook")
            .File("HomeDomain", "Library/AddressBook/AddressBook.sqlitedb", AddressBook())
            .File("HomeDomain", "Library/AddressBook/AddressBookImages.sqlitedb", AddressBookImages())
            .File("HomeDomain", "Library/SMS/sms.db", Messages())
            .Text("HomeDomain", "Library/Preferences/com.apple.springboard.plist", "<plist/>")
            .File("MediaDomain", "Library/SMS/Attachments/aa/10/A1/IMG_0001.JPG", Jpeg)
            .File("MediaDomain", "Library/SMS/Attachments/bb/11/B2/IMG_0002.HEIC", Heic)
            .File("CameraRollDomain", "Media/DCIM/100APPLE/IMG_0100.HEIC", Heic)
            .Dir("AppDomain-net.whatsapp.WhatsApp", "Documents")
            .Text("AppDomain-net.whatsapp.WhatsApp", "Documents/notes.txt", "hello from whatsapp")
            .Text("AppDomain-net.whatsapp.WhatsApp", "Documents/Sub folder/deep.txt", "deep")
            .Text("AppDomainGroup-group.net.whatsapp.WhatsApp.shared", "ChatStorage.sqlite", "chat storage")
            .Text("AppDomainPlugin-net.whatsapp.WhatsApp.Intents", "Library/Preferences/x.plist", "plugin")
            .Text("AppDomain-com.apple.Pages", "Documents/Report.pages", "pages");
    }

    public static byte[] AddressBook() => Database(db =>
    {
        BackupFixtureBuilder.Exec(db, """
            CREATE TABLE ABPerson (ROWID INTEGER PRIMARY KEY AUTOINCREMENT, First TEXT, Last TEXT, Middle TEXT, FirstPhonetic TEXT,
              Organization TEXT, Department TEXT, Note TEXT, Kind INTEGER, Birthday TEXT, JobTitle TEXT, Nickname TEXT, Prefix TEXT,
              Suffix TEXT, CreationDate INTEGER, ModificationDate INTEGER, DisplayName TEXT);
            CREATE TABLE ABMultiValue (UID INTEGER PRIMARY KEY, record_id INTEGER, property INTEGER, identifier INTEGER, label INTEGER, value TEXT);
            CREATE TABLE ABMultiValueLabel (value TEXT);
            CREATE TABLE ABMultiValueEntry (parent_id INTEGER, key INTEGER, value TEXT);
            CREATE TABLE ABMultiValueEntryKey (value TEXT);
            INSERT INTO ABMultiValueLabel (ROWID, value) VALUES (1, '_$!<Mobile>!$_'), (2, '_$!<Home>!$_'), (3, '_$!<Work>!$_'), (4, 'iPhone'), (5, 'Gym'), (6, '_$!<Main>!$_');
            INSERT INTO ABMultiValueEntryKey (ROWID, value) VALUES (1, 'Street'), (2, 'City'), (3, 'State'), (4, 'ZIP'), (5, 'Country'), (6, 'CountryCode');
            """);
        Person(db, 1, "سارا", "احمدی", birthday: Seconds(new DateTime(1990, 5, 1, 12, 0, 0, DateTimeKind.Utc)));
        Value(db, 10, 1, 3, 1, "+98 912 345 6789");
        Value(db, 11, 1, 4, 2, "sara@example.com");
        Value(db, 12, 1, 5, 2, null);
        BackupFixtureBuilder.Exec(db, "INSERT INTO ABMultiValueEntry VALUES (12, 1, 'خیابان ولیعصر ۱۲'), (12, 2, 'تهران'), (12, 4, '1415'), (12, 5, 'Iran')");

        Person(db, 2, "John", "Appleseed", organization: "Apple", jobTitle: "Engineer", note: "Line one\nLine two; with, commas");
        Value(db, 20, 2, 3, 4, "+1 (555) 010-2030");
        Value(db, 21, 2, 3, 5, "+1 555 999 0000");
        Value(db, 22, 2, 4, 3, "john@apple.example");
        Value(db, 23, 2, 22, null, "https://example.com/john");

        Person(db, 3, null, null, organization: "Pizza Place");
        Value(db, 30, 3, 3, 6, "12345");

        Person(db, 4, "Leila", null, birthday: Seconds(new DateTime(1604, 3, 21, 12, 0, 0, DateTimeKind.Utc)));
        Value(db, 40, 4, 4, null, "leila@example.org");

        // Someone with nothing to show is left out.
        Person(db, 5, null, null);
    });

    public static byte[] AddressBookImages() => Database(db =>
    {
        BackupFixtureBuilder.Exec(db, """
            CREATE TABLE ABFullSizeImage (ROWID INTEGER PRIMARY KEY, record_id INTEGER, crop_x INTEGER, crop_y INTEGER, crop_width INTEGER, data BLOB);
            CREATE TABLE ABThumbnailImage (ROWID INTEGER PRIMARY KEY, record_id INTEGER, format INTEGER, derived_from_format INTEGER, data BLOB);
            """);
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO ABFullSizeImage (record_id, data) VALUES (1, $data)";
        insert.Parameters.AddWithValue("$data", Jpeg);
        insert.ExecuteNonQuery();
    });

    public static byte[] Messages() => Database(db =>
    {
        BackupFixtureBuilder.Exec(db, """
            CREATE TABLE handle (ROWID INTEGER PRIMARY KEY AUTOINCREMENT UNIQUE, id TEXT NOT NULL, country TEXT, service TEXT NOT NULL, uncanonicalized_id TEXT);
            CREATE TABLE chat (ROWID INTEGER PRIMARY KEY AUTOINCREMENT, guid TEXT UNIQUE NOT NULL, style INTEGER, state INTEGER, chat_identifier TEXT,
              service_name TEXT, room_name TEXT, display_name TEXT);
            CREATE TABLE message (ROWID INTEGER PRIMARY KEY AUTOINCREMENT, guid TEXT UNIQUE NOT NULL, text TEXT, handle_id INTEGER DEFAULT 0,
              service TEXT, date INTEGER, date_read INTEGER, is_from_me INTEGER DEFAULT 0, cache_has_attachments INTEGER DEFAULT 0,
              item_type INTEGER DEFAULT 0, group_title TEXT, associated_message_guid TEXT, associated_message_type INTEGER DEFAULT 0, attributedBody BLOB);
            CREATE TABLE chat_message_join (chat_id INTEGER, message_id INTEGER, message_date INTEGER DEFAULT 0, PRIMARY KEY (chat_id, message_id));
            CREATE TABLE chat_handle_join (chat_id INTEGER, handle_id INTEGER, UNIQUE(chat_id, handle_id));
            CREATE TABLE attachment (ROWID INTEGER PRIMARY KEY AUTOINCREMENT, guid TEXT UNIQUE NOT NULL, filename TEXT, uti TEXT, mime_type TEXT,
              transfer_name TEXT, total_bytes INTEGER DEFAULT 0);
            CREATE TABLE message_attachment_join (message_id INTEGER, attachment_id INTEGER, UNIQUE(message_id, attachment_id));
            INSERT INTO handle (ROWID, id, service) VALUES (1, '+989123456789', 'iMessage'), (2, 'john@apple.example', 'iMessage'), (3, '12345', 'SMS');
            INSERT INTO chat (ROWID, guid, style, chat_identifier, service_name, display_name) VALUES
              (1, 'iMessage;-;+989123456789', 45, '+989123456789', 'iMessage', ''),
              (2, 'iMessage;+;chat123', 43, 'chat123', 'iMessage', 'Family'),
              (3, 'SMS;-;12345', 45, '12345', 'SMS', NULL),
              (4, 'iMessage;-;nobody', 45, 'nobody@example.com', 'iMessage', NULL);
            INSERT INTO chat_handle_join VALUES (1, 1), (2, 1), (2, 2), (3, 3);
            INSERT INTO attachment (ROWID, guid, filename, mime_type, transfer_name, total_bytes) VALUES
              (1, 'A1', '~/Library/SMS/Attachments/aa/10/A1/IMG_0001.JPG', 'image/jpeg', 'IMG_0001.JPG', 22),
              (2, 'B2', '/var/mobile/Library/SMS/Attachments/bb/11/B2/IMG_0002.HEIC', 'image/heic', 'IMG_0002.HEIC', 24),
              (3, 'C3', '~/Library/SMS/Attachments/cc/12/C3/clip.mov', 'video/quicktime', 'clip.mov', 4096);
            """);
        var t0 = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        Message(db, 1, 1, "سلام! خوبی؟", handle: 1, fromMe: false, t0);
        Message(db, 2, 1, "Hi Sara 👋", handle: 0, fromMe: true, t0.AddMinutes(1));
        Message(db, 3, 1, "Loved “Hi Sara 👋”", handle: 1, fromMe: false, t0.AddMinutes(2), reaction: 2000);
        Message(db, 4, 1, null, handle: 1, fromMe: false, t0.AddMinutes(3), body: TypedStream("Text only in attributedBody"));
        Message(db, 5, 1, "￼Photos", handle: 0, fromMe: true, t0.AddDays(1));
        BackupFixtureBuilder.Exec(db, "INSERT INTO message_attachment_join VALUES (5, 1), (5, 2), (5, 3)");
        Message(db, 6, 2, null, handle: 2, fromMe: false, t0.AddHours(2), itemType: 2, groupTitle: "Family");
        Message(db, 7, 2, "Dinner at 8?", handle: 2, fromMe: false, t0.AddHours(3));
        Message(db, 8, 2, "Yes!", handle: 1, fromMe: false, t0.AddHours(4));
        Message(db, 9, 3, "Your code is 4242", handle: 3, fromMe: false, t0.AddDays(-2), service: "SMS");
        // Old seconds-since-2001 dates are read too.
        Message(db, 10, 3, "Old message", handle: 3, fromMe: false, null, service: "SMS", rawDate: 500_000_000);
    });

    /// <summary>An NSAttributedString archived by NSArchiver, as newer iOS stores message text.</summary>
    public static byte[] TypedStream(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        byte[] length = utf8.Length < 0x80 ? [(byte)utf8.Length] : [0x81, (byte)utf8.Length, (byte)(utf8.Length >> 8)];
        return [0x04, 0x0B, .. "streamtyped"u8, 0x81, 0xE8, 0x03, 0x84, 0x01, 0x40, 0x84, 0x84, 0x84, 0x12, .. "NSAttributedString"u8, 0x00,
            0x84, 0x84, 0x08, .. "NSObject"u8, 0x00, 0x85, 0x92, 0x84, 0x84, 0x84, 0x08, .. "NSString"u8, 0x01, 0x94, 0x84, 0x01, 0x2B,
            .. length, .. utf8, 0x86, 0x84, 0x02, 0x69, 0x49, 0x01];
    }

    private static void Person(SqliteConnection db, long id, string? first, string? last, string? organization = null, string? jobTitle = null,
        string? note = null, string? birthday = null)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO ABPerson (ROWID, First, Last, Organization, JobTitle, Note, Birthday, ModificationDate)
            VALUES ($id, $first, $last, $org, $job, $note, $birthday, 780000000)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$first", (object?)first ?? DBNull.Value);
        insert.Parameters.AddWithValue("$last", (object?)last ?? DBNull.Value);
        insert.Parameters.AddWithValue("$org", (object?)organization ?? DBNull.Value);
        insert.Parameters.AddWithValue("$job", (object?)jobTitle ?? DBNull.Value);
        insert.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        insert.Parameters.AddWithValue("$birthday", (object?)birthday ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private static void Value(SqliteConnection db, long uid, long person, int property, long? label, string? value)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO ABMultiValue VALUES ($uid, $person, $property, $uid, $label, $value)";
        insert.Parameters.AddWithValue("$uid", uid);
        insert.Parameters.AddWithValue("$person", person);
        insert.Parameters.AddWithValue("$property", property);
        insert.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
        insert.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        insert.ExecuteNonQuery();
    }

    private static void Message(SqliteConnection db, long id, long chat, string? text, long handle, bool fromMe, DateTime? when, int reaction = 0,
        byte[]? body = null, int itemType = 0, string? groupTitle = null, string service = "iMessage", long? rawDate = null)
    {
        using var insert = db.CreateCommand();
        insert.CommandText = """
            INSERT INTO message (ROWID, guid, text, handle_id, service, date, is_from_me, item_type, group_title, associated_message_type, attributedBody)
            VALUES ($id, $guid, $text, $handle, $service, $date, $me, $item, $title, $reaction, $body)
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$guid", "M" + id.ToString(CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
        insert.Parameters.AddWithValue("$handle", handle);
        insert.Parameters.AddWithValue("$service", service);
        insert.Parameters.AddWithValue("$date", rawDate ?? (when is { } w ? (long)(w - Epoch2001.UtcDateTime).TotalSeconds * 1_000_000_000L : 0));
        insert.Parameters.AddWithValue("$me", fromMe ? 1 : 0);
        insert.Parameters.AddWithValue("$item", itemType);
        insert.Parameters.AddWithValue("$title", (object?)groupTitle ?? DBNull.Value);
        insert.Parameters.AddWithValue("$reaction", reaction);
        insert.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        insert.ExecuteNonQuery();
        BackupFixtureBuilder.Exec(db, $"INSERT INTO chat_message_join (chat_id, message_id) VALUES ({chat}, {id})");
    }

    private static string Seconds(DateTime utc) => (utc - Epoch2001.UtcDateTime).TotalSeconds.ToString(CultureInfo.InvariantCulture);

    private static byte[] Database(Action<SqliteConnection> fill)
    {
        var path = Path.Combine(Path.GetTempPath(), "devicedeck-sample-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                fill(db);
            }
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
