using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

// The clean-up interfaces on the simulated phones. Contents aren't stored, so a fake asset's bytes are its
// name, size and date: two assets that agree on all three are byte-for-byte copies (the same photo saved twice).
// The Android fake also answers the app-folder scan and keeps app caches (a quarter of each app's data) that
// a trim empties; the iPhone fake answers those two with not_supported, as a real iPhone does.
public sealed partial class FakeDevice : IMediaHashSource, IAppFileScanner, IAppCacheCleaner
{
    private static readonly string[] AppFolders = ["/Android/data", "/Android/media", "/Download", "/Documents"];

    private Dictionary<string, long>? _caches;

    public async Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct)
    {
        EnsureConnected();
        var wanted = assetIds.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, PhotoAsset>(StringComparer.Ordinal);
        for (var offset = 0; ;)
        {
            var page = await Photos.GetAssetsAsync(null, offset, 1000, ct).ConfigureAwait(false);
            foreach (var asset in page.Items.Where(a => wanted.Contains(a.Id)))
            {
                found[asset.Id] = asset;
            }

            offset += page.Items.Count;
            if (page.Items.Count == 0 || offset >= page.Total)
            {
                break;
            }
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long done = 0;
        foreach (var id in assetIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!found.TryGetValue(id, out var asset) || asset.InCloudOnly)
            {
                continue;
            }

            var length = prefixBytes is { } prefix ? Math.Min(prefix, asset.Size) : asset.Size;
            var content = string.Join('\n', asset.FileName, asset.Size.ToString(CultureInfo.InvariantCulture), asset.TakenAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                length == asset.Size ? "whole" : $"prefix:{length.ToString(CultureInfo.InvariantCulture)}");
            hashes[id] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            done += length;
            progress?.Report(done);
            await FakeIo.DelayAsync(this, TimeSpan.Zero, ct).ConfigureAwait(false);
        }

        return hashes;
    }

    public Task<AppFileScan> FindLargeAppFilesAsync(long minBytes, int limit, CancellationToken ct)
    {
        EnsureConnected();
        if (Summary.Platform != DevicePlatform.Android || !Roots.TryGetValue("internal", out var root))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{Summary.Name} has no shared app folders.");
        }

        var hits = new List<AppFileHit>();
        var unreadable = new List<string>();
        lock (Gate)
        {
            foreach (var folder in AppFolders)
            {
                var node = Find(root.Tree, folder);
                if (node is null)
                {
                    continue;
                }

                if (node.Denied)
                {
                    unreadable.Add(folder.TrimStart('/'));
                    continue;
                }

                Collect(node, folder, minBytes, hits, unreadable);
            }
        }

        return Task.FromResult(new AppFileScan(hits.OrderByDescending(h => h.Size).ThenBy(h => h.Path, StringComparer.Ordinal).Take(limit).ToList(), unreadable));
    }

    public async Task<AppCacheReport> GetCachesAsync(CancellationToken ct)
    {
        EnsureConnected();
        RequireAndroid();
        var installed = await InstalledAsync(ct).ConfigureAwait(false);
        lock (Gate)
        {
            var caches = Caches().Where(c => installed.Contains(c.Key)).ToList();
            return new AppCacheReport(caches.Sum(c => c.Value), caches.Select(c => new AppCacheEntry(c.Key, c.Value)).OrderByDescending(c => c.Bytes).ToList());
        }
    }

    public async Task<long> TrimCachesAsync(CancellationToken ct)
    {
        EnsureConnected();
        RequireAndroid();
        var installed = await InstalledAsync(ct).ConfigureAwait(false);
        lock (Gate)
        {
            var caches = Caches();
            long freed = 0;
            foreach (var key in caches.Keys.Where(installed.Contains).ToList())
            {
                freed += caches[key];
                caches[key] = 0;
            }

            return freed;
        }
    }

    /// <summary>An uninstalled app's cache went with it.</summary>
    private async Task<HashSet<string>> InstalledAsync(CancellationToken ct) =>
        (await Apps.ListAsync(true, ct).ConfigureAwait(false)).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

    private void RequireAndroid()
    {
        if (Summary.Platform != DevicePlatform.Android)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{Summary.Name} doesn't let the PC clear app caches.");
        }
    }

    private Dictionary<string, long> Caches() =>
        _caches ??= Spec.Apps.ToDictionary(a => a.Info.Id, a => a.Info.DataBytes / 4, StringComparer.Ordinal);

    private static FakeFileNode? Find(FakeFileNode tree, string path)
    {
        var node = tree;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.Children is null || !node.Children.TryGetValue(part, out node))
            {
                return null;
            }
        }

        return node;
    }

    private static void Collect(FakeFileNode node, string path, long minBytes, List<AppFileHit> hits, List<string> unreadable)
    {
        foreach (var child in node.Children!.Values)
        {
            var childPath = path + "/" + child.Name;
            if (child.IsDirectory)
            {
                if (child.Denied)
                {
                    unreadable.Add(childPath.TrimStart('/'));
                }
                else
                {
                    Collect(child, childPath, minBytes, hits, unreadable);
                }
            }
            else if (child.Size >= minBytes)
            {
                var parts = childPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var appId = parts is ["Android", "data" or "media", var package, ..] ? package : null;
                hits.Add(new AppFileHit("internal", childPath, child.Name, child.Size, child.Modified, appId, appId is null ? parts[0] : null));
            }
        }
    }
}
