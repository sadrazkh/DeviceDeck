using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DeviceDeck.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.Fake;

/// <summary>
/// The fake iPhone's backups: an Apple-layout folder (Info/Status/Manifest.plist, Manifest.db, files named by SHA-1)
/// with sample contacts, conversations, photos and app documents, written over <see cref="FakeOptions.BackupDuration"/>
/// so progress, a pulled cable and resuming can be watched. Later runs skip files already there, like a phone's
/// incremental backup. It is never encrypted, even when the fake says encryption is on (that code lives in the
/// iPhone provider). A restore only plays its progress and the restart.
/// </summary>
public sealed partial class FakeDevice : IDeviceBackup
{
    private readonly Lock _backupGate = new();
    private string? _backupPassword;

    public Task<BackupDeviceState> GetBackupStateAsync(CancellationToken ct)
    {
        EnsureConnected();
        RequireBackup();
        lock (_backupGate)
        {
            return Task.FromResult(new BackupDeviceState(_backupPassword is not null, CloudBackupEnabled: false));
        }
    }

    public async Task BackupAsync(string folder, BackupRequest request, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        EnsureConnected();
        RequireBackup();
        progress.Report(new BackupProgress(BackupPhase.Starting, null, 0, 0));
        var target = Path.Combine(folder, Summary.Id);
        Directory.CreateDirectory(target);
        var files = FakeBackupContent.For(this);
        var step = files.Count == 0 ? TimeSpan.Zero : Options.BackupDuration / (files.Count + 4);

        Write(Path.Combine(target, "Info.plist"), FakeBackupContent.InfoPlist(this, DateTimeOffset.UtcNow));
        var snapshot = Guid.NewGuid().ToString().ToUpperInvariant();
        Write(Path.Combine(target, "Status.plist"), FakeBackupContent.StatusPlist("uploading", snapshot, DateTimeOffset.UtcNow, request.Full));
        await FakeIo.DelayAsync(this, step * 2, ct).ConfigureAwait(false);
        progress.Report(new BackupProgress(BackupPhase.Preparing, 2, 0, 0));
        await FakeIo.DelayAsync(this, step, ct).ConfigureAwait(false);

        long bytes = 0;
        var count = 0;
        var done = 0;
        foreach (var file in files)
        {
            await FakeIo.DelayAsync(this, step, ct).ConfigureAwait(false);
            var id = FakeBackupContent.FileId(file.Domain, file.Path);
            var path = Path.Combine(target, id[..2], id);
            // What the last backup already has isn't sent again.
            if (request.Full || !File.Exists(path) || new FileInfo(path).Length != file.Content.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Write(path, file.Content);
                bytes += file.Content.Length;
                count++;
            }
            done++;
            progress.Report(new BackupProgress(BackupPhase.Transferring, 2 + 95.0 * done / files.Count, bytes, count));
        }

        await FakeIo.DelayAsync(this, step, ct).ConfigureAwait(false);
        FakeBackupContent.WriteManifestDatabase(Path.Combine(target, "Manifest.db"), files);
        Write(Path.Combine(target, "Manifest.plist"), FakeBackupContent.ManifestPlist(this, DateTimeOffset.UtcNow));
        Write(Path.Combine(target, "Status.plist"), FakeBackupContent.StatusPlist("finished", snapshot, DateTimeOffset.UtcNow, request.Full));
        progress.Report(new BackupProgress(BackupPhase.Finishing, 100, bytes, count));
    }

