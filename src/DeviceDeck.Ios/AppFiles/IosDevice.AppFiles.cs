using System.Collections.Concurrent;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.AppFiles;
using DeviceDeck.Ios.AppFiles;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Protocol.Services;

namespace DeviceDeck.Ios.Device;

/// <summary>
/// App files on an iPhone: the apps with File Sharing on, each with its Documents folder, which house_arrest
/// vends over AFC ("app:&lt;bundleId&gt;" roots). Nothing else in an app's container is reachable, by design.
/// </summary>
public sealed partial class IosDevice : IAppFilesSource
{
    /// <summary>One house_arrest connection per app for listings and small changes; see <see cref="AppDocumentsFileSystem"/>.</summary>
    private readonly ConcurrentDictionary<string, SharedConnection<AfcClient>> _appDocuments = new(StringComparer.Ordinal);

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

    /// <summary>An app's Documents. Opens the app's shared connection now, so a missing app fails here as it always did.</summary>
    private async Task<IDeviceFileSystem> OpenAppDocumentsAsync(string bundleId, CancellationToken ct)
    {
        var shared = _appDocuments.GetOrAdd(bundleId, id =>
            new SharedConnection<AfcClient>(t => UseAsync(c => VendDocumentsAsync(c, id, t)), afc => afc.IsBroken, serialize: false));
        var fs = new AppDocumentsFileSystem(shared, t => UseAsync(c => VendDocumentsAsync(c, bundleId, t)));
        await fs.WarmAsync(ct).ConfigureAwait(false);
        return fs;
    }

    private static async Task<AfcClient> VendDocumentsAsync(DeviceConnector c, string bundleId, CancellationToken ct)
    {
        var connection = await c.StartServiceAsync(ServiceNames.HouseArrest, ct).ConfigureAwait(false);
        try
        {
            return await HouseArrestClient.VendDocumentsAsync(connection, bundleId, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DisposeAppDocumentsAsync()
    {
        foreach (var shared in _appDocuments.Values)
            await shared.DisposeAsync().ConfigureAwait(false);
        _appDocuments.Clear();
    }
}
