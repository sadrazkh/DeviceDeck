using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.AppFiles;

namespace DeviceDeck.Ios.Device;

/// <summary>
/// App files on an iPhone: the apps with File Sharing on, each with its Documents folder, which house_arrest
/// vends over AFC ("app:&lt;bundleId&gt;" roots). Nothing else in an app's container is reachable, by design.
/// </summary>
public sealed partial class IosDevice : IAppFilesSource
{
    public async Task<AppFilesOverview> GetAppFilesAsync(CancellationToken ct)
    {
        var roots = await GetFileRootsAsync(ct).ConfigureAwait(false);
        var entries = roots
            .Where(r => r.Kind == FileRootKind.AppDocuments && r.AppId is not null)
            .Select(r => new AppFilesEntry(r.AppId!, r.Label, AppFilesKind.App, r.AppId, [new AppFolder(r.Key, "Documents")]))
            .ToList();
        return new AppFilesOverview(entries, AppDataAccess.NotApplicable);
    }

    /// <summary>Lists folder after folder over one house_arrest connection (one AFC round trip per entry).</summary>
    public async Task<FolderUsage> MeasureAsync(string rootKey, IReadOnlyList<string> paths, CancellationToken ct)
    {
        await using var fs = await OpenFileSystemAsync(rootKey, ct).ConfigureAwait(false);
        return await AppFilesHelpers.MeasureAsync(fs, paths, ct).ConfigureAwait(false);
    }
}
