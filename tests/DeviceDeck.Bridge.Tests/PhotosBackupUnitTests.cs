using System.Text.Json;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Serialization;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The pieces of the photo backup on their own: folder patterns and names, the ledger's idea of "known", EXIF.</summary>
public sealed class PhotosBackupUnitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-photos-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("yyyy/MM", true)]
    [InlineData("yyyy/MM/dd", true)]
    [InlineData("yyyy-MM", true)]
    [InlineData("yyyy", true)]
    [InlineData("", true)]
    [InlineData("yyyy MM dd", true)]
    [InlineData("MMMM", false)]
    [InlineData("yyyy/", false)]
    [InlineData("/yyyy", false)]
    [InlineData("yyyy/../MM", false)]
    [InlineData("C:\\yyyy", false)]
    [InlineData("hh", false)]
    public void Folder_patterns_are_dates_only(string pattern, bool valid) => Assert.Equal(valid, PhotoBackupNaming.IsValidPattern(pattern));

    [Fact]
    public void Folders_follow_the_pattern_in_the_pcs_time_zone()
    {
        var when = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        var local = when.ToLocalTime();
        string L(string f) => local.ToString(f, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(Path.Combine(@"D:\B", L("yyyy"), L("MM")), PhotoBackupNaming.Folder(@"D:\B", "yyyy/MM", when));
        Assert.Equal(Path.Combine(@"D:\B", L("yyyy"), L("MM"), L("dd")), PhotoBackupNaming.Folder(@"D:\B", "yyyy/MM/dd", when));
        Assert.Equal(Path.Combine(@"D:\B", L("yyyy-MM")), PhotoBackupNaming.Folder(@"D:\B", "yyyy-MM", when));
        Assert.Equal(@"D:\B", PhotoBackupNaming.Folder(@"D:\B", "", when));
        Assert.Equal(Path.Combine(@"D:\B", "Undated"), PhotoBackupNaming.Folder(@"D:\B", "yyyy/MM", null));
    }

    [Fact]
    public void A_batch_never_plans_two_photos_onto_one_name_and_live_pairs_share_a_stem()
    {
        var when = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        PhotoBackupAsset A(string name, bool live = false) => new("k:" + Guid.NewGuid(), name, name, PhotoKind.Photo, when, 10, false, false, null,
            live ? new PhotoBackupMotion(name + ".MOV", Path.ChangeExtension(name, ".MOV"), 5) : null);
        var plans = PhotoBackupNaming.Plan([A("IMG_0001.HEIC", live: true), A("IMG_0001.HEIC"), A("IMG_0001.HEIC", live: true), A("con.jpg")], @"D:\B", "", convertHeic: true);
        Assert.Equal(
            [(@"D:\B\IMG_0001.jpg", @"D:\B\IMG_0001.MOV"), (@"D:\B\IMG_0001 (1).jpg", null), (@"D:\B\IMG_0001 (2).jpg", @"D:\B\IMG_0001 (2).MOV"), (@"D:\B\_con.jpg", null)],
            plans);
    }

    [Theory]
    [InlineData("IMG_0001.HEIC", "IMG_0001.HEIC")]
    [InlineData("a:b?.jpg", "a_b_.jpg")]
    [InlineData("NUL.jpg", "_NUL.jpg")]
    [InlineData("com1.mov", "_com1.mov")]
    [InlineData("photo. ", "photo")]
    public void Phone_names_are_made_safe_for_windows(string name, string expected) => Assert.Equal(expected, PhotoBackupNaming.SafeName(name));

    [Fact]
    public void A_photo_is_known_by_key_by_path_or_by_name_size_and_date()
    {
        using var ledger = new PhotoBackupLedger(Path.Combine(_dir, "ledger.db"));
        var when = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        ledger.Record("d1", new PhotoBackupRecord("uuid:A", "DCIM/100APPLE/IMG_0001.HEIC", "IMG_0001.HEIC", 1000, when, @"D:\B\IMG_0001.HEIC", null, false, "j", when));
        var known = ledger.LoadKnown("d1");
        PhotoBackupAsset Asset(string key, string? path, string name = "IMG_0009.HEIC", long size = 1000, DateTimeOffset? taken = null) =>
            new(key, "x", name, PhotoKind.Photo, taken, size, false, false, path, null);

        var cases = new (PhotoBackupAsset Asset, bool Known)[]
        {
            (Asset("uuid:A", null), true),                                                   // same key
            (Asset("dcim:DCIM/100APPLE/IMG_0001.HEIC", "DCIM/100APPLE/IMG_0001.HEIC", size: 0), true), // read without Photos.sqlite: path, size not known yet
            (Asset("x", "dcim/100apple/img_0001.heic", size: 1000), true),                   // path, same size (any case)
            (Asset("x", "DCIM/100APPLE/IMG_0001.HEIC", size: 2000), false),                  // path, but a new version
            (Asset("file:/moved/IMG_0001.HEIC|1000", "/moved/IMG_0001.HEIC", "img_0001.heic", 1000, when), true), // moved: name + size + date
            (Asset("y", null, "IMG_0001.HEIC", 1000, when.AddSeconds(1)), false),             // another date
            (Asset("uuid:B", null), false),
        };
        foreach (var (asset, expected) in cases)
        {
            Assert.True(expected == known.Contains(asset), $"{asset.Key} {asset.SourcePath} should be {(expected ? "known" : "new")}");
            Assert.True(expected == ledger.IsKnown("d1", asset), $"{asset.Key} {asset.SourcePath} (database) should be {(expected ? "known" : "new")}");
        }

        Assert.False(ledger.LoadKnown("d2").Contains(Asset("uuid:A", null)));
        Assert.Equal(1, ledger.Forget("d1"));
        Assert.False(ledger.IsKnown("d1", Asset("uuid:A", null)));
    }

    [Fact]
    public void Pc_file_hashes_are_reused_until_the_file_changes()
    {
        using var ledger = new PhotoBackupLedger(Path.Combine(_dir, "ledger.db"));
        var file = Path.Combine(_dir, "a.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        var info = new FileInfo(file);
        ledger.PutHash(file, 3, info.LastWriteTimeUtc.Ticks, [9, 9]);
        Assert.Equal([9, 9], ledger.GetHash(file.ToUpperInvariant(), 3, info.LastWriteTimeUtc.Ticks));
        Assert.Null(ledger.GetHash(file, 3, info.LastWriteTimeUtc.Ticks + 1));
        Assert.Null(ledger.GetHash(file, 4, info.LastWriteTimeUtc.Ticks));
    }

    [Fact]
    public void Exif_is_read_from_a_heif_container_by_file_offset_or_from_idat()
    {
        var tiff = TestTiff.Make(orientation: 6);
        foreach (var (version, idat) in new[] { (0, false), (1, false), (1, true), (2, false) })
        {
            using var heif = new MemoryStream(TestHeif.Make(tiff, version, idat));
            Assert.True(tiff.AsSpan().SequenceEqual(PhotosHeicMetadata.ReadExif(heif)), $"iloc version {version}, idat {idat}");
        }

        Assert.Null(PhotosHeicMetadata.ReadExif(new MemoryStream(TestJpeg.Make(10, 10))));
        Assert.Null(PhotosHeicMetadata.ReadExif(new MemoryStream(new byte[100])));
        var torn = TestHeif.Make(tiff);
        Assert.Null(PhotosHeicMetadata.ReadExif(new MemoryStream(torn[..(torn.Length - 40)])));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exif_goes_into_the_jpeg_upright_with_the_jpegs_size(bool littleEndian)
    {
        var jpeg = TestJpeg.Make(300, 400, encoderExif: true);
        var result = PhotosHeicMetadata.WithExif(jpeg, TestTiff.Make(orientation: 8, littleEndian: littleEndian));

        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], result[..4]); // JFIF stays first
        var exif = PhotosHeicMetadata.ReadJpegExif(result)!;
        Assert.Equal(1u, PhotosHeicMetadata.ReadTag(exif, 0x0112));
        Assert.Equal(300u, PhotosHeicMetadata.ReadTag(exif, 0xA002, exifIfd: true));
        Assert.Equal(400u, PhotosHeicMetadata.ReadTag(exif, 0xA003, exifIfd: true));
        Assert.True(exif.AsSpan().IndexOf("2026:10:15 13:00:00"u8) >= 0);

        // The encoder's own EXIF is gone: exactly one APP1 Exif segment.
        var app1 = 0;
        for (var i = 0; i + 10 < result.Length; i++)
        {
            if (result[i] == 0xFF && result[i + 1] == 0xE1 && result.AsSpan(i + 4, 6).SequenceEqual("Exif\0\0"u8))
            {
                app1++;
            }
        }

        Assert.Equal(1, app1);
        Assert.Equal(jpeg[^40..], result[^40..]); // the image data is untouched
    }

    [Fact]
    public void Exif_too_big_for_one_segment_or_a_broken_jpeg_leaves_the_jpeg_as_it_was()
    {
        var jpeg = TestJpeg.Make(300, 400);
        Assert.Same(jpeg, PhotosHeicMetadata.WithExif(jpeg, TestTiff.Make(orientation: 1, padding: 70_000)));
        var broken = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        Assert.Same(broken, PhotosHeicMetadata.WithExif(broken, TestTiff.Make(orientation: 1)));
        Assert.Same(jpeg, PhotosHeicMetadata.WithExif(jpeg, [1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Settings_carry_the_backup_defaults_after_round_ones_fields()
    {
        var json = JsonSerializer.SerializeToNode(new AppSettings { PhotoBackupFolder = @"D:\Backup" }, DeviceDeckJson.Options)!.AsObject();
        Assert.Equal(["photoBackupFolder", "photoBackupPattern", "photoBackupConvertHeic"], json.Select(p => p.Key).Skip(8).Take(3));
        Assert.Equal(@"D:\Backup", (string?)json["photoBackupFolder"]);
        Assert.Equal("yyyy/MM", (string?)json["photoBackupPattern"]);
        Assert.False((bool)json["photoBackupConvertHeic"]!);
        Assert.Throws<ArgumentException>(() => new AppSettings { PhotoBackupPattern = "dd/MM/yyyy/hh" }.Validate());
        Assert.Throws<ArgumentException>(() => new AppSettings { PhotoBackupFolder = " " }.Validate());
    }

    [Fact]
    public void Transfer_kinds_and_backup_enums_are_camel_case()
    {
        Assert.Equal("\"photosBackup\"", JsonSerializer.Serialize(TransferKind.PhotosBackup, DeviceDeckJson.Options));
        Assert.Equal("\"photosToApp\"", JsonSerializer.Serialize(TransferKind.PhotosToApp, DeviceDeckJson.Options));
        Assert.Equal("\"missingCodec\"", JsonSerializer.Serialize(HeicConversion.MissingCodec, DeviceDeckJson.Options));
        Assert.Equal("\"cancelled\"", JsonSerializer.Serialize(PhotoBackupRunState.Cancelled, DeviceDeckJson.Options));
    }

    [Fact]
    public async Task The_hashing_stream_sums_what_passes_through()
    {
        using var target = new MemoryStream();
        using var hashing = new HashingWriteStream(target);
        hashing.Write([1, 2, 3]);
        await hashing.WriteAsync(new byte[] { 4, 5 });
        Assert.Equal(5, hashing.Written);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3, 4, 5 }), hashing.Finish());
        Assert.Equal([1, 2, 3, 4, 5], target.ToArray());
    }
}
