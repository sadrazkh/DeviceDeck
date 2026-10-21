using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Device.Photos;

/// <summary>
/// The phone's photos as one list (newest first) plus albums over it. Asset ids are DCIM-relative paths
/// ("DCIM/100APPLE/IMG_0001.HEIC"), so they stay valid across reconnects and work without Photos.sqlite.
/// </summary>
public sealed class PhotoIndex
{
    public const string AllAlbum = "all";
    public const string FavoritesAlbum = "favorites";
    public const string VideosAlbum = "videos";
    public const string ScreenshotsAlbum = "screenshots";
    public const string LiveAlbum = "live";
    public const string HiddenAlbum = "hidden";
    public const string UserAlbumPrefix = "album:";
    public const string FolderAlbumPrefix = "dcim:";

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".heic", ".heif", ".jpg", ".jpeg", ".png", ".gif", ".tif", ".tiff", ".dng", ".webp" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mov", ".mp4", ".m4v", ".3gp" };

    private readonly Dictionary<string, List<int>> _albumMembers;
    private readonly HashSet<string> _dcimFiles;

    private PhotoIndex(List<PhotoAsset> assets, List<PhotoAlbum> albums, Dictionary<string, List<int>> members,
        HashSet<string> dcimFiles, PhotoIndexMode mode, string? schemaVariant, string? fallbackReason, string? note)
    {
        Note = note;
        Assets = assets;
        Albums = albums;
        _albumMembers = members;
        _dcimFiles = dcimFiles;
        Mode = mode;
        SchemaVariant = schemaVariant;
        FallbackReason = fallbackReason;
    }

    public IReadOnlyList<PhotoAsset> Assets { get; }

    public IReadOnlyList<PhotoAlbum> Albums { get; }

    public PhotoIndexMode Mode { get; }

    /// <summary>"iOS 14+" / "iOS 9-13" when Photos.sqlite was used.</summary>
    public string? SchemaVariant { get; }

    /// <summary>Why the plain DCIM listing was used instead of Photos.sqlite.</summary>
    public string? FallbackReason { get; }

    /// <summary>How Photos.sqlite was obtained (copied or reused, sizes, speed), for diagnostics.</summary>
    public string? Note { get; }

    /// <summary>Assets the user hid in Photos: listed only in the "hidden" album.</summary>
    public int HiddenCount { get; private init; }

    private IReadOnlyDictionary<string, string> Uuids { get; init; } = new Dictionary<string, string>();

    private IReadOnlySet<string> HiddenIds { get; init; } = new HashSet<string>();

    private IReadOnlyDictionary<string, string> EditStamps { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// When Photos.sqlite says the asset was edited: a stamp that changes with every new edit (the adjustment time, or
    /// "edited" where the database has none). Null when it wasn't edited, or without the database.
    /// </summary>
    public string? EditStampOf(string assetId) => EditStamps.GetValueOrDefault(assetId);

    /// <summary>The asset's ZUUID in Photos.sqlite (stable for the life of the photo), or null without the database.</summary>
    public string? UuidOf(string assetId) => Uuids.GetValueOrDefault(assetId);

    public bool IsHidden(string assetId) => HiddenIds.Contains(assetId);

    /// <summary>True for any media file in DCIM, including the video halves of Live Photos.</summary>
    public bool IsKnownFile(string assetId) => _dcimFiles.Contains(assetId);

    public Page<PhotoAsset> Page(string? albumId, int offset, int limit)
    {
        var members = _albumMembers.GetValueOrDefault(albumId ?? AllAlbum)
                      ?? throw new DeviceException(DeviceErrorCodes.NotFound, $"No album '{albumId}' on this iPhone.");
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 0, 10_000);
        var items = members.Skip(offset).Take(limit).Select(i => Assets[i]).ToList();
        return new Page<PhotoAsset>(items, members.Count);
    }

