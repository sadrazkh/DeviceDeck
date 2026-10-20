using System.Runtime.CompilerServices;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

/// <summary>A '/'-separated view of one simulated root. File contents are filler of the stored size.</summary>
internal sealed class FakeDeviceFileSystem(FakeDevice device, FakeRoot root) : IDeviceFileSystem
{
    private static readonly byte[] Header = Encoding.UTF8.GetBytes("DeviceDeck sample file\n");

    public async IAsyncEnumerable<FileEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        device.EnsureConnected();
        FileEntry[] entries;
        lock (device.Gate)
        {
            var node = Resolve(path) ?? throw NotFound(path);
            if (!node.IsDirectory)
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That isn't a folder.", new IOException(path));
            }

            if (node.Denied)
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Android doesn't let computers open this folder.", new UnauthorizedAccessException(path));
            }

            var parent = Normalize(path);
            entries = node.Children!.Values.Select(c => ToEntry(Combine(parent, c.Name), c)).ToArray();
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            yield return entry;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        device.EnsureConnected();
        lock (device.Gate)
        {
            var node = Resolve(path);
            return Task.FromResult(node is null ? null : ToEntry(Normalize(path), node));
        }
    }

    public async Task ReadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        device.EnsureConnected();
        long size;
        lock (device.Gate)
        {
            var node = Resolve(path) ?? throw NotFound(path);
            if (node.IsDirectory)
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That is a folder, not a file.", new IOException(path));
            }

            if (node.Denied)
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "The phone doesn't allow reading this file.");
            }

            size = node.Size;
        }

        await FakeIo.ProduceAsync(device, Header, size, destination, progress, ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string path, Stream source, long length, IProgress<long>? progress, CancellationToken ct)
    {
        device.EnsureConnected();
        var (parentPath, name) = Split(path);
        lock (device.Gate)
        {
            CheckWritableParent(parentPath, path);
            if (Resolve(path) is { IsDirectory: true })
            {
                throw new DeviceException(DeviceErrorCodes.AlreadyExists, "A folder with that name is already there.");
            }
        }

        var received = await FakeIo.ConsumeAsync(device, source, length, progress, ct).ConfigureAwait(false);
        lock (device.Gate)
        {
            var parent = CheckWritableParent(parentPath, path);
            parent.Children![name] = FakeFileNode.File(name, received, DateTimeOffset.Now);
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        device.EnsureConnected();
        var (parentPath, name) = Split(path);
        lock (device.Gate)
        {
            var parent = CheckWritableParent(parentPath, path);
            if (parent.Children!.ContainsKey(name))
            {
                throw new DeviceException(DeviceErrorCodes.AlreadyExists, "Something with that name is already there.");
            }

            var folder = FakeFileNode.Dir(name);
            folder.Modified = DateTimeOffset.Now;
            parent.Children[name] = folder;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken ct)
    {
        device.EnsureConnected();
        var (parentPath, name) = Split(path);
        lock (device.Gate)
        {
            var parent = CheckWritableParent(parentPath, path);
            if (!parent.Children!.TryGetValue(name, out var node))
            {
                throw NotFound(path);
            }

            if (node.ReadOnly)
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "This item is read-only.");
            }

            if (node.IsDirectory && node.Children!.Count > 0 && !recursive)
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "The folder isn't empty.");
            }

            parent.Children.Remove(name);
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(string from, string to, CancellationToken ct)
    {
        device.EnsureConnected();
        var (fromParentPath, fromName) = Split(from);
        var (toParentPath, toName) = Split(to);
        lock (device.Gate)
        {
            var fromParent = CheckWritableParent(fromParentPath, from);
            var toParent = CheckWritableParent(toParentPath, to);
            if (!fromParent.Children!.TryGetValue(fromName, out var node))
            {
                throw NotFound(from);
            }

            if (node.ReadOnly)
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "This item is read-only.");
            }

            if (toParent.Children!.ContainsKey(toName))
            {
                throw new DeviceException(DeviceErrorCodes.AlreadyExists, "Something with that name is already there.");
            }

            fromParent.Children.Remove(fromName);
            node.Name = toName;
            node.Modified = DateTimeOffset.Now;
            toParent.Children[toName] = node;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private FakeFileNode CheckWritableParent(string parentPath, string path)
    {
        var parent = Resolve(parentPath);
        if (parent is null || !parent.IsDirectory)
        {
            throw NotFound(path);
        }

        if (parent.ReadOnly || parent.Denied)
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "This folder is read-only.", new UnauthorizedAccessException(parentPath));
        }

        return parent;
    }

    private FakeFileNode? Resolve(string path)
    {
        var node = root.Tree;
        foreach (var part in Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.IsDirectory || !node.Children!.TryGetValue(part, out var child))
            {
                return null;
            }

            node = child;
        }

        return node;
    }

    private static FileEntry ToEntry(string path, FakeFileNode node) =>
        new(path, node.Name, node.IsDirectory, node.IsDirectory ? 0 : node.Size, node.Modified, node.ReadOnly || node.Denied);

    internal static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".."))
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That path isn't valid.", new ArgumentException(path));
        }

        return "/" + string.Join('/', parts);
    }

    private static (string Parent, string Name) Split(string path)
    {
        var normal = Normalize(path);
        if (normal == "/")
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "The top folder can't be changed.");
        }

        var cut = normal.LastIndexOf('/');
        return (cut == 0 ? "/" : normal[..cut], normal[(cut + 1)..]);
    }

    private static string Combine(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;

    private static DeviceException NotFound(string path) =>
        new(DeviceErrorCodes.PathNotFound, "That file or folder isn't on the phone.", new FileNotFoundException(path));
}
