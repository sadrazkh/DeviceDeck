using DeviceDeck.Ios.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace PhotoBench;

/// <summary>
/// An iPhone for fake mode: the iPhone tests' usbmuxd, lockdown and AFC fakes over a <see cref="SimulatedLink"/>, with a
/// camera roll of made-up files (content generated on the fly, so a gigabyte costs no memory) and a Photos.sqlite
/// describing them. Dates spread over the last 60 days, newest first, so "--since 30d" takes about half.
/// </summary>
internal sealed class FakeIphone
{
    public const string Udid = "00008140-000A1B2C3D4E801E";

    private FakeIphone(FakeUsbmux usbmux, int items, long bytes, string description)
    {
        Usbmux = usbmux;
        Items = items;
        Bytes = bytes;
        Description = description;
    }

    public FakeUsbmux Usbmux { get; }

    public int Items { get; }

    public long Bytes { get; }

    public string Description { get; }

    public sealed record Link(double MegabytesPerSecond, double RoundTripMs, double ConnectMs, double SessionMs);

    public static FakeIphone Create(int count, int seed, Link link, string scratch)
    {
        var simulated = new SimulatedLink(link.MegabytesPerSecond * 1_000_000, TimeSpan.FromMilliseconds(link.RoundTripMs / 2));
        var usbmux = new FakeUsbmux
        {
            CreatePair = simulated.CreatePair,
            ConnectDelay = TimeSpan.FromMilliseconds(link.ConnectMs),
        };
        usbmux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId, SessionDelay = TimeSpan.FromMilliseconds(link.SessionMs) };

        var random = new Random(seed);
        var now = DateTimeOffset.UtcNow;
        var rows = new List<Row>(count);
        long bytes = 0;
        var files = 0;
        for (var i = 0; i < count; i++)
        {
            var taken = now - TimeSpan.FromDays(60.0 * i / count) - TimeSpan.FromMinutes(random.Next(0, 60));
            var roll = random.NextDouble();
            var (ext, kind, subtype, size) = roll switch
            {
                < 0.03 => (".MOV", 1, 0, Mb(random, 15, 60)),
                < 0.10 => (".PNG", 0, 10, Mb(random, 0.3, 1.5)),
                < 0.30 => (".JPG", 0, 0, Mb(random, 2, 5)),
                _ => (".HEIC", 0, random.NextDouble() < 0.5 ? 2 : 0, Mb(random, 1.5, 3.5)),
            };
            var folder = $"DCIM/{100 + i / 1000}APPLE";
            var name = $"IMG_{1000 + i % 9000:D4}{ext}";
            phone.Media.AddGeneratedFile($"/{folder}/{name}", size, (ulong)(seed * 1_000_003L + i * 2), taken);
            bytes += size;
            files++;
            if (subtype == 2)
            {
                var motion = Mb(random, 1.5, 3);
                phone.Media.AddGeneratedFile($"/{folder}/{Path.ChangeExtension(name, ".MOV")}", motion, (ulong)(seed * 1_000_003L + i * 2 + 1), taken);
                bytes += motion;
                files++;
            }

            rows.Add(new Row(i + 1, folder, name, kind, subtype, taken, size, $"BENCH-{seed}-{i:D6}"));
        }

        Directory.CreateDirectory(scratch);
        phone.Media.AddFile("/PhotoData/Photos.sqlite", PhotosDatabase(rows, Path.Combine(scratch, "fake-Photos.sqlite")));
        usbmux.Attach(1, phone);
        return new FakeIphone(usbmux, count, bytes,
            $"fake iPhone: {count:N0} photos and videos in {files:N0} files, {bytes / 1e9:0.00} GB; link {link.MegabytesPerSecond:0.#} MB/s shared, " +
            $"round trip {link.RoundTripMs:0.##} ms, {link.ConnectMs:0.#} ms per connection, {link.SessionMs:0.#} ms per lockdown session");
    }

    private static long Mb(Random random, double min, double max) => (long)((min + random.NextDouble() * (max - min)) * 1_000_000);

    private sealed record Row(long Pk, string Directory, string FileName, int Kind, int Subtype, DateTimeOffset Taken, long Size, string Uuid);

    /// <summary>A Photos.sqlite in the iOS 14+ layout with the columns DeviceDeck reads.</summary>
    private static byte[] PhotosDatabase(IReadOnlyList<Row> rows, string path)
    {
        File.Delete(path);
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var tx = db.BeginTransaction();
            using (var create = db.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText = """
                    CREATE TABLE ZASSET (
                      Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZFAVORITE INTEGER, ZHIDDEN INTEGER, ZKIND INTEGER,
                      ZKINDSUBTYPE INTEGER, ZTRASHEDSTATE INTEGER, ZISDETECTEDSCREENSHOT INTEGER, ZWIDTH INTEGER, ZHEIGHT INTEGER,
                      ZDATECREATED TIMESTAMP, ZDURATION FLOAT, ZDIRECTORY VARCHAR, ZFILENAME VARCHAR, ZUUID VARCHAR);
                    CREATE TABLE ZADDITIONALASSETATTRIBUTES (
                      Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZASSET INTEGER, ZORIGINALFILESIZE INTEGER, ZORIGINALFILENAME VARCHAR);
                    CREATE TABLE ZGENERICALBUM (
                      Z_PK INTEGER PRIMARY KEY, Z_ENT INTEGER, Z_OPT INTEGER, ZKIND INTEGER, ZTRASHEDSTATE INTEGER, ZTITLE VARCHAR, ZUUID VARCHAR);
                    """;
                create.ExecuteNonQuery();
            }

            using var asset = db.CreateCommand();
            asset.Transaction = tx;
            asset.CommandText = """
                INSERT INTO ZASSET VALUES ($pk, 3, 1, 0, 0, $kind, $subtype, 0, $shot, 4032, 3024, $created, $duration, $dir, $file, $uuid);
                INSERT INTO ZADDITIONALASSETATTRIBUTES VALUES ($pk, 33, 1, $pk, $size, $file);
                """;
            foreach (var name in new[] { "$pk", "$kind", "$subtype", "$shot", "$created", "$duration", "$dir", "$file", "$uuid", "$size" })
            {
                asset.Parameters.Add(new SqliteParameter(name, null));
            }

            var epoch = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
            foreach (var row in rows)
            {
                asset.Parameters["$pk"].Value = row.Pk;
                asset.Parameters["$kind"].Value = row.Kind;
                asset.Parameters["$subtype"].Value = row.Subtype;
                asset.Parameters["$shot"].Value = row.Subtype == 10 ? 1 : 0;
                asset.Parameters["$created"].Value = (row.Taken - epoch).TotalSeconds;
                asset.Parameters["$duration"].Value = row.Kind == 1 ? 30.0 : 0.0;
                asset.Parameters["$dir"].Value = row.Directory;
                asset.Parameters["$file"].Value = row.FileName;
                asset.Parameters["$uuid"].Value = row.Uuid;
                asset.Parameters["$size"].Value = row.Size;
                asset.ExecuteNonQuery();
            }

            tx.Commit();
        }

        SqliteConnection.ClearAllPools();
        return File.ReadAllBytes(path);
    }
}
