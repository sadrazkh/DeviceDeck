using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Cleanup;

/// <summary>
/// The fallback for <see cref="IAppFileScanner"/>: walks every AppDocuments root (an iPhone's file-sharing apps)
/// folder by folder and keeps the big files. Bounded per root, so a huge tree can't stall the analysis.
/// </summary>
internal static class AppFileWalker
{
    public static async Task<AppFileScan> ScanAsync(IDevice device, long minBytes, int limit, int maxEntriesPerRoot, Action<int, int>? progress, CancellationToken ct)
    {
        var roots = (await device.GetFileRootsAsync(ct).ConfigureAwait(false))
            .Where(r => r.Kind == FileRootKind.AppDocuments)
            .ToList();
        var hits = new List<AppFileHit>();
        var unreadable = new List<string>();
        for (var i = 0; i < roots.Count; i++)
        {
            progress?.Invoke(i, roots.Count);
            var root = roots[i];
            try
            {
                await using var fs = await device.OpenFileSystemAsync(root.Key, ct).ConfigureAwait(false);
                await WalkAsync(fs, root, minBytes, maxEntriesPerRoot, hits, ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound or DeviceErrorCodes.NotSupported or DeviceErrorCodes.Protocol)
            {
                // An app that stopped sharing, or a folder the phone won't list.
                unreadable.Add(root.Label);
            }
        }

        progress?.Invoke(roots.Count, roots.Count);
        return new AppFileScan(hits.OrderByDescending(h => h.Size).ThenBy(h => h.Path, StringComparer.Ordinal).Take(limit).ToList(), unreadable);
    }

    private static async Task WalkAsync(IDeviceFileSystem fs, FileRoot root, long minBytes, int maxEntries, List<AppFileHit> hits, CancellationToken ct)
    {
        var folders = new Queue<string>();
        folders.Enqueue("/");
        var seen = 0;
        while (folders.Count > 0 && seen < maxEntries)
        {
            var folder = folders.Dequeue();
            try
            {
                await foreach (var entry in fs.ListAsync(folder, ct).ConfigureAwait(false))
                {
                    if (++seen > maxEntries)
                    {
                        break;
                    }

                    if (entry.IsDirectory)
                    {
                        folders.Enqueue(entry.Path);
                    }
                    else if (entry.Size >= minBytes)
                    {
                        hits.Add(new AppFileHit(root.Key, entry.Path, entry.Name, entry.Size, entry.Modified, root.AppId));
                    }
                }
            }
            catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound)
            {
                // One locked or vanished sub-folder doesn't spoil the rest of the app.
            }
        }
    }
}
