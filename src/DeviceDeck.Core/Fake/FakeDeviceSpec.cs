using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

/// <summary>Everything one simulated phone holds. <see cref="FakeSampleData"/> builds the plan's two phones; tests build small ones.</summary>
public sealed class FakeDeviceSpec
{
    public required DeviceSummary Summary { get; init; }
    public required StorageInfo Storage { get; init; }
    public required BatteryInfo Battery { get; init; }
    public required DeviceDetails Details { get; init; }

    /// <summary>Newest first.</summary>
    public required IReadOnlyList<PhotoAsset> Assets { get; init; }

    /// <summary>Albums with their members; an album of kind All lists every asset and may leave AssetIds empty.</summary>
    public required IReadOnlyList<FakeAlbum> Albums { get; init; }

    public required IReadOnlyList<FakeRoot> Roots { get; init; }
    public required IReadOnlyList<FakeApp> Apps { get; init; }

    /// <summary>False plays an iPhone whose library has no ready-made thumbnails: GetThumbnailAsync returns null.</summary>
    public bool ThumbnailsOnPhone { get; init; } = true;

    /// <summary>Screen size for screenshots.</summary>
    public (int Width, int Height) Screen { get; init; } = (603, 1311);
}

public sealed record FakeAlbum(PhotoAlbum Album, IReadOnlyList<string> AssetIds);

public sealed record FakeRoot(FileRoot Root, FakeFileNode Tree);

public sealed record FakeApp(AppInfo Info, (int R, int G, int B) Colour);

/// <summary>A node of a simulated file tree. Content is not stored: reads produce filler bytes of the right size.</summary>
public sealed class FakeFileNode
{
    private FakeFileNode(string name, bool isDirectory)
    {
        Name = name;
        IsDirectory = isDirectory;
        Children = isDirectory ? new SortedDictionary<string, FakeFileNode>(StringComparer.Ordinal) : null;
    }

    public string Name { get; set; }
    public bool IsDirectory { get; }
    public long Size { get; set; }
    public DateTimeOffset? Modified { get; set; }
    public bool ReadOnly { get; init; }

    /// <summary>Listing or reading it fails with permission_denied (Android/data on Android 11+).</summary>
    public bool Denied { get; init; }

    public SortedDictionary<string, FakeFileNode>? Children { get; }

    public static FakeFileNode Dir(string name, params FakeFileNode[] children) => Dir(name, readOnly: false, denied: false, children);

    public static FakeFileNode Dir(string name, bool readOnly, bool denied, params FakeFileNode[] children)
    {
        var node = new FakeFileNode(name, true) { ReadOnly = readOnly, Denied = denied };
        foreach (var child in children)
        {
            node.Children![child.Name] = child;
        }

        node.Modified = children.Select(c => c.Modified).Where(m => m is not null).DefaultIfEmpty(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)).Max();
        return node;
    }

    public static FakeFileNode File(string name, long size, DateTimeOffset modified, bool readOnly = false) =>
        new(name, false) { Size = size, Modified = modified, ReadOnly = readOnly };

    public long TotalSize() => IsDirectory ? Children!.Values.Sum(c => c.TotalSize()) : Size;
}
