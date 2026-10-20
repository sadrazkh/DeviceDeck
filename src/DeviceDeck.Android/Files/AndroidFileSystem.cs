using System.Runtime.CompilerServices;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Files;

/// <summary>
/// One file root (/sdcard or /storage/&lt;sd-id&gt;). Paths in and out are root-relative and '/'-separated
/// ("/DCIM/Camera/x.jpg"). Listings come from the shell (stat, falling back to ls -la); file contents move over one
/// sync-service connection that lives as long as this object.
/// </summary>
internal sealed class AndroidFileSystem(IAdbChannel channel, string basePath, int sdk, TimeSpan phoneOffset) : IDeviceFileSystem
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly MediaScanner _scanner = new(channel);
    private IAdbSyncSession? _sync;

    public string BasePath { get; } = basePath.TrimEnd('/');

    public async IAsyncEnumerable<FileEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        string relative = RootPath.Normalize(path);
        string device = RootPath.ToDevice(BasePath, relative);
        IReadOnlyList<RemoteFile> files = await ListDeviceAsync(device, ct).ConfigureAwait(false);
        foreach (var file in files.OrderBy(f => !f.IsDirectory).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            yield return ToEntry(RootPath.Join(relative, file.Name), file);
        }
    }

    internal async Task<IReadOnlyList<RemoteFile>> ListDeviceAsync(string devicePath, CancellationToken ct)
    {
        string quotedDir = ShellQuote.Quote(devicePath.TrimEnd('/') + "/");
        var result = await channel.ShellAsync(FileListingParser.StatListCommand(quotedDir), ct).ConfigureAwait(false);
        var files = FileListingParser.ParseStat(result.Output);
        if (files.Count > 0)
        {
            return files; // find exits 1 when one entry was unreadable; the rest is still good
        }
        if (result.Succeeded)
        {
            return files; // an empty folder
        }
        if (AdbErrors.FromFileErrorText(result.Error, devicePath) is { } error)
        {
            throw error;
        }

        // A build without toybox find/stat -c: fall back to ls -la.
        var ls = await channel.ShellAsync($"ls -la {quotedDir}", ct).ConfigureAwait(false);
        if (!ls.Succeeded && ls.Output.Length == 0)
        {
            throw AdbErrors.FromShell("ls", ls, devicePath);
        }
        return FileListingParser.ParseLs(ls.Output, phoneOffset);
    }

    public async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        string relative = RootPath.Normalize(path);
        string device = RootPath.ToDevice(BasePath, relative);
        var result = await channel.ShellAsync(FileListingParser.StatOneCommand(ShellQuote.Quote(device)), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            if (result.Error.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            throw AdbErrors.FromShell("stat", result, device);
        }
        var file = FileListingParser.TryParseStatLine(result.Output.Trim());
        if (file == null)
        {
            if (relative == "/")
            {
                return new FileEntry("/", "", true, 0, null);
            }
            throw new DeviceException(DeviceErrorCodes.Protocol, $"Couldn't read the details of {device}.");
        }
        return ToEntry(relative, file);
    }

    public async Task ReadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        string device = RootPath.ToDevice(BasePath, RootPath.Normalize(path));
        await WithSyncAsync(sync => sync.PullAsync(device, destination, progress, ct), ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string path, Stream source, long length, IProgress<long>? progress, CancellationToken ct)
    {
        string relative = RootPath.Normalize(path);
        if (relative == "/")
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Pick a file name inside the folder.");
        }
        string device = RootPath.ToDevice(BasePath, relative);
        await WithSyncAsync(sync => sync.PushAsync(source, device, DateTimeOffset.UtcNow, progress, ct), ct).ConfigureAwait(false);
        await AfterChangeAsync([device], ct).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        string device = RootPath.ToDevice(BasePath, RootPath.Normalize(path));
        var result = await channel.ShellAsync($"mkdir -- {ShellQuote.Quote(device)}", ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw AdbErrors.FromShell("mkdir", result, device);
        }
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
    {
        string relative = RootPath.Normalize(path);
        if (relative == "/")
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "The top of a storage root can't be deleted.");
        }
        string device = ShellQuote.Quote(RootPath.ToDevice(BasePath, relative));
        // Exit 2 = missing. rmdir refuses a non-empty folder unless the caller asked for recursive.
        string script =
            $"if [ ! -e {device} ] && [ ! -L {device} ]; then echo 'No such file or directory' >&2; exit 2; fi; " +
            (recursive
                ? $"rm -rf -- {device}"
                : $"if [ -d {device} ] && [ ! -L {device} ]; then rmdir -- {device}; else rm -f -- {device}; fi");
        var result = await channel.ShellAsync(script, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw AdbErrors.FromShell("rm", result, relative);
        }
        await AfterChangeAsync([RootPath.ToDevice(BasePath, relative)], ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        string source = RootPath.ToDevice(BasePath, RootPath.Normalize(from));
        string target = RootPath.ToDevice(BasePath, RootPath.Normalize(to));
        string script =
            $"if [ -e {ShellQuote.Quote(target)} ]; then echo 'File exists' >&2; exit 17; fi; " +
            $"mv -- {ShellQuote.Quote(source)} {ShellQuote.Quote(target)}";
        var result = await channel.ShellAsync(script, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw AdbErrors.FromShell("mv", result, from);
        }
        await AfterChangeAsync([source, target], ct).ConfigureAwait(false);
    }

    /// <summary>Android 10 keeps /sdcard on sdcardfs, which doesn't tell MediaStore; 11+ (FUSE) indexes changes itself.</summary>
    private async Task AfterChangeAsync(IReadOnlyCollection<string> devicePaths, CancellationToken ct)
    {
        if (sdk is > 0 and <= 29)
        {
            await _scanner.ScanAsync(devicePaths, ct).ConfigureAwait(false);
        }
    }

    private async Task WithSyncAsync(Func<IAdbSyncSession, Task> action, CancellationToken ct)
    {
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _sync ??= await channel.OpenSyncAsync(ct).ConfigureAwait(false);
            try
            {
                await action(_sync).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or DeviceException { Code: DeviceErrorCodes.Disconnected })
            {
                // A cancelled or broken transfer leaves the sync stream mid-message; start clean next time.
                await _sync.DisposeAsync().ConfigureAwait(false);
                _sync = null;
                throw;
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static FileEntry ToEntry(string relativePath, RemoteFile file) =>
        new(relativePath, file.Name, file.IsDirectory, file.Size, file.Modified, file.ReadOnly);

    public async ValueTask DisposeAsync()
    {
        await _syncGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sync != null)
            {
                await _sync.DisposeAsync().ConfigureAwait(false);
                _sync = null;
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }
}
