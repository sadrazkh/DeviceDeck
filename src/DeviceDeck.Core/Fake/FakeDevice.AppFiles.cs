using System.Diagnostics.CodeAnalysis;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.AppFiles;

namespace DeviceDeck.Core.Fake;

// App files on the simulated phones. An iPhone's apps are its "app:" roots; an Android's app folders are views
// into its internal storage (Android/media/<package>, Android/data/<package> unless that folder is denied,
// Documents, Download), opened as roots of their own so the browser and the queue treat them like any root.
public sealed partial class FakeDevice : IAppFilesSource
{
    private const string InternalRoot = "internal";

    public Task<AppFilesOverview> GetAppFilesAsync(CancellationToken ct)
    {
        EnsureConnected();
        Require(DeviceCapability.AppDocuments, "share app files");
        lock (Gate)
        {
            return Task.FromResult(Summary.Platform == DevicePlatform.Ios ? IphoneAppFiles() : AndroidAppFiles());
        }
    }

    public async Task<FolderUsage> MeasureAsync(string rootKey, IReadOnlyList<string> paths, CancellationToken ct)
    {
        await using var fs = await OpenFileSystemAsync(rootKey, ct).ConfigureAwait(false);
        return await AppFilesHelpers.MeasureAsync(fs, paths, ct).ConfigureAwait(false);
    }

    private AppFilesOverview IphoneAppFiles()
    {
        var entries = Spec.Roots
            .Where(r => r.Root.Kind == FileRootKind.AppDocuments)
            .OrderBy(r => r.Root.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new AppFilesEntry(r.Root.AppId ?? r.Root.Key, r.Root.Label, AppFilesKind.App, r.Root.AppId,
                [new AppFolder(r.Root.Key, "Documents")]))
            .ToList();
        return new AppFilesOverview(entries, AppDataAccess.NotApplicable);
    }

    private AppFilesOverview AndroidAppFiles()
    {
        if (!Roots.TryGetValue(InternalRoot, out var storage))
        {
            return new AppFilesOverview([], AppDataAccess.Blocked);
        }

        var folders = new SortedDictionary<string, List<AppFolder>>(StringComparer.Ordinal);
        void Add(string prefix, string label, FakeFileNode? parent)
        {
            foreach (var app in parent?.Children?.Values.Where(c => c.IsDirectory) ?? [])
            {
                if (!folders.TryGetValue(app.Name, out var list))
                {
                    folders[app.Name] = list = [];
                }

                list.Add(new AppFolder(prefix + app.Name, label, Empty: !HasFile(app)));
            }
        }

        var data = Child(storage.Tree, "Android", "data");
        var dataAccess = data is null or { Denied: true } ? AppDataAccess.Blocked : AppDataAccess.Allowed;
        Add(AppFilesRootKeys.MediaPrefix, "Media", Child(storage.Tree, "Android", "media"));
        if (dataAccess == AppDataAccess.Allowed)
        {
            Add(AppFilesRootKeys.DataPrefix, "Data", data);
        }

        var entries = folders
            .Select(f => new AppFilesEntry(f.Key, Spec.Apps.FirstOrDefault(a => a.Info.Id == f.Key)?.Info.Name ?? f.Key, AppFilesKind.App, f.Key, f.Value))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var shared in AppFilesRootKeys.SharedFolders)
        {
            if (Child(storage.Tree, shared) is { IsDirectory: true } node)
            {
                var key = AppFilesRootKeys.SharedPrefix + shared;
                entries.Add(new AppFilesEntry(key, shared, AppFilesKind.SharedFolder, null, [new AppFolder(key, shared, Empty: !HasFile(node))]));
            }
        }

        return new AppFilesOverview(entries, dataAccess);
    }

    /// <summary>An app-files root of the simulated Android: a view into its internal storage.</summary>
    private bool TryAppFilesRoot(string rootKey, [NotNullWhen(true)] out FakeRoot? root)
    {
        root = null;
        if (Summary.Platform != DevicePlatform.Android || !AppFilesRootKeys.TryParse(rootKey, out var segments) || !Roots.TryGetValue(InternalRoot, out var storage))
        {
            return false;
        }

        lock (Gate)
        {
            if (segments[..^1].SequenceEqual(["Android", "data"]) && Child(storage.Tree, "Android", "data") is { Denied: true })
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "This phone doesn't let a computer open Android/data.");
            }

            if (Child(storage.Tree, segments) is not { IsDirectory: true } node)
            {
                return false;
            }

            root = new FakeRoot(new FileRoot(rootKey, segments[^1], FileRootKind.InternalStorage), node);
            return true;
        }
    }

    private static FakeFileNode? Child(FakeFileNode node, params string[] path)
    {
        foreach (var part in path)
        {
            if (node.Children is null || !node.Children.TryGetValue(part, out var next))
            {
                return null;
            }

            node = next;
        }

        return node;
    }

    private static bool HasFile(FakeFileNode node) =>
        !node.IsDirectory || node.Children!.Values.Any(HasFile);
}
