using System.Runtime.CompilerServices;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.Device;

/// <summary>
/// IDeviceFileSystem over one AFC connection. Paths callers see are rooted at "/" of the root;
/// <paramref name="devicePrefix"/> maps them onto the AFC jail (e.g. "/Documents" for an app's files).
/// </summary>
public sealed class IosFileSystem(AfcClient afc, string devicePrefix = "") : IDeviceFileSystem
{
    private readonly string _prefix = devicePrefix.TrimEnd('/');

    public async IAsyncEnumerable<FileEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var dir = Normalize(path);
        var names = await IosErrors.Guard(() => afc.ReadDirectoryAsync(ToDevice(dir), ct)).ConfigureAwait(false);
        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var child = Combine(dir, name);
            var info = await IosErrors.Guard(() => afc.GetFileInfoAsync(ToDevice(child), ct)).ConfigureAwait(false);
            if (info is null)
                continue; // removed between the listing and the stat
            yield return ToEntry(child, name, info);
        }
    }

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct) => IosErrors.Guard(async () =>
    {
        var p = Normalize(path);
        var info = await afc.GetFileInfoAsync(ToDevice(p), ct).ConfigureAwait(false);
        return info is null ? null : ToEntry(p, p == "/" ? "/" : p[(p.LastIndexOf('/') + 1)..], info);
    });

    public Task ReadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
        IosErrors.Guard(() => afc.ReadFileAsync(ToDevice(Normalize(path)), destination, progress, ct));

    public Task WriteAsync(string path, Stream source, long length, IProgress<long>? progress, CancellationToken ct) =>
        IosErrors.Guard(() => afc.WriteFileAsync(ToDevice(Normalize(path)), source, length, progress, ct));

    public Task CreateDirectoryAsync(string path, CancellationToken ct) =>
        IosErrors.Guard(() => afc.MakeDirectoryAsync(ToDevice(Normalize(path)), ct));

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => IosErrors.Guard(async () =>
    {
        var p = Normalize(path);
        if (p == "/")
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "The root folder cannot be deleted.");
        if (recursive)
            await afc.RemovePathAndContentsAsync(ToDevice(p), ct).ConfigureAwait(false);
        else
            await afc.RemovePathAsync(ToDevice(p), ct).ConfigureAwait(false);
    });

    public Task RenameAsync(string from, string to, CancellationToken ct) =>
        IosErrors.Guard(() => afc.RenamePathAsync(ToDevice(Normalize(from)), ToDevice(Normalize(to)), ct));

    public ValueTask DisposeAsync() => afc.DisposeAsync();

    /// <summary>"/a//b/" → "/a/b". Rejects ".." so a root cannot be escaped.</summary>
    public static string Normalize(string path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p == ".."))
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Paths may not contain '..'.");
        return "/" + string.Join('/', parts.Where(p => p != "."));
    }

    private string ToDevice(string normalized) => _prefix.Length == 0 ? normalized : normalized == "/" ? _prefix : _prefix + normalized;

    private static string Combine(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    private static FileEntry ToEntry(string path, string name, AfcFileInfo info) =>
        new(path, name, info.IsDirectory, info.IsDirectory ? 0 : info.Size, info.Modified);
}
