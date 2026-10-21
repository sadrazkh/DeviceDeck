using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Device.Photos;

/// <summary>
/// Reads a copy of the phone's /PhotoData/Photos.sqlite (a Core Data store), read-only.
/// The schema differs by iOS version, so it is mapped by looking at what is there:
/// <list type="bullet">
/// <item>iOS 14 and later: assets in ZASSET.</item>
/// <item>iOS 9 – 13: assets in ZGENERICASSET.</item>
/// <item>Both: ZADDITIONALASSETATTRIBUTES (original size and name), ZGENERICALBUM (albums, ZKIND 2 = user album),
/// and an album↔asset join table whose name and column numbers (Z_26ASSETS, Z_28ALBUMS, Z_3ASSETS, …) change
/// with the Core Data model, so it is found by its column pattern.</item>
/// </list>
/// Optional columns that a version lacks are read as NULL.
/// </summary>
public sealed partial class PhotosDatabase : IDisposable
{
    private static readonly DateTimeOffset CoreDataEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection;

    private PhotosDatabase(SqliteConnection connection, PhotosSchema schema)
    {
        _connection = connection;
        Schema = schema;
    }

    public PhotosSchema Schema { get; }

    /// <summary>Opens <paramref name="path"/> read-only. Throws <see cref="NotSupportedException"/> for an unknown schema.</summary>
    public static PhotosDatabase Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return new PhotosDatabase(connection, DetectSchema(connection));
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IReadOnlyList<PhotoRow> ReadAssets()
    {
        var s = Schema;
        string A(string column) => s.AssetColumns.Contains(column) ? "a." + column : "NULL";
        string X(string column) => s.AttributesTable is not null && s.AttributeColumns.Contains(column) ? "x." + column : "NULL";
        // Older schemas say "edited" in ZHASADJUSTMENTS; iOS 27 has no such column, only ZADJUSTMENTSSTATE (0 = not edited).
        var edited = s.AssetColumns.Contains("ZHASADJUSTMENTS") ? "a.ZHASADJUSTMENTS" : A("ZADJUSTMENTSSTATE");

        var join = s.AttributesTable is null ? "" : $"LEFT JOIN {s.AttributesTable} x ON x.ZASSET = a.Z_PK";
        var sql = $"""
            SELECT a.Z_PK, {A("ZDIRECTORY")}, {A("ZFILENAME")}, {A("ZKIND")}, {A("ZKINDSUBTYPE")}, {A("ZDATECREATED")},
                   {A("ZFAVORITE")}, {A("ZHIDDEN")}, {A("ZTRASHEDSTATE")}, {A("ZWIDTH")}, {A("ZHEIGHT")}, {A("ZDURATION")},
                   {A("ZUUID")}, {A("ZISDETECTEDSCREENSHOT")}, {X("ZORIGINALFILESIZE")}, {X("ZORIGINALFILENAME")},
                   {edited}, {A("ZADJUSTMENTTIMESTAMP")}
            FROM {s.AssetTable} a {join}
            """;
        var rows = new List<PhotoRow>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var directory = r.IsDBNull(1) ? null : r.GetString(1);
            var fileName = r.IsDBNull(2) ? null : r.GetString(2);
            if (directory is null || fileName is null)
                continue;
            rows.Add(new PhotoRow(
                Pk: r.GetInt64(0),
                Directory: directory,
                FileName: fileName,
                Kind: Int(r, 3) ?? 0,
                KindSubtype: Int(r, 4) ?? 0,
                DateCreated: r.IsDBNull(5) ? null : CoreDataEpoch.AddSeconds(r.GetDouble(5)),
                Favorite: Int(r, 6) == 1,
                Hidden: Int(r, 7) == 1,
                Trashed: Int(r, 8) is > 0,
                Width: Int(r, 9),
                Height: Int(r, 10),
                DurationSeconds: r.IsDBNull(11) ? null : r.GetDouble(11),
                Uuid: r.IsDBNull(12) ? null : r.GetString(12),
                DetectedScreenshot: Int(r, 13) == 1,
                OriginalFileSize: r.IsDBNull(14) ? null : r.GetInt64(14),
                OriginalFileName: r.IsDBNull(15) ? null : r.GetString(15),
                HasAdjustments: Int(r, 16) is > 0,
                AdjustedAt: r.IsDBNull(17) ? null : CoreDataEpoch.AddSeconds(r.GetDouble(17))));
        }
        return rows;
    }

    /// <summary>User albums (ZKIND 2) that are not in the trash, with their asset Z_PKs in album order.</summary>
    public IReadOnlyList<AlbumRow> ReadAlbums()
    {
        var s = Schema;
        if (s.AlbumTable is null)
            return [];
        string C(string column) => s.AlbumColumns.Contains(column) ? column : "NULL";

        var albums = new List<AlbumRow>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT Z_PK, {C("ZUUID")}, {C("ZTITLE")}, {C("ZKIND")}
                FROM {s.AlbumTable}
                WHERE {C("ZKIND")} = 2 AND IFNULL({C("ZTRASHEDSTATE")}, 0) = 0
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                albums.Add(new AlbumRow(
                    r.GetInt64(0),
                    r.IsDBNull(1) ? r.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture) : r.GetString(1),
                    r.IsDBNull(2) ? "Untitled album" : r.GetString(2),
                    []));
            }
        }

        if (s.Join is not { } j)
            return albums;
        var members = new Dictionary<long, List<long>>();
        using (var cmd = _connection.CreateCommand())
        {
            var order = j.OrderColumn is null ? "" : $"ORDER BY {j.AlbumColumn}, {j.OrderColumn}";
            cmd.CommandText = $"SELECT {j.AlbumColumn}, {j.AssetColumn} FROM {j.Table} {order}";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.IsDBNull(1))
                    continue;
                var album = r.GetInt64(0);
                if (!members.TryGetValue(album, out var list))
                    members[album] = list = [];
                list.Add(r.GetInt64(1));
            }
        }
        return albums.Select(a => a with { AssetPks = members.GetValueOrDefault(a.Pk) ?? [] }).ToList();
    }

    public void Dispose() => _connection.Dispose();

    internal static PhotosSchema DetectSchema(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                tables.Add(r.GetString(0));
        }

        var (assetTable, variant) = tables.Contains("ZASSET") ? ("ZASSET", "iOS 14+")
            : tables.Contains("ZGENERICASSET") ? ("ZGENERICASSET", "iOS 9-13")
            : throw new NotSupportedException("Photos.sqlite has neither ZASSET nor ZGENERICASSET.");

        var assetColumns = Columns(connection, assetTable);
        if (!assetColumns.Contains("ZDIRECTORY") || !assetColumns.Contains("ZFILENAME"))
            throw new NotSupportedException($"{assetTable} lacks ZDIRECTORY/ZFILENAME.");

        var attributesTable = tables.Contains("ZADDITIONALASSETATTRIBUTES") ? "ZADDITIONALASSETATTRIBUTES" : null;
        var attributeColumns = attributesTable is null ? [] : Columns(connection, attributesTable);
        if (attributesTable is not null && !attributeColumns.Contains("ZASSET"))
            attributesTable = null;

        var albumTable = tables.Contains("ZGENERICALBUM") ? "ZGENERICALBUM" : null;
        var albumColumns = albumTable is null ? [] : Columns(connection, albumTable);

        AlbumJoin? join = null;
        foreach (var table in tables.Where(t => JoinTableName().IsMatch(t)).Order(StringComparer.Ordinal))
        {
            var columns = Columns(connection, table);
            var albumCol = columns.FirstOrDefault(c => AlbumColumnName().IsMatch(c));
            var assetCol = columns.FirstOrDefault(c => AssetColumnName().IsMatch(c));
            if (albumCol is null || assetCol is null)
                continue;
            var fok = "Z_FOK_" + assetCol[2..];
            join = new AlbumJoin(table, albumCol, assetCol, columns.Contains(fok) ? fok : null);
            break;
        }

        return new PhotosSchema(variant, assetTable, assetColumns, attributesTable, attributeColumns, albumTable, albumColumns, join);
    }

    private static HashSet<string> Columns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = connection.CreateCommand();
        // Table names come from sqlite_master, filtered to identifier characters, so quoting is enough.
        cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "", StringComparison.Ordinal)}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            columns.Add(r.GetString(1));
        return columns;
    }

    private static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : (int)r.GetInt64(i);

    [GeneratedRegex(@"^Z_\d+ASSETS$", RegexOptions.IgnoreCase)]
    private static partial Regex JoinTableName();

    [GeneratedRegex(@"^Z_\d+ALBUMS$", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumColumnName();

    [GeneratedRegex(@"^Z_\d+ASSETS$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetColumnName();
}

/// <param name="Variant">"iOS 14+" or "iOS 9-13", by which asset table exists.</param>
public sealed record PhotosSchema(
    string Variant,
    string AssetTable,
    IReadOnlySet<string> AssetColumns,
    string? AttributesTable,
    IReadOnlySet<string> AttributeColumns,
    string? AlbumTable,
    IReadOnlySet<string> AlbumColumns,
    AlbumJoin? Join);

public sealed record AlbumJoin(string Table, string AlbumColumn, string AssetColumn, string? OrderColumn);

/// <param name="Kind">ZKIND: 0 photo, 1 video.</param>
/// <param name="KindSubtype">ZKINDSUBTYPE: 1 panorama, 2 Live Photo, 10 screenshot, 100 / 101 / 102 video variants.</param>
/// <param name="HasAdjustments">ZHASADJUSTMENTS, or ZADJUSTMENTSSTATE &gt; 0 on iOS 27: edited in Photos (false where the schema has neither).</param>
/// <param name="AdjustedAt">ZADJUSTMENTTIMESTAMP: when it was last edited; a new edit changes it.</param>
public sealed record PhotoRow(
    long Pk,
    string Directory,
    string FileName,
    int Kind,
    int KindSubtype,
    DateTimeOffset? DateCreated,
    bool Favorite,
    bool Hidden,
    bool Trashed,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? Uuid,
    bool DetectedScreenshot,
    long? OriginalFileSize,
    string? OriginalFileName,
    bool HasAdjustments = false,
    DateTimeOffset? AdjustedAt = null)
{
    /// <summary>"DCIM/100APPLE/IMG_0001.HEIC" — also the asset id.</summary>
    public string Path => Directory.TrimEnd('/') + "/" + FileName;
}

public sealed record AlbumRow(long Pk, string Uuid, string Title, IReadOnlyList<long> AssetPks);
