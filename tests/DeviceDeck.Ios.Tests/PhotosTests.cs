using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device.Photos;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests;

public sealed class PhotosTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-photos-").FullName;

    /// <summary>What AFC lists under DCIM in these tests (IMG_0007 is newer than the database copy).</summary>
    internal static readonly string[] DcimFiles =
    [
        "DCIM/100APPLE/IMG_0001.JPG",
        "DCIM/100APPLE/IMG_0002.HEIC",
        "DCIM/100APPLE/IMG_0002.MOV",
        "DCIM/100APPLE/IMG_0003.MOV",
        "DCIM/100APPLE/IMG_0004.PNG",
        "DCIM/100APPLE/IMG_0005.JPG",
        "DCIM/100APPLE/IMG_0006.JPG",
        "DCIM/100APPLE/IMG_0007.JPG",
    ];

    private static readonly DateTimeOffset June1 = new(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Builds a Photos.sqlite from one of the fixture scripts.</summary>
    internal static string CreateDatabase(string script, string path, bool wal = false)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = (wal ? "PRAGMA journal_mode=WAL;" : "") + Fixture.Text("Photos", script);
        cmd.ExecuteNonQuery();
        return path;
    }

    [Theory]
    [InlineData("ios13.sql", "iOS 9-13", "ZGENERICASSET", "Z_26ASSETS", "Z_26ALBUMS", "Z_34ASSETS")]
    [InlineData("ios17.sql", "iOS 14+", "ZASSET", "Z_30ASSETS", "Z_30ALBUMS", "Z_3ASSETS")]
    public void Schema_is_detected_per_ios_version(string script, string variant, string assetTable, string join, string albumColumn, string assetColumn)
    {
        using var db = PhotosDatabase.Open(CreateDatabase(script, Path.Combine(_dir, script + ".sqlite")));

        Assert.Equal(variant, db.Schema.Variant);
        Assert.Equal(assetTable, db.Schema.AssetTable);
        Assert.Equal("ZADDITIONALASSETATTRIBUTES", db.Schema.AttributesTable);
        Assert.Equal(new AlbumJoin(join, albumColumn, assetColumn, "Z_FOK_" + assetColumn[2..]), db.Schema.Join);
    }

    [Theory]
    [InlineData("ios13.sql")]
    [InlineData("ios17.sql")]
    public void Index_maps_kinds_dates_favourites_live_pairs_and_albums(string script)
    {
        using var db = PhotosDatabase.Open(CreateDatabase(script, Path.Combine(_dir, script + ".sqlite")));
        var index = PhotoIndex.Build(DcimFiles, db.ReadAssets(), db.ReadAlbums(), db.Schema.Variant);

        Assert.Equal(PhotoIndexMode.Database, index.Mode);
        Assert.Equal(40_000_000, index.VideoBytes); // the one video whose file is on the phone
        // Newest first; trashed (5) and hidden (6) are not in All; IMG_0007.JPG is not in the database and comes last.
        Assert.Equal(
            ["DCIM/101APPLE/IMG_0101.HEIC", "DCIM/100APPLE/IMG_0004.PNG", "DCIM/100APPLE/IMG_0003.MOV",
             "DCIM/100APPLE/IMG_0002.HEIC", "DCIM/100APPLE/IMG_0001.JPG", "DCIM/100APPLE/IMG_0007.JPG"],
            index.Page(null, 0, 100).Items.Select(a => a.Id));

        // The hidden photo (6) is only in the Hidden album.
        Assert.Equal(1, index.HiddenCount);
        Assert.Equal(["DCIM/100APPLE/IMG_0006.JPG"], index.Page("hidden", 0, 10).Items.Select(a => a.Id));

        var byId = index.Assets.ToDictionary(a => a.Id);
        var photo = byId["DCIM/100APPLE/IMG_0001.JPG"];
        Assert.Equal((PhotoKind.Photo, June1, 2_500_000L, 4032, 3024, false, false),
            (photo.Kind, photo.TakenAt, photo.Size, photo.Width, photo.Height, photo.IsFavorite, photo.InCloudOnly));

        var live = byId["DCIM/100APPLE/IMG_0002.HEIC"];
        Assert.Equal(PhotoKind.LivePhoto, live.Kind);
        Assert.True(live.IsFavorite);
        Assert.Equal("DCIM/100APPLE/IMG_0002.MOV", live.LivePairId);
        Assert.True(index.IsKnownFile("DCIM/100APPLE/IMG_0002.MOV"));
        Assert.DoesNotContain("DCIM/100APPLE/IMG_0002.MOV", byId.Keys);

        var video = byId["DCIM/100APPLE/IMG_0003.MOV"];
        Assert.Equal((PhotoKind.Video, 12.5), (video.Kind, video.DurationSeconds));
        Assert.Equal(PhotoKind.Screenshot, byId["DCIM/100APPLE/IMG_0004.PNG"].Kind);
        Assert.True(byId["DCIM/101APPLE/IMG_0101.HEIC"].InCloudOnly);
        Assert.Null(byId["DCIM/100APPLE/IMG_0007.JPG"].TakenAt);

        var albums = index.Albums.ToDictionary(a => a.Id);
        Assert.Equal(6, albums["all"].Count);
        Assert.Equal((AlbumKind.Favorites, 1), (albums["favorites"].Kind, albums["favorites"].Count));
        Assert.Equal(1, albums["videos"].Count);
        Assert.Equal(1, albums["screenshots"].Count);
        Assert.Equal(1, albums["live"].Count);
        Assert.Equal(("Trip", AlbumKind.User, 2), (albums["album:ALBUM-TRIP"].Name, albums["album:ALBUM-TRIP"].Kind, albums["album:ALBUM-TRIP"].Count));
        Assert.Equal(0, albums["album:ALBUM-EMPTY"].Count);
        Assert.DoesNotContain("album:ALBUM-GONE", albums.Keys);
        Assert.DoesNotContain("album:FOLDER-1", albums.Keys);

        // Album order follows the join table: the video, then the photo (the trashed one is gone).
        Assert.Equal(["DCIM/100APPLE/IMG_0003.MOV", "DCIM/100APPLE/IMG_0001.JPG"], index.Page("album:ALBUM-TRIP", 0, 10).Items.Select(a => a.Id));
    }

    [Fact]
    public void Paging_and_unknown_albums()
    {
        var index = PhotoIndex.Build(DcimFiles, null, null);
        var page = index.Page(null, 2, 3);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(index.Assets.Count, page.Total);
        Assert.Equal(DeviceErrorCodes.NotFound, Assert.Throws<DeviceException>(() => index.Page("album:nope", 0, 10)).Code);
    }

    [Fact]
    public void Dcim_only_fallback_pairs_by_name_and_groups_by_folder()
    {
        var index = PhotoIndex.Build(DcimFiles, null, null, fallbackReason: "no database");

        Assert.Equal(PhotoIndexMode.DcimOnly, index.Mode);
        Assert.Equal("no database", index.FallbackReason);
        Assert.Null(index.VideoBytes);
        var byId = index.Assets.ToDictionary(a => a.Id);
        Assert.Equal(7, byId.Count); // 8 files, the Live Photo's MOV folded into its HEIC
        Assert.Equal(PhotoKind.LivePhoto, byId["DCIM/100APPLE/IMG_0002.HEIC"].Kind);
        Assert.Equal("DCIM/100APPLE/IMG_0002.MOV", byId["DCIM/100APPLE/IMG_0002.HEIC"].LivePairId);
        Assert.Equal(PhotoKind.Video, byId["DCIM/100APPLE/IMG_0003.MOV"].Kind);
        Assert.Equal(PhotoKind.Screenshot, byId["DCIM/100APPLE/IMG_0004.PNG"].Kind);
        // Newest first by name when there are no dates.
        Assert.Equal("DCIM/100APPLE/IMG_0007.JPG", index.Assets[0].Id);

        var albums = index.Albums.ToDictionary(a => a.Id);
        Assert.Equal(7, albums["all"].Count);
        Assert.Equal((AlbumKind.Folder, 7), (albums["dcim:100APPLE"].Kind, albums["dcim:100APPLE"].Count));
        Assert.DoesNotContain("favorites", albums.Keys);
    }

    [Theory]
    [InlineData("IMG_0001.JPG", true)]
    [InlineData("IMG_0001.heic", true)]
    [InlineData("IMG_0001.MOV", true)]
    [InlineData("IMG_0001.AAE", false)]
    [InlineData(".MISC", false)]
    public void Media_file_filter(string name, bool expected) => Assert.Equal(expected, PhotoIndex.IsMediaFile(name));

    [Fact]
    public void Wal_copy_without_shm_opens_read_only_and_sees_uncheckpointed_rows()
    {
        var live = Path.Combine(_dir, "live.sqlite");
        using var writer = new SqliteConnection($"Data Source={live};Pooling=False");
        writer.Open();
        using (var cmd = writer.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;" + Fixture.Text("Photos", "ios17.sql");
            cmd.ExecuteNonQuery();
        }
        // Copy while the writer still holds it open, as the phone would: main file + -wal, no -shm.
        var copy = Path.Combine(_dir, "copy.sqlite");
        File.Copy(live, copy);
        File.Copy(live + "-wal", copy + "-wal");
        Assert.True(new FileInfo(copy + "-wal").Length > 0);

        using var db = PhotosDatabase.Open(copy);
        Assert.Equal(7, db.ReadAssets().Count);
    }

    [Fact]
    public void Unknown_schema_is_not_supported()
    {
        var path = Path.Combine(_dir, "other.sqlite");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE ZSOMETHING (Z_PK INTEGER PRIMARY KEY)";
            cmd.ExecuteNonQuery();
        }
        Assert.Throws<NotSupportedException>(() => PhotosDatabase.Open(path));
    }

    [Fact]
    public void Jpeg_size_is_read_from_the_frame_header()
    {
        Assert.Equal((320, 240), JpegInfo.Size(TinyJpeg(320, 240)));
        Assert.Null(JpegInfo.Size([0x89, 0x50, 0x4E, 0x47]));
    }

    /// <summary>SOI, an APP0 segment, and a SOF0 header: enough for the size reader.</summary>
    internal static byte[] TinyJpeg(int width, int height, int padding = 0) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3,
        1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
        .. new byte[padding],
        0xFF, 0xD9,
    ];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
