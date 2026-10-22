using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.PhotoDelete;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>The phone's side of a delete: DCIM files with sizes, and a library built from them like Photos.sqlite would describe it.</summary>
internal sealed class FakeCameraRoll : IPhoneCameraRoll
{
    private PhotoIndex? _index;

    /// <summary>"DCIM/100APPLE/IMG_0001.HEIC" → bytes.</summary>
    public Dictionary<string, long> Disk { get; } = new(StringComparer.Ordinal);

    /// <summary>Files kept on disk that the library marks deleted (Recently Deleted).</summary>
    public HashSet<string> Trashed { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Edited { get; } = new(StringComparer.Ordinal);

    /// <summary>When an asset was taken, where a test cares; others get a minute apart from 19:10.</summary>
    public Dictionary<string, DateTimeOffset> Taken { get; } = new(StringComparer.Ordinal);

    /// <summary>Assets the library lists whose original is in iCloud only (no file in DCIM).</summary>
    public Dictionary<string, long> CloudOnly { get; } = new(StringComparer.Ordinal);

    /// <summary>Photos.sqlite was read (sizes known); false: the DCIM listing alone (sizes 0).</summary>
    public bool WithDatabase { get; set; } = true;

    public bool? ICloud { get; set; }

    public int Refreshes { get; private set; }

    /// <summary>After this many index reads, the next ones fail as if the cable came out.</summary>
    public int? FailIndexAfter { get; set; }

    public FakeCameraRoll Add(string path, long size)
    {
        Disk[path] = size;
        return this;
    }

    public Task<PhotoIndex> GetIndexAsync(bool refresh, CancellationToken ct)
    {
        if (FailIndexAfter is { } limit && Refreshes >= limit)
        {
            throw new DeviceDeck.Core.Abstractions.DeviceException(DeviceDeck.Core.Abstractions.DeviceErrorCodes.Disconnected, "The iPhone was disconnected.");
        }

        if (refresh || _index is null)
        {
            Refreshes++;
            _index = Build();
        }

        return Task.FromResult(_index);
    }

    public PhotoIndex? CachedIndex => _index;

    public Task<IReadOnlyDictionary<string, long?>> StatAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, long?>>(paths.Distinct().ToDictionary(p => p, p => Disk.TryGetValue(p, out var s) ? s : (long?)null, StringComparer.Ordinal));

    public Task<IReadOnlyList<string>> ListDcimAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Disk.Keys.ToList());

    public bool? ICloudPhotosHint(PhotoIndex? index) => ICloud;

    private PhotoIndex Build()
    {
        var files = Disk.Keys.ToList();
        if (!WithDatabase)
        {
            return PhotoIndex.Build(files, null, null);
        }

        var stems = files.Where(f => !PhotoIndex.IsVideoFile(f)).Select(f => Path.ChangeExtension(f, null)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<PhotoRow>();
        var pk = 0;
        foreach (var f in files.Concat(CloudOnly.Keys).Order(StringComparer.Ordinal))
        {
            var video = PhotoIndex.IsVideoFile(f);
            if (video && stems.Contains(Path.ChangeExtension(f, null)))
            {
                continue; // a Live Photo's motion half: not an asset of its own
            }

            var live = !video && files.Contains(Path.ChangeExtension(f, ".MOV"));
            var slash = f.LastIndexOf('/');
            rows.Add(new PhotoRow(++pk, f[..slash], f[(slash + 1)..], video ? 1 : 0, live ? 2 : 0,
                Taken.TryGetValue(f, out var taken) ? taken : new DateTimeOffset(2026, 10, 21, 19, 10, 0, TimeSpan.Zero).AddMinutes(pk), false, false, Trashed.Contains(f),
                4032, 3024, video ? 10 : null, "UUID-" + pk, false, Disk.TryGetValue(f, out var size) ? size : CloudOnly[f], f[(slash + 1)..], HasAdjustments: Edited.Contains(f)));
        }

        return PhotoIndex.Build(files, rows, []);
    }
}
