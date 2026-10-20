using System.Runtime.CompilerServices;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.AppFiles;

/// <summary>
/// An app's Documents over house_arrest. Opening house_arrest takes about a second (lockdown session, service start,
/// VendDocuments), so the page's many small calls (list, stat, new folder, rename, delete) share one connection per
/// app that stays open. File contents move over a connection of this object's own, opened on first use and closed
/// with it, so a long copy never holds up a listing and a copy is never retried on a half-read stream.
/// </summary>
internal sealed class AppDocumentsFileSystem(SharedConnection<AfcClient> shared, Func<CancellationToken, Task<AfcClient>> openForContents) : IDeviceFileSystem
{
    private const string Documents = "/Documents";
    private readonly SemaphoreSlim _contentsGate = new(1, 1);
    private AfcClient? _contentsAfc;
    private IosFileSystem? _contents;

    public async IAsyncEnumerable<FileEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var entries = await UseAsync(async fs =>
        {
            var list = new List<FileEntry>();
            await foreach (var entry in fs.ListAsync(path, ct).ConfigureAwait(false))
                list.Add(entry);
            return list;
        }, ct).ConfigureAwait(false);
        foreach (var entry in entries)
            yield return entry;
    }

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct) => UseAsync(fs => fs.StatAsync(path, ct), ct);

    public async Task ReadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
        await (await ContentsAsync(ct).ConfigureAwait(false)).ReadAsync(path, destination, progress, ct).ConfigureAwait(false);

    public async Task WriteAsync(string path, Stream source, long length, IProgress<long>? progress, CancellationToken ct) =>
        await (await ContentsAsync(ct).ConfigureAwait(false)).WriteAsync(path, source, length, progress, ct).ConfigureAwait(false);

    // A retry after a dead connection is safe here: the request did not reach the phone, or repeating it is harmless.
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => UseAsync(fs => fs.CreateDirectoryAsync(path, ct), ct);

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct) => UseAsync(fs => fs.DeleteAsync(path, recursive, ct), ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => UseAsync(fs => fs.RenameAsync(from, to, ct), ct);

    /// <summary>Opens the shared connection now, so a missing app or one without File Sharing fails at open as before.</summary>
    public Task WarmAsync(CancellationToken ct) => UseAsync(_ => Task.FromResult(true), ct);

    private async Task<T> UseAsync<T>(Func<IosFileSystem, Task<T>> operation, CancellationToken ct)
    {
        try
        {
            // IosFileSystem only adds the /Documents jail; it is never disposed here, the shared connection lives on.
            return await shared.UseAsync(afc => operation(new IosFileSystem(afc, Documents)), ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new DeviceException(DeviceErrorCodes.Disconnected, "The iPhone was disconnected.", ex);
        }
    }

    private Task UseAsync(Func<IosFileSystem, Task> operation, CancellationToken ct) =>
        UseAsync(async fs =>
        {
            await operation(fs).ConfigureAwait(false);
            return true;
        }, ct);

    private async Task<IosFileSystem> ContentsAsync(CancellationToken ct)
    {
        await _contentsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_contentsAfc is { IsBroken: true })
            {
                await _contents!.DisposeAsync().ConfigureAwait(false);
                _contents = null;
                _contentsAfc = null;
            }

            if (_contents is null)
            {
                _contentsAfc = await openForContents(ct).ConfigureAwait(false);
                _contents = new IosFileSystem(_contentsAfc, Documents);
            }

            return _contents;
        }
        finally
        {
            _contentsGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _contentsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_contents is not null)
                await _contents.DisposeAsync().ConfigureAwait(false);
            _contents = null;
            _contentsAfc = null;
        }
        finally
        {
            _contentsGate.Release();
        }
    }
}