    /// <summary>Replaces an asset (after its size and date were read from the phone) so later pages reuse it.</summary>
    internal void Update(PhotoAsset asset)
    {
        var list = (List<PhotoAsset>)Assets;
        var i = list.FindIndex(a => a.Id == asset.Id);
        if (i >= 0)
            list[i] = asset;
    }

    public static bool IsMediaFile(string name)
    {
        if (name.StartsWith('.'))
            return false;
        var ext = Path.GetExtension(name);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext);
    }

    public static bool IsVideoFile(string name) => VideoExtensions.Contains(Path.GetExtension(name));

    /// <param name="dcimFiles">Every media file under DCIM, as "DCIM/&lt;folder&gt;/&lt;name&gt;".</param>
    /// <param name="rows">Photos.sqlite assets, or null to index DCIM alone.</param>
    public static PhotoIndex Build(IReadOnlyCollection<string> dcimFiles, IReadOnlyList<PhotoRow>? rows, IReadOnlyList<AlbumRow>? albumRows,
        string? schemaVariant = null, string? fallbackReason = null, string? note = null)
    {
        var files = new HashSet<string>(dcimFiles, StringComparer.OrdinalIgnoreCase);
        var actualCase = dcimFiles.ToDictionary(f => f, f => f, StringComparer.OrdinalIgnoreCase);
        var assets = new List<PhotoAsset>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idByPk = new Dictionary<long, string>();
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var edits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (rows is not null)
        {
            foreach (var row in rows)
            {
                var path = row.Path;
                if (!path.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase))
                    continue;
                covered.Add(path);
                var livePair = row.KindSubtype == 2 ? PairedVideo(path, files, actualCase) : null;
                if (livePair is not null)
                    covered.Add(livePair);
                if (row.Trashed)
                    continue;

                var id = actualCase.GetValueOrDefault(path) ?? path;
                // Hidden photos stay out of the library and albums, as in the Photos app; they get their own album.
                if (row.Hidden)
                    hidden.Add(id);
                if (row.Uuid is { Length: > 0 } uuid)
                    uuids[id] = uuid;
                if (row.HasAdjustments)
                    edits[id] = row.AdjustedAt is { } at ? at.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) : "edited";
                var kind = row.Kind == 1 ? PhotoKind.Video
                    : row.KindSubtype == 2 ? PhotoKind.LivePhoto
                    : row.KindSubtype == 10 || row.DetectedScreenshot ? PhotoKind.Screenshot
                    : PhotoKind.Photo;
                if (!row.Hidden)
                    idByPk[row.Pk] = id;
                assets.Add(new PhotoAsset(
                    Id: id,
                    FileName: row.FileName,
                    Kind: kind,
                    TakenAt: row.DateCreated,
                    Size: row.OriginalFileSize ?? 0,
                    Width: row.Width is > 0 ? row.Width : null,
                    Height: row.Height is > 0 ? row.Height : null,
                    DurationSeconds: row.Kind == 1 && row.DurationSeconds is > 0 ? row.DurationSeconds : null,
                    IsFavorite: row.Favorite,
                    InCloudOnly: !files.Contains(path),
                    LivePairId: livePair));
            }
        }

        // Files Photos.sqlite does not know (no database, or taken after the copy): pair by base name.
        var loose = dcimFiles.Where(f => !covered.Contains(f)).ToList();
        foreach (var group in loose.GroupBy(f => Path.ChangeExtension(f, null), StringComparer.OrdinalIgnoreCase))
        {
            var image = group.FirstOrDefault(f => !IsVideoFile(f));
            var video = group.FirstOrDefault(IsVideoFile);
            foreach (var f in group)
            {
                if (f == video && image is not null)
                    continue; // the motion half of a Live Photo
                var isVideo = IsVideoFile(f);
                var kind = isVideo ? PhotoKind.Video
                    : video is not null ? PhotoKind.LivePhoto
                    : Path.GetExtension(f).Equals(".png", StringComparison.OrdinalIgnoreCase) ? PhotoKind.Screenshot
                    : PhotoKind.Photo;
                assets.Add(new PhotoAsset(f, Path.GetFileName(f), kind, null, 0, null, null, null, false, false,
                    kind == PhotoKind.LivePhoto ? video : null));
            }
        }

        assets.Sort((a, b) =>
        {
            var byDate = Nullable.Compare(b.TakenAt, a.TakenAt);
            return byDate != 0 ? byDate : string.CompareOrdinal(b.Id, a.Id);
        });

        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < assets.Count; i++)
            position[assets[i].Id] = i;

        var albums = new List<PhotoAlbum>();
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        void AddAlbum(string id, string name, AlbumKind kind, List<int> list, bool keepEmpty = false)
        {
            if (list.Count == 0 && !keepEmpty)
                return;
            members[id] = list;
            albums.Add(new PhotoAlbum(id, name, kind, list.Count));
        }
        List<int> Where(Func<PhotoAsset, bool> predicate) =>
            Enumerable.Range(0, assets.Count).Where(i => !hidden.Contains(assets[i].Id) && predicate(assets[i])).ToList();

        AddAlbum(AllAlbum, "All photos", AlbumKind.All, Where(_ => true), keepEmpty: true);
        if (rows is not null)
            AddAlbum(FavoritesAlbum, "Favorites", AlbumKind.Favorites, Where(a => a.IsFavorite));
        AddAlbum(VideosAlbum, "Videos", AlbumKind.Videos, Where(a => a.Kind == PhotoKind.Video));
        AddAlbum(ScreenshotsAlbum, "Screenshots", AlbumKind.Screenshots, Where(a => a.Kind == PhotoKind.Screenshot));
        AddAlbum(LiveAlbum, "Live Photos", AlbumKind.Live, Where(a => a.Kind == PhotoKind.LivePhoto));
        // AlbumKind has no Hidden yet (proposed); User is the closest fit until it does.
        AddAlbum(HiddenAlbum, "Hidden", AlbumKind.User,
            Enumerable.Range(0, assets.Count).Where(i => hidden.Contains(assets[i].Id)).ToList());

        if (rows is not null && albumRows is not null)
        {
            foreach (var album in albumRows.OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                var list = album.AssetPks
                    .Select(pk => idByPk.GetValueOrDefault(pk))
                    .Where(id => id is not null && position.ContainsKey(id))
                    .Select(id => position[id!])
                    .Distinct()
                    .ToList();
                AddAlbum(UserAlbumPrefix + album.Uuid, album.Title, AlbumKind.User, list, keepEmpty: true);
            }
        }
        else
        {
            foreach (var folder in assets.Select(a => FolderOf(a.Id)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                AddAlbum(FolderAlbumPrefix + folder, folder, AlbumKind.Folder, Where(a => FolderOf(a.Id).Equals(folder, StringComparison.OrdinalIgnoreCase)));
        }

        return new PhotoIndex(assets, albums, members, files,
            rows is null ? PhotoIndexMode.DcimOnly : PhotoIndexMode.Database, schemaVariant, fallbackReason, note)
        {
            HiddenCount = hidden.Count,
            Uuids = uuids,
            HiddenIds = hidden,
            EditStamps = edits,
        };
    }

    private static string? PairedVideo(string imagePath, HashSet<string> files, Dictionary<string, string> actualCase)
    {
        foreach (var ext in new[] { ".MOV", ".mov", ".MP4" })
        {
            var candidate = Path.ChangeExtension(imagePath, ext);
            if (files.Contains(candidate))
                return actualCase.GetValueOrDefault(candidate) ?? candidate;
        }
        return null;
    }

    private static string FolderOf(string id)
    {
        var parts = id.Split('/');
        return parts.Length >= 3 ? parts[1] : "DCIM";
    }
}

public enum PhotoIndexMode { Database, DcimOnly }
