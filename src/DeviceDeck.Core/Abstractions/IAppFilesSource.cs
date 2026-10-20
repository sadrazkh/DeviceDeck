namespace DeviceDeck.Core.Abstractions;

// Round 2, app files. Optional: an IDevice that also implements this lists the apps that share files with a
// computer and the folders they use. Every folder is a file root that IDevice.OpenFileSystemAsync opens, so
// browsing, files.copyToDevice and files.copyToPc work on it unchanged.

public interface IAppFilesSource
{
    /// <summary>
    /// iPhone: the apps with File Sharing on (house_arrest VendDocuments). Android: the apps with a folder under
    /// Android/media or Android/data (the latter only where the phone lets adb read it), then Documents and Download.
    /// </summary>
    Task<AppFilesOverview> GetAppFilesAsync(CancellationToken ct);

    /// <summary>Files, folders and bytes under each path of one root, counted all the way down. A path that is gone counts as zero.</summary>
    Task<FolderUsage> MeasureAsync(string rootKey, IReadOnlyList<string> paths, CancellationToken ct);
}

public enum AppFilesKind { App, SharedFolder }

/// <summary>Can adb read the apps' private folders (Android/data)? NotApplicable on an iPhone.</summary>
public enum AppDataAccess { NotApplicable, Allowed, Blocked }

/// <param name="RootKey">
/// Opens with IDevice.OpenFileSystemAsync: "app:&lt;bundleId&gt;" (an iPhone app's Documents), "appmedia:&lt;package&gt;"
/// (Android/media/&lt;package&gt;), "appdata:&lt;package&gt;" (Android/data/&lt;package&gt;), "shared:Documents", "shared:Download".
/// </param>
/// <param name="Label">The folder's name for a switcher when an app has more than one: "Documents", "Media", "Data", "Download".</param>
/// <param name="Empty">True when the folder holds no file at all (Android checks; an iPhone reports false and lets the listing say).</param>
public sealed record AppFolder(string RootKey, string Label, bool Empty = false);

/// <param name="Id">The bundle id or package name; for a shared folder, its root key.</param>
/// <param name="Name">The app's name when the provider knows it, else its id.</param>
/// <param name="AppId">The app whose icon to show (icon/{deviceId}/{appId}); null for a shared folder.</param>
/// <param name="Folders">At least one. The first is where a drop on the app's card goes.</param>
public sealed record AppFilesEntry(string Id, string Name, AppFilesKind Kind, string? AppId, IReadOnlyList<AppFolder> Folders);

/// <param name="Entries">Apps by name, then shared folders.</param>
public sealed record AppFilesOverview(IReadOnlyList<AppFilesEntry> Entries, AppDataAccess PrivateData);

/// <param name="Folders">Folders inside the path, not counting the path itself.</param>
public sealed record PathUsage(string Path, int Files, int Folders, long Bytes);

/// <summary>One <see cref="PathUsage"/> per path asked for, in order, and their totals (a path inside another one, or asked for twice, counts once).</summary>
public sealed record FolderUsage(IReadOnlyList<PathUsage> Items, int Files, int Folders, long Bytes)
{
    public static FolderUsage Of(IReadOnlyList<PathUsage> items)
    {
        static string Norm(string path) => "/" + path.Trim().Trim('/');
        var outer = items
            .DistinctBy(i => Norm(i.Path), StringComparer.Ordinal)
            .Where(i => !items.Any(o => Norm(o.Path) != Norm(i.Path) && (Norm(o.Path) == "/" || Norm(i.Path).StartsWith(Norm(o.Path) + "/", StringComparison.Ordinal))))
            .ToList();
        return new(items, outer.Sum(i => i.Files), outer.Sum(i => i.Folders), outer.Sum(i => i.Bytes));
    }
}