    public async Task RestoreAsync(string backupFolder, RestoreRequest request, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        EnsureConnected();
        RequireBackup();
        if (!File.Exists(Path.Combine(backupFolder, "Manifest.db")))
        {
            throw new DeviceException("backup_corrupt", "That folder doesn't hold a complete iPhone backup.");
        }

        progress.Report(new BackupProgress(BackupPhase.Starting, null, 0, 0));
        var files = Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f).Length).ToList();
        var step = Options.BackupDuration / (files.Count + 2);
        await FakeIo.DelayAsync(this, step, ct).ConfigureAwait(false);
        progress.Report(new BackupProgress(BackupPhase.Preparing, 1, 0, 0));
        long bytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            await FakeIo.DelayAsync(this, step, ct).ConfigureAwait(false);
            bytes += files[i];
            progress.Report(new BackupProgress(BackupPhase.Transferring, 1 + 98.0 * (i + 1) / files.Count, bytes, i + 1));
        }

        progress.Report(new BackupProgress(BackupPhase.Finishing, 100, bytes, files.Count));
        if (request.Reboot)
        {
            Provider.SimulateAbsence(this, Options.RebootDelay);
        }
    }

    public async Task ChangeEncryptionAsync(string? oldPassword, string? newPassword, IProgress<BackupProgress> progress, CancellationToken ct)
    {
        EnsureConnected();
        RequireBackup();
        progress.Report(new BackupProgress(BackupPhase.Starting, null, 0, 0));
        progress.Report(new BackupProgress(BackupPhase.WaitingForPhone, null, 0, 0));
        // The time it takes to type the passcode on the phone.
        await FakeIo.DelayAsync(this, Options.BackupDuration / 3, ct).ConfigureAwait(false);
        lock (_backupGate)
        {
            if (_backupPassword is not null && oldPassword != _backupPassword)
            {
                throw new DeviceException("wrong_password", "The backup password isn't right.");
            }

            _backupPassword = string.IsNullOrEmpty(newPassword) ? null : newPassword;
        }
    }

    private void RequireBackup()
    {
        if (Summary.Platform != DevicePlatform.Ios || (Summary.Capabilities & DeviceCapability.Backup) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{Summary.Name} has no backup service of its own.");
        }
    }

    private static void Write(string path, byte[] content)
    {
        File.WriteAllBytes(path + ".ddpart", content);
        File.Move(path + ".ddpart", path, overwrite: true);
    }
}

/// <summary>What the fake iPhone's backup holds, and Apple's file formats for it.</summary>
internal static class FakeBackupContent
{
    public sealed record BackupFile(string Domain, string Path, byte[] Content, DateTimeOffset Modified);

