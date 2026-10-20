using System.Globalization;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Photos;

/// <summary>One MediaStore row (images and videos share the files table, so ids are unique across both).</summary>
internal sealed record MediaItem(
    long Id,
    string FileName,
    string Path,
    bool IsVideo,
    string? MimeType,
    long Size,
    DateTimeOffset? TakenAt,
    int? Width,
    int? Height,
    double? DurationSeconds,
    string? BucketId,
    string? BucketName,
    string? RelativePath,
    bool IsFavorite)
{
    public bool IsScreenshot =>
        !IsVideo && (RelativePath?.Contains("Screenshots", StringComparison.OrdinalIgnoreCase) == true
                     || BucketName?.Equals("Screenshots", StringComparison.OrdinalIgnoreCase) == true);

    public PhotoAsset ToAsset() => new(
        Id.ToString(CultureInfo.InvariantCulture),
        FileName,
        IsVideo ? PhotoKind.Video : IsScreenshot ? PhotoKind.Screenshot : PhotoKind.Photo,
        TakenAt,
        Size,
        Width,
        Height,
        DurationSeconds,
        IsFavorite,
        InCloudOnly: false,
        LivePairId: null);
}

/// <summary>
/// Builds and reads `content query` on content://media/external/file for images (media_type 1) and videos (3).
/// "external" is the union of all external volumes on Android 10+, so SD-card media is included.
/// </summary>
internal static class MediaQuery
{
    public const string FilesUri = "content://media/external/file";

    public static IReadOnlyList<string> Projection(int sdk)
    {
        var columns = new List<string>
        {
            "_id", "_display_name", "_data", "media_type", "mime_type", "_size",
            "datetaken", "date_added", "date_modified", "width", "height", "duration", "orientation",
            "bucket_id", "bucket_display_name", "relative_path",
        };
        if (sdk >= 30)
        {
            columns.Add("is_favorite"); // MediaColumns.IS_FAVORITE arrived in Android 11
        }
        return columns;
    }

    public static string Command(int sdk, string where = "media_type IN (1,3)") =>
        $"content query --uri {FilesUri} --projection {string.Join(':', Projection(sdk))} --where {ShellQuote.Quote(where)}";

    public static IReadOnlyList<MediaItem> Parse(string output, int sdk)
    {
        var items = new List<MediaItem>();
        foreach (var row in ContentQueryParser.Parse(output, Projection(sdk)))
        {
            if (FromRow(row) is { } item)
            {
                items.Add(item);
            }
        }
        // Newest first, ties by id (newer inserts have bigger ids).
        items.Sort((a, b) =>
        {
            int byTime = Nullable.Compare(b.TakenAt, a.TakenAt);
            return byTime != 0 ? byTime : b.Id.CompareTo(a.Id);
        });
        return items;
    }

    private static MediaItem? FromRow(IReadOnlyDictionary<string, string?> row)
    {
        if (Long(row, "_id") is not { } id || row.GetValueOrDefault("_data") is not { Length: > 0 } path)
        {
            return null;
        }
        bool video = Long(row, "media_type") == 3;
        string name = row.GetValueOrDefault("_display_name") ?? path[(path.LastIndexOf('/') + 1)..];

        // DATE_TAKEN is ms since the epoch; the other two are seconds. Some camera apps leave DATE_TAKEN at 0.
        DateTimeOffset? taken = Long(row, "datetaken") is > 0 and var ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : Long(row, "date_modified") is > 0 and var modified ? DateTimeOffset.FromUnixTimeSeconds(modified)
            : Long(row, "date_added") is > 0 and var added ? DateTimeOffset.FromUnixTimeSeconds(added)
            : null;

        int? width = Int(row, "width") is > 0 and var w ? w : null;
        int? height = Int(row, "height") is > 0 and var h ? h : null;
        // MediaStore keeps the sensor's width/height; a 90°/270° orientation means it displays the other way round.
        if (Int(row, "orientation") is 90 or 270)
        {
            (width, height) = (height, width);
        }
        double? duration = video && Long(row, "duration") is > 0 and var durationMs ? durationMs / 1000.0 : null;

        return new MediaItem(
            id,
            name,
            path,
            video,
            row.GetValueOrDefault("mime_type"),
            Long(row, "_size") ?? 0,
            taken,
            width,
            height,
            duration,
            row.GetValueOrDefault("bucket_id"),
            row.GetValueOrDefault("bucket_display_name"),
            row.GetValueOrDefault("relative_path"),
            Long(row, "is_favorite") == 1);
    }

    private static long? Long(IReadOnlyDictionary<string, string?> row, string key) =>
        row.GetValueOrDefault(key) is { } value && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;

    private static int? Int(IReadOnlyDictionary<string, string?> row, string key) =>
        Long(row, key) is { } n && n is >= int.MinValue and <= int.MaxValue ? (int)n : null;

    /// <summary>Albums the way the phone's gallery groups them: all, videos, favourites, then one per bucket (folder).</summary>
    public static IReadOnlyList<PhotoAlbum> Albums(IReadOnlyList<MediaItem> items)
    {
        var albums = new List<PhotoAlbum> { new("all", "All photos", AlbumKind.All, items.Count) };
        int videos = items.Count(i => i.IsVideo);
        if (videos > 0)
        {
            albums.Add(new PhotoAlbum("videos", "Videos", AlbumKind.Videos, videos));
        }
        int favorites = items.Count(i => i.IsFavorite);
        if (favorites > 0)
        {
            albums.Add(new PhotoAlbum("favorites", "Favourites", AlbumKind.Favorites, favorites));
        }
        foreach (var bucket in items.Where(i => i.BucketId != null).GroupBy(i => i.BucketId!)
                     .OrderByDescending(g => g.Max(i => i.TakenAt)))
        {
            var first = bucket.First();
            string relative = first.RelativePath ?? "";
            AlbumKind kind = relative.TrimEnd('/').Equals("DCIM/Camera", StringComparison.OrdinalIgnoreCase) ? AlbumKind.Camera
                : first.IsScreenshot ? AlbumKind.Screenshots
                : AlbumKind.Folder;
            albums.Add(new PhotoAlbum("bucket:" + bucket.Key, first.BucketName ?? "Folder", kind, bucket.Count()));
        }
        return albums;
    }

    public static IEnumerable<MediaItem> Filter(IReadOnlyList<MediaItem> items, string? albumId) => albumId switch
    {
        null or "" or "all" => items,
        "videos" => items.Where(i => i.IsVideo),
        "favorites" => items.Where(i => i.IsFavorite),
        _ when albumId.StartsWith("bucket:", StringComparison.Ordinal) => items.Where(i => i.BucketId == albumId["bucket:".Length..]),
        _ => throw new DeviceException(DeviceErrorCodes.PathNotFound, "That album doesn't exist on the phone."),
    };
}
