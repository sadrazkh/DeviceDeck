using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DeviceDeck.Ios.Backup.Format;
using DeviceDeck.Ios.Plist;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests.Backup;

/// <summary>
/// Writes a backup the way an iPhone (iOS 10+) does: Info/Status/Manifest.plist, Manifest.db whose rows carry
/// NSKeyedArchiver MBFile blobs, and files named by SHA-1 in 256 folders. With a password it is encrypted like the
/// phone does it: a keybag (PBKDF2-SHA256 then -SHA1, AES-wrapped class keys), Manifest.db under ManifestKey, and
/// every file under its own wrapped key. Iteration counts are small so tests stay fast.
/// </summary>
internal sealed class BackupFixtureBuilder
{
    public const string Udid = "00008150-0012345678ABCDEF";

    private readonly List<(string Domain, string Path, byte[]? Content, int Class, DateTimeOffset Modified)> _items = [];
    private readonly Dictionary<int, byte[]> _classKeys = [];

    public string DeviceId { get; init; } = Udid;
    public string DeviceName { get; init; } = "Nima’s iPhone";
    public string ProductType { get; init; } = "iPhone17,3";
    public string ProductVersion { get; init; } = "18.4";
    public DateTimeOffset Date { get; init; } = new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);
    public string? Password { get; init; }
    public string SnapshotState { get; init; } = "finished";
    public string SnapshotId { get; init; } = "11111111-2222-3333-4444-555555555555";
    public Dictionary<string, string> AppNames { get; } = new(StringComparer.Ordinal);

    public BackupFixtureBuilder File(string domain, string path, byte[] content, int protectionClass = 3, DateTimeOffset? modified = null)
    {
        _items.Add((domain, path, content, protectionClass, modified ?? new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));
        return this;
    }

    public BackupFixtureBuilder Text(string domain, string path, string text) => File(domain, path, Encoding.UTF8.GetBytes(text));

    public BackupFixtureBuilder Dir(string domain, string path)
    {
        _items.Add((domain, path, null, 0, new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));
        return this;
    }

    public static string FileId(string domain, string path) =>
        Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(domain + "-" + path)));

    /// <summary>Writes the backup into <paramref name="folder"/> (created) and returns it.</summary>
    public string Build(string folder)
    {
        Directory.CreateDirectory(folder);
        var encrypted = Password is not null;
        byte[]? keybag = null;
        if (encrypted)
            keybag = MakeKeybag(Password!);

        var dbPath = Path.Combine(Path.GetTempPath(), "devicedeck-fixture-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                db.Open();
                Exec(db, """
                    CREATE TABLE Files (fileID TEXT PRIMARY KEY, domain TEXT, relativePath TEXT, flags INTEGER, file BLOB);
                    CREATE INDEX FilesDomainIdx ON Files(domain);
                    CREATE INDEX FilesRelativePathIdx ON Files(relativePath);
                    CREATE INDEX FilesFlagsIdx ON Files(flags);
                    CREATE TABLE Properties (key TEXT PRIMARY KEY, value BLOB);
                    """);
                using var tx = db.BeginTransaction();
                foreach (var (domain, path, content, cls, modified) in _items)
                {
                    var id = FileId(domain, path);
                    byte[]? storedKey = null;
                    if (content is not null)
                    {
                        var data = content;
                        if (encrypted)
                        {
                            var fileKey = RandomNumberGenerator.GetBytes(32);
                            storedKey = [.. LittleEndian(cls), .. AesKeyWrap.Wrap(_classKeys[cls], fileKey)];
                            data = BackupCrypto.Encrypt(content, fileKey);
                        }
                        Directory.CreateDirectory(Path.Combine(folder, id[..2]));
                        System.IO.File.WriteAllBytes(Path.Combine(folder, id[..2], id), data);
                    }
                    using var insert = db.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = "INSERT INTO Files VALUES ($id, $domain, $path, $flags, $file)";
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$domain", domain);
                    insert.Parameters.AddWithValue("$path", path);
                    insert.Parameters.AddWithValue("$flags", content is null ? 2 : 1);
                    insert.Parameters.AddWithValue("$file", MbFile(path, content?.Length ?? 0, content is null, cls, modified, storedKey));
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
            }

            var manifestDb = System.IO.File.ReadAllBytes(dbPath);
            byte[]? manifestKey = null;
            if (encrypted)
            {
                var key = RandomNumberGenerator.GetBytes(32);
                manifestKey = [.. LittleEndian(4), .. AesKeyWrap.Wrap(_classKeys[4], key)];
                manifestDb = BackupCrypto.Encrypt(manifestDb, key);
            }
            System.IO.File.WriteAllBytes(Path.Combine(folder, "Manifest.db"), manifestDb);

            var manifest = new PlistDict
            {
                ["Version"] = "10.0",
                ["Date"] = Date,
                ["SystemDomainsVersion"] = "24.0",
                ["WasPasscodeSet"] = true,
                ["IsEncrypted"] = encrypted,
                ["Lockdown"] = new PlistDict
                {
                    ["ProductVersion"] = ProductVersion,
                    ["ProductType"] = ProductType,
                    ["BuildVersion"] = "22E240",
                    ["UniqueDeviceID"] = DeviceId,
                    ["SerialNumber"] = "F2LXX0000000",
                    ["DeviceName"] = DeviceName,
                },
                ["Applications"] = new PlistDict(AppNames.Keys.ToDictionary(id => id, id => (object)new PlistDict { ["CFBundleIdentifier"] = id, ["CFBundleVersion"] = "1" })),
            };
            if (encrypted)
            {
                manifest["BackupKeyBag"] = keybag!;
                manifest["ManifestKey"] = manifestKey!;
            }
            System.IO.File.WriteAllBytes(Path.Combine(folder, "Manifest.plist"), PlistSerializer.ToBinary(manifest));

            System.IO.File.WriteAllBytes(Path.Combine(folder, "Status.plist"), PlistSerializer.ToBinary(new PlistDict
            {
                ["IsFullBackup"] = false,
                ["Version"] = "3.3",
                ["BackupState"] = "new",
                ["Date"] = Date,
                ["UUID"] = SnapshotId,
                ["SnapshotState"] = SnapshotState,
            }));

            System.IO.File.WriteAllBytes(Path.Combine(folder, "Info.plist"), PlistSerializer.ToXml(new PlistDict
            {
                ["Device Name"] = DeviceName,
                ["Display Name"] = DeviceName,
                ["Product Type"] = ProductType,
                ["Product Version"] = ProductVersion,
                ["Target Identifier"] = DeviceId,
                ["Unique Identifier"] = DeviceId,
                ["Target Type"] = "Device",
                ["Last Backup Date"] = Date,
                ["Installed Applications"] = AppNames.Keys.Cast<object>().ToList(),
                ["DeviceDeck App Names"] = new PlistDict(AppNames.ToDictionary(kv => kv.Key, kv => (object)kv.Value)),
            }));
            return folder;
        }
        finally
        {
            System.IO.File.Delete(dbPath);
        }
    }

    /// <summary>An MBFile archived with NSKeyedArchiver, as Manifest.db's file column holds it.</summary>
    private static byte[] MbFile(string path, long size, bool directory, int cls, DateTimeOffset modified, byte[]? storedKey)
    {
        var objects = new List<object> { "$null" };
        var file = new PlistDict
        {
            ["Size"] = size,
            ["LastModified"] = modified.ToUnixTimeSeconds(),
            ["Birth"] = modified.ToUnixTimeSeconds(),
            ["LastStatusChange"] = modified.ToUnixTimeSeconds(),
            ["Mode"] = directory ? 0x41EDL : 0x81A4L,
            ["Flags"] = 0L,
            ["ProtectionClass"] = (long)cls,
            ["UserID"] = 501L,
            ["GroupID"] = 501L,
            ["InodeNumber"] = 4242L,
        };
        objects.Add(file);
        objects.Add(path);
        file["RelativePath"] = new PlistUid(2);
        if (storedKey is not null)
        {
            objects.Add(new PlistDict { ["NS.data"] = storedKey, ["$class"] = new PlistUid(4) });
            objects.Add(new PlistDict { ["$classname"] = "NSMutableData", ["$classes"] = new List<object> { "NSMutableData", "NSData", "NSObject" } });
            file["EncryptionKey"] = new PlistUid(3);
        }
        objects.Add(new PlistDict { ["$classname"] = "MBFile", ["$classes"] = new List<object> { "MBFile", "NSObject" } });
        file["$class"] = new PlistUid((ulong)objects.Count - 1);
        return PlistSerializer.ToBinary(new PlistDict
        {
            ["$version"] = 100000L,
            ["$archiver"] = "NSKeyedArchiver",
            ["$top"] = new PlistDict { ["root"] = new PlistUid(1) },
            ["$objects"] = objects,
        });
    }

    private byte[] MakeKeybag(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(20);
        var dpSalt = RandomNumberGenerator.GetBytes(20);
        const int iterations = 50;
        const int dpIterations = 100;
        var round1 = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), dpSalt, dpIterations, HashAlgorithmName.SHA256, 32);
        var passwordKey = Rfc2898DeriveBytes.Pbkdf2(round1, salt, iterations, HashAlgorithmName.SHA1, 32);

        var bag = new MemoryStream();
        void Tag(string tag, byte[] value)
        {
            bag.Write(Encoding.ASCII.GetBytes(tag));
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            bag.Write(length);
            bag.Write(value);
        }
        Tag("VERS", BigEndian(4));
        Tag("TYPE", BigEndian(1));
        Tag("UUID", RandomNumberGenerator.GetBytes(16));
        Tag("HMCK", RandomNumberGenerator.GetBytes(40));
        Tag("WRAP", BigEndian(0));
        Tag("SALT", salt);
        Tag("ITER", BigEndian(iterations));
        Tag("DPWT", BigEndian(1));
        Tag("DPIC", BigEndian(dpIterations));
        Tag("DPSL", dpSalt);
        foreach (var cls in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 })
        {
            var key = RandomNumberGenerator.GetBytes(32);
            _classKeys[cls] = key;
            Tag("UUID", RandomNumberGenerator.GetBytes(16));
            Tag("CLAS", BigEndian(cls));
            Tag("WRAP", BigEndian(2));
            Tag("KTYP", BigEndian(0));
            Tag("WPKY", AesKeyWrap.Wrap(passwordKey, key));
        }
        return bag.ToArray();
    }

    private static byte[] BigEndian(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return b;
    }

    private static byte[] LittleEndian(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    internal static void Exec(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