    private static readonly DateTimeOffset Base = new(2026, 10, 12, 18, 30, 0, TimeSpan.FromHours(3.5));
    private static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static string FileId(string domain, string path) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(domain + "-" + path)));

    public static List<BackupFile> For(FakeDevice device)
    {
        var files = new List<BackupFile>
        {
            new("HomeDomain", "Library/AddressBook/AddressBook.sqlitedb", AddressBook(), Base),
            new("HomeDomain", "Library/SMS/sms.db", Messages(), Base),
            new("HomeDomain", "Library/Preferences/com.apple.springboard.plist", Plist(new Dictionary<string, object> { ["SBShowBatteryPercentage"] = true }), Base),
            new("HomeDomain", "Library/Notes/notes.txt", Encoding.UTF8.GetBytes("Shopping: bread, milk, saffron\nCall the bank on Sunday\n"), Base.AddDays(-3)),
        };
        foreach (var (name, index) in Attachments.Select((a, i) => (a, i)))
        {
            files.Add(new BackupFile("MediaDomain", AttachmentPath(index, name), FakeArt.Photo(name, 640, 480, index % 2 == 0), Base.AddDays(-index)));
        }

        foreach (var asset in device.Spec.Assets.Where(a => a.Kind != PhotoKind.Video).Take(8))
        {
            files.Add(new BackupFile("CameraRollDomain", "Media/DCIM/100APPLE/" + Path.ChangeExtension(asset.FileName, ".PNG"),
                FakeArt.Photo(asset.Id, 320, 240, false), asset.TakenAt ?? Base));
        }

        foreach (var app in device.Spec.Apps.Where(a => a.Info.Source != AppSource.System).Take(12))
        {
            var domain = "AppDomain-" + app.Info.Id;
            files.Add(new BackupFile(domain, $"Library/Preferences/{app.Info.Id}.plist", Plist(new Dictionary<string, object> { ["LaunchCount"] = 12L }), Base));
            if (app.Info.FileSharing)
            {
                files.Add(new BackupFile(domain, "Documents/Read me.txt", Encoding.UTF8.GetBytes($"Documents of {app.Info.Name}\n"), Base));
            }
        }

        return files;
    }

    private static readonly string[] Attachments = ["IMG_2041.PNG", "IMG_2042.PNG", "Map.PNG"];

    private static string AttachmentPath(int index, string name) => $"Library/SMS/Attachments/{index:x2}/{index + 10:x2}/AT{index}/{name}";

    public static byte[] InfoPlist(FakeDevice device, DateTimeOffset when)
    {
        var apps = device.Spec.Apps.Where(a => a.Info.Source != AppSource.System).Take(12).ToList();
        return Plist(new Dictionary<string, object>
        {
            ["Device Name"] = device.Summary.Name,
            ["Display Name"] = device.Summary.Name,
            ["Product Type"] = device.Summary.Model,
            ["Product Name"] = device.Summary.MarketingName ?? "iPhone",
            ["Product Version"] = device.Summary.OsVersion,
            ["Target Identifier"] = device.Summary.Id,
            ["Unique Identifier"] = device.Summary.Id,
            ["Target Type"] = "Device",
            ["Last Backup Date"] = when,
            ["Installed Applications"] = apps.Select(a => (object)a.Info.Id).ToList(),
            ["DeviceDeck App Names"] = apps.ToDictionary(a => a.Info.Id, a => (object)a.Info.Name),
        });
    }

    public static byte[] StatusPlist(string state, string snapshot, DateTimeOffset when, bool full) => Plist(new Dictionary<string, object>
    {
        ["BackupState"] = "new",
        ["Date"] = when,
        ["IsFullBackup"] = full,
        ["Version"] = "3.3",
        ["SnapshotState"] = state,
        ["UUID"] = snapshot,
    });

    public static byte[] ManifestPlist(FakeDevice device, DateTimeOffset when) => Plist(new Dictionary<string, object>
    {
        ["Version"] = "10.0",
        ["Date"] = when,
        ["IsEncrypted"] = false,
        ["WasPasscodeSet"] = true,
        ["Lockdown"] = new Dictionary<string, object>
        {
            ["DeviceName"] = device.Summary.Name,
            ["ProductType"] = device.Summary.Model,
            ["ProductVersion"] = device.Summary.OsVersion,
            ["UniqueDeviceID"] = device.Summary.Id,
        },
        ["Applications"] = device.Spec.Apps.Where(a => a.Info.Source != AppSource.System).Take(12)
            .ToDictionary(a => a.Info.Id, a => (object)new Dictionary<string, object> { ["CFBundleIdentifier"] = a.Info.Id }),
    });

    /// <summary>Manifest.db with one row per file and folder; the file column holds a plain plist (DeviceDeck reads both that and NSKeyedArchiver).</summary>
    public static void WriteManifestDatabase(string path, IReadOnlyList<BackupFile> files)
    {
        var temp = path + ".ddpart";
        File.Delete(temp);
        using (var db = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            db.Open();
            Exec(db, """
                CREATE TABLE Files (fileID TEXT PRIMARY KEY, domain TEXT, relativePath TEXT, flags INTEGER, file BLOB);
                CREATE INDEX FilesDomainIdx ON Files(domain);
                CREATE TABLE Properties (key TEXT PRIMARY KEY, value BLOB);
                """);
            using var tx = db.BeginTransaction();
            var folders = new HashSet<(string, string)>();
            foreach (var file in files)
            {
                var parts = file.Path.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    var folder = string.Join('/', parts[..i]);
                    if (folders.Add((file.Domain, folder)))
                    {
                        Insert(db, tx, file.Domain, folder, 2, 0, file.Modified);
                    }
                }

                Insert(db, tx, file.Domain, file.Path, 1, file.Content.Length, file.Modified);
            }

            tx.Commit();
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void Insert(SqliteConnection db, SqliteTransaction tx, string domain, string path, int flags, long size, DateTimeOffset modified)
    {
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO Files VALUES ($id, $domain, $path, $flags, $file)";
        insert.Parameters.AddWithValue("$id", FileId(domain, path));
        insert.Parameters.AddWithValue("$domain", domain);
        insert.Parameters.AddWithValue("$path", path);
        insert.Parameters.AddWithValue("$flags", flags);
        insert.Parameters.AddWithValue("$file", Plist(new Dictionary<string, object>
        {
            ["RelativePath"] = path,
            ["Size"] = size,
            ["LastModified"] = modified.ToUnixTimeSeconds(),
            ["Mode"] = flags == 2 ? 0x41EDL : 0x81A4L,
            ["ProtectionClass"] = 3L,
        }));
        insert.ExecuteNonQuery();
    }

    // ---- sample databases in the phone's own schemas ----

    private static readonly (string First, string? Last, string? Org, string Phone, string? Email, string Label)[] People =
    [
        ("سارا", "احمدی", null, "+98 912 345 6789", "sara.ahmadi@example.com", "_$!<Mobile>!$_"),
        ("علی", "رضایی", "دانشگاه تهران", "+98 935 111 2233", "ali@example.ir", "iPhone"),
        ("مریم", "کریمی", null, "+98 917 444 5566", null, "_$!<Mobile>!$_"),
        ("رضا", "محمدی", "Snapp", "+98 21 8888 0000", "reza@snapp.example", "_$!<Work>!$_"),
        ("Anna", "Schmidt", "Bosch", "+49 151 2345 6789", "anna.schmidt@example.de", "_$!<Mobile>!$_"),
        ("John", "Appleseed", "Apple", "+1 (408) 555-0199", "john@apple.example", "iPhone"),
        ("Mina", null, null, "+98 901 777 8899", null, "_$!<Home>!$_"),
        ("", null, "Pizza Place", "+98 21 2222 3333", null, "_$!<Main>!$_"),
    ];

    private static byte[] AddressBook() => Database(db =>
    {
        Exec(db, """
            CREATE TABLE ABPerson (ROWID INTEGER PRIMARY KEY, First TEXT, Last TEXT, Middle TEXT, Organization TEXT, Department TEXT,
              Note TEXT, Birthday TEXT, JobTitle TEXT, Nickname TEXT, Prefix TEXT, Suffix TEXT, ModificationDate INTEGER);
            CREATE TABLE ABMultiValue (UID INTEGER PRIMARY KEY, record_id INTEGER, property INTEGER, identifier INTEGER, label INTEGER, value TEXT);
            CREATE TABLE ABMultiValueLabel (value TEXT);
            CREATE TABLE ABMultiValueEntry (parent_id INTEGER, key INTEGER, value TEXT);
            CREATE TABLE ABMultiValueEntryKey (value TEXT);
            """);
        var labels = People.Select(p => p.Label).Append("_$!<Home>!$_").Distinct().ToList();
        foreach (var label in labels)
        {
            Run(db, "INSERT INTO ABMultiValueLabel (value) VALUES ($v)", ("$v", label));
        }

        var uid = 1;
        for (var i = 0; i < People.Length; i++)
        {
            var p = People[i];
            var birthday = i == 0 ? (new DateTime(1992, 7, 23, 12, 0, 0, DateTimeKind.Utc) - Epoch2001.UtcDateTime).TotalSeconds.ToString(CultureInfo.InvariantCulture) : null;
            Run(db, "INSERT INTO ABPerson (ROWID, First, Last, Organization, Birthday, ModificationDate) VALUES ($id, $f, $l, $o, $b, 780000000)",
                ("$id", i + 1), ("$f", string.IsNullOrEmpty(p.First) ? null : p.First), ("$l", p.Last), ("$o", p.Org), ("$b", birthday));
            Run(db, "INSERT INTO ABMultiValue VALUES ($uid, $p, 3, 0, $label, $v)", ("$uid", uid++), ("$p", i + 1), ("$label", labels.IndexOf(p.Label) + 1), ("$v", p.Phone));
            if (p.Email is not null)
            {
                Run(db, "INSERT INTO ABMultiValue VALUES ($uid, $p, 4, 1, $label, $v)", ("$uid", uid++), ("$p", i + 1), ("$label", labels.IndexOf("_$!<Home>!$_") + 1), ("$v", p.Email));
            }
        }
    });

    private static byte[] Messages() => Database(db =>
    {
        Exec(db, """
            CREATE TABLE handle (ROWID INTEGER PRIMARY KEY, id TEXT NOT NULL, service TEXT NOT NULL);
            CREATE TABLE chat (ROWID INTEGER PRIMARY KEY, guid TEXT, style INTEGER, chat_identifier TEXT, service_name TEXT, display_name TEXT);
            CREATE TABLE message (ROWID INTEGER PRIMARY KEY, guid TEXT, text TEXT, handle_id INTEGER, service TEXT, date INTEGER, is_from_me INTEGER,
              item_type INTEGER DEFAULT 0, group_title TEXT, associated_message_type INTEGER DEFAULT 0, attributedBody BLOB);
            CREATE TABLE chat_message_join (chat_id INTEGER, message_id INTEGER);
            CREATE TABLE chat_handle_join (chat_id INTEGER, handle_id INTEGER);
            CREATE TABLE attachment (ROWID INTEGER PRIMARY KEY, guid TEXT, filename TEXT, mime_type TEXT, transfer_name TEXT, total_bytes INTEGER);
            CREATE TABLE message_attachment_join (message_id INTEGER, attachment_id INTEGER);
            INSERT INTO handle VALUES (1, '+989123456789', 'iMessage'), (2, '+989351112233', 'iMessage'), (3, 'john@apple.example', 'iMessage'), (4, '+982122223333', 'SMS');
            INSERT INTO chat VALUES (1, 'c1', 45, '+989123456789', 'iMessage', ''), (2, 'c2', 43, 'chat-family', 'iMessage', 'خانواده'),
              (3, 'c3', 45, 'john@apple.example', 'iMessage', NULL), (4, 'c4', 45, '+982122223333', 'SMS', NULL);
            INSERT INTO chat_handle_join VALUES (1, 1), (2, 1), (2, 2), (3, 3), (4, 4);
            """);
        var id = 0;
        void Say(int chat, int handle, bool me, string text, DateTimeOffset when, int? attachment = null)
        {
            id++;
            Run(db, "INSERT INTO message (ROWID, guid, text, handle_id, service, date, is_from_me) VALUES ($id, $g, $t, $h, $s, $d, $m)",
                ("$id", id), ("$g", "m" + id), ("$t", text), ("$h", me ? 0 : handle), ("$s", chat == 4 ? "SMS" : "iMessage"),
                ("$d", (long)(when - Epoch2001).TotalSeconds * 1_000_000_000L), ("$m", me ? 1 : 0));
            Run(db, "INSERT INTO chat_message_join VALUES ($c, $m)", ("$c", chat), ("$m", id));
            if (attachment is { } a)
            {
                Run(db, "INSERT INTO attachment VALUES ($a, $g, $f, 'image/png', $n, 0)",
                    ("$a", a + 1), ("$g", "AT" + a), ("$f", "~/" + AttachmentPath(a, Attachments[a])), ("$n", Attachments[a]));
                Run(db, "INSERT INTO message_attachment_join VALUES ($m, $a)", ("$m", id), ("$a", a + 1));
            }
        }

        var t = Base.AddDays(-20);
        Say(1, 1, false, "سلام! فردا وقت داری؟", t);
        Say(1, 1, true, "سلام سارا، آره بعد از ظهر خالیم 🙂", t.AddMinutes(4));
        Say(1, 1, false, "عالیه، ساعت ۵ کافه همیشگی؟", t.AddMinutes(6));
        Say(1, 1, true, "قبول 👍", t.AddMinutes(7));
        Say(1, 1, false, "عکس‌های سفر رو هم بیار", t.AddDays(1));
        Say(1, 1, true, "￼", t.AddDays(1).AddMinutes(30), attachment: 0);
        Say(1, 1, false, "چقدر قشنگه! ❤️", t.AddDays(1).AddMinutes(32));
        Say(2, 1, false, "شام جمعه خونه‌ی ما؟", t.AddDays(3));
        Say(2, 2, false, "من هستم", t.AddDays(3).AddMinutes(10));
        Say(2, 2, true, "ما هم میایم", t.AddDays(3).AddMinutes(12));
        Say(2, 1, false, "￼", t.AddDays(3).AddHours(2), attachment: 2);
        Say(3, 3, false, "Hi! Are the slides ready for Monday?", t.AddDays(10));
        Say(3, 3, true, "Almost — sending them tonight.", t.AddDays(10).AddMinutes(15));
        Say(3, 3, true, "￼Here’s the cover photo", t.AddDays(10).AddHours(5), attachment: 1);
        Say(3, 3, false, "Perfect, thanks!", t.AddDays(10).AddHours(6));
        Say(4, 4, false, "سفارش شما ثبت شد و تا ۳۰ دقیقه دیگر می‌رسد.", t.AddDays(15));
    });

    private static void Exec(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
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
        var path = Path.Combine(Path.GetTempPath(), "devicedeck-fake-" + Guid.NewGuid().ToString("N") + ".db");
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

    /// <summary>An XML property list: dictionaries, lists, strings, integers, booleans, dates and data.</summary>
    public static byte[] Plist(IReadOnlyDictionary<string, object> root)
    {
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            w.WriteStartDocument();
            w.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            w.WriteStartElement("plist");
            w.WriteAttributeString("version", "1.0");
            Value(w, root);
            w.WriteEndElement();
        }

        return stream.ToArray();

        static void Value(XmlWriter w, object value)
        {
            switch (value)
            {
                case string s:
                    w.WriteElementString("string", s);
                    break;
                case bool b:
                    w.WriteStartElement(b ? "true" : "false");
                    w.WriteEndElement();
                    break;
                case long or int:
                    w.WriteElementString("integer", Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case DateTimeOffset d:
                    w.WriteElementString("date", d.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                    break;
                case byte[] data:
                    w.WriteElementString("data", Convert.ToBase64String(data));
                    break;
                case IReadOnlyDictionary<string, object> dict:
                    w.WriteStartElement("dict");
                    foreach (var (key, item) in dict)
                    {
                        w.WriteElementString("key", key);
                        Value(w, item);
                    }

                    w.WriteEndElement();
                    break;
                case IEnumerable<object> list:
                    w.WriteStartElement("array");
                    foreach (var item in list)
                    {
                        Value(w, item);
                    }

                    w.WriteEndElement();
                    break;
                default:
                    throw new ArgumentException($"A property list can't hold {value.GetType().Name}.");
            }
        }
    }
}
