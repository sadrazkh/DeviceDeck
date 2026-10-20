using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.AppFiles;

/// <summary>Shared pieces of the app-files feature: counting a folder's contents and naming a root.</summary>
public static class AppFilesHelpers
{
    /// <summary>
    /// Counts what is under each path by listing folder after folder. A folder the phone won't list is counted as
    /// far as it can be read; a path that is gone counts as zero.
    /// </summary>
    public static async Task<FolderUsage> MeasureAsync(IDeviceFileSystem fs, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var items = new List<PathUsage>(paths.Count);
        foreach (var path in paths)
        {
            var entry = await fs.StatAsync(path, ct).ConfigureAwait(false);
            if (entry is null)
            {
                items.Add(new PathUsage(path, 0, 0, 0));
                continue;
            }

            if (!entry.IsDirectory)
            {
                items.Add(new PathUsage(path, 1, 0, entry.Size));
                continue;
            }

            int files = 0, folders = 0;
            long bytes = 0;
            var pending = new Stack<string>();
            pending.Push(entry.Path);
            while (pending.TryPop(out var folder))
            {
                try
                {
                    await foreach (var child in fs.ListAsync(folder, ct).ConfigureAwait(false))
                    {
                        if (child.IsDirectory)
                        {
                            folders++;
                            pending.Push(child.Path);
                        }
                        else
                        {
                            files++;
                            bytes += child.Size;
                        }
                    }
                }
                catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound)
                {
                    // Private or removed meanwhile: what could be read still counts.
                }
            }

            items.Add(new PathUsage(path, files, folders, bytes));
        }

        return FolderUsage.Of(items);
    }

    /// <summary>
    /// The name of a root for job titles and folder names on the PC: the file root's label, or the app the folder
    /// belongs to ("WhatsApp", or "WhatsApp (Media)" when the app has more than one folder). Null when the root is unknown.
    /// </summary>
    public static async Task<string?> RootLabelAsync(IDevice device, string rootKey, CancellationToken ct)
    {
        var roots = await device.GetFileRootsAsync(ct).ConfigureAwait(false);
        if (roots.FirstOrDefault(r => r.Key == rootKey) is { } root)
        {
            return root.Label;
        }

        if (device is not IAppFilesSource source)
        {
            return null;
        }

        var overview = await source.GetAppFilesAsync(ct).ConfigureAwait(false);
        foreach (var entry in overview.Entries)
        {
            if (entry.Folders.FirstOrDefault(f => f.RootKey == rootKey) is { } folder)
            {
                return entry.Folders.Count > 1 ? $"{entry.Name} ({folder.Label})" : entry.Name;
            }
        }

        return null;
    }
}
