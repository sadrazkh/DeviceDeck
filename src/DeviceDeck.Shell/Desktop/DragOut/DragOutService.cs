using System.Runtime.InteropServices;
using DeviceDeck.Bridge;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Shell.Desktop.Interop;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeviceDeck.Shell.Desktop.DragOut;

/// <summary>
/// shell.dragOut: the page cancelled its own HTML drag and asks for a native one. The data object is built on a
/// thread-pool (MTA) thread and handed to the UI thread as a proxy; DoDragDrop runs there while the button is
/// still down. A drop on DeviceDeck's own window is cancelled and reported back with the point, for the page.
/// </summary>
internal sealed class DragOutService
{
    private readonly DeviceHub _hub;
    private readonly TransferQueue _queue;
    private readonly DragOutRegistry _registry;
    private readonly string _stagingRoot;
    private readonly Func<Action, bool> _postToUi;
    private readonly nint _window;
    private readonly Func<double> _scale;

    public DragOutService(DeviceHub hub, TransferQueue queue, DragOutRegistry registry, string stagingRoot, Func<Action, bool> postToUi, nint window, Func<double> scale)
    {
        _hub = hub;
        _queue = queue;
        _registry = registry;
        _stagingRoot = stagingRoot;
        _postToUi = postToUi;
        _window = window;
        _scale = scale;
        queue.JobUpdated += (_, job) =>
        {
            if (job.Kind == TransferKind.DragOut)
            {
                foreach (var session in _registry.All)
                {
                    session.OnJobUpdated(job);
                }
            }
        };
    }

    /// <summary>Removes staging files a previous run left behind.</summary>
    public static void ClearStaging(string stagingRoot)
    {
        try
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task<DragOutResult> DragAsync(DragOutParams request, CancellationToken ct)
    {
        // Build and marshal on the thread pool: the object must belong to the MTA, not the UI thread.
        var (stream, session) = await Task.Run(() => Prepare(request), ct).ConfigureAwait(false);
        var done = new TaskCompletionSource<DragOutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_postToUi(() => Run(stream, session, done)))
        {
            ReleaseStream(stream);
            session?.DragEnded(dropped: false);
            throw new DeviceException(DeviceErrorCodes.NotSupported, "The window is closing.");
        }

        return await done.Task.ConfigureAwait(false);
    }

    private (nint Stream, DragOutSession? Session) Prepare(DragOutParams request)
    {
        var (data, session) = Create(request);
        var unknown = Marshal.GetComInterfaceForObject(data, typeof(ComIDataObject));
        try
        {
            Marshal.ThrowExceptionForHR(Ole.CoMarshalInterThreadInterfaceInStream(Ole.IID_IDataObject, unknown, out var stream));
            return (stream, session);
        }
        catch
        {
            session?.DragEnded(dropped: false);
            throw;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>The data object for a drag (and its session for phone items). Call on an MTA thread.</summary>
    internal (VirtualFileDataObject Data, DragOutSession? Session) Create(DragOutParams request)
    {
        VirtualFileDataObject data;
        DragOutSession? session = null;
        if (request.Source == "pc")
        {
            var paths = request.Paths!.Select(Path.GetFullPath).ToArray();
            if (paths.Any(p => !File.Exists(p) && !Directory.Exists(p)))
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "Something you dragged isn't on this PC any more.");
            }

            data = new VirtualFileDataObject(paths);
        }
        else
        {
            var deviceId = request.DeviceId!;
            var device = _hub.GetUsableDevice(deviceId);
            var id = Guid.NewGuid().ToString("N");
            // Files may come as entries (the page knows sizes and which are folders) or as bare paths, looked up on the phone.
            var items = request.Items;
            var paths = request.Paths;
            session = new DragOutSession(id, deviceId, request.Source, request.Root, Path.Combine(_stagingRoot, id), _queue, _registry,
                () => request.Source == "photos" ? PhotoFiles(items!) : PhoneFiles(device, request.Root!, items, paths));
            _registry.Add(session);

            if (request.Source == "files" && (items is null || items.Any(i => i.IsDirectory)))
            {
                // Walk the dragged folders while the pointer moves; the drop then finds the list ready.
                _ = Task.Run(() =>
                {
                    try
                    {
                        _ = session.Files;
                    }
                    catch (Exception)
                    {
                        // Reported to File Explorer when it asks for the list.
                    }
                });
            }

            data = new VirtualFileDataObject(session);
        }

        return (data, session);
    }

    private void Run(nint stream, DragOutSession? session, TaskCompletionSource<DragOutResult> done)
    {
        var dropped = false;
        try
        {
            if (!Ole.PrimaryButtonDown())
            {
                // Let go before the drag could start: nothing to do.
                ReleaseStream(stream);
                done.TrySetResult(new DragOutResult("none", null));
                return;
            }

            Ole.OleInitialize(0);
            Marshal.ThrowExceptionForHR(Ole.CoGetInterfaceAndReleaseStream(stream, Ole.IID_IDataObject, out var proxy));
            try
            {
                var source = new DropSource(_window, _scale);
                var hr = Ole.SHDoDragDrop(0, proxy, source, Ole.DROPEFFECT_COPY, out var effect);
                if (hr < 0)
                {
                    hr = Ole.DoDragDrop(proxy, source, Ole.DROPEFFECT_COPY, out effect);
                }

                dropped = hr == Ole.DRAGDROP_S_DROP && effect != Ole.DROPEFFECT_NONE;
                done.TrySetResult(source.DroppedInside is { } inside
                    ? new DragOutResult("none", inside)
                    : new DragOutResult(dropped ? "copy" : "none", null));
            }
            finally
            {
                Marshal.Release(proxy);
            }
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
        finally
        {
            session?.DragEnded(dropped);
        }
    }

    private static void ReleaseStream(nint stream)
    {
        if (Ole.CoGetInterfaceAndReleaseStream(stream, Ole.IID_IUnknown, out var unknown) >= 0)
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Photos keep their names; a second IMG_0001.HEIC becomes "IMG_0001 (2).HEIC".</summary>
    internal static IReadOnlyList<DragOutFile> PhotoFiles(IReadOnlyList<DragOutEntry> items)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return items.Select((item, i) => new DragOutFile(Unique(names, SafeName(item.Name)), false, item.Size > 0 ? item.Size : null, item.Modified, item.Id, i)).ToArray();
    }

    /// <summary>Files as they are; dragged folders walked on the phone, folders listed before their contents.</summary>
    /// <param name="paths">Used when <paramref name="items"/> is null: each one is looked up on the phone first.</param>
    internal static IReadOnlyList<DragOutFile> PhoneFiles(IDevice device, string root, IReadOnlyList<DragOutEntry>? items, IReadOnlyList<string>? paths = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        return WalkAsync(device, root, items, paths ?? [], timeout.Token).GetAwaiter().GetResult();
    }

    private static async Task<IReadOnlyList<DragOutFile>> WalkAsync(IDevice device, string root, IReadOnlyList<DragOutEntry>? items, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var files = new List<DragOutFile>();
        var top = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var next = 0;
        IDeviceFileSystem? fs = null;
        try
        {
            if (items is null)
            {
                fs = await device.OpenFileSystemAsync(root, ct).ConfigureAwait(false);
                var found = new List<DragOutEntry>();
                foreach (var path in paths)
                {
                    var entry = await fs.StatAsync(path, ct).ConfigureAwait(false)
                        ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "Something you dragged isn't on the phone any more.");
                    found.Add(new DragOutEntry(entry.Path, entry.Name.Length > 0 ? entry.Name : "Phone", entry.IsDirectory ? 0 : entry.Size, entry.IsDirectory, entry.Modified));
                }

                items = found;
            }

            foreach (var item in items)
            {
                var name = Unique(top, SafeName(item.Name));
                if (!item.IsDirectory)
                {
                    // 0 from the page means "not known": the stream then reads to the end instead of stopping at 0.
                    files.Add(new DragOutFile(name, false, item.Size > 0 ? item.Size : null, item.Modified, item.Id, next++));
                    continue;
                }

                fs ??= await device.OpenFileSystemAsync(root, ct).ConfigureAwait(false);
                files.Add(new DragOutFile(name, true, null, item.Modified, null, -1));
                await WalkFolderAsync(fs, item.Id, name, files, () => next++, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (fs is not null)
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }
        }

        return files;
    }

    private static async Task WalkFolderAsync(IDeviceFileSystem fs, string folder, string relative, List<DragOutFile> files, Func<int> nextIndex, CancellationToken ct)
    {
        var children = new List<FileEntry>();
        await foreach (var child in fs.ListAsync(folder, ct).ConfigureAwait(false))
        {
            children.Add(child);
        }

        foreach (var child in children)
        {
            var path = relative + "\\" + SafeName(child.Name);
            if (child.IsDirectory)
            {
                files.Add(new DragOutFile(path, true, null, child.Modified, null, -1));
                await WalkFolderAsync(fs, child.Path, path, files, nextIndex, ct).ConfigureAwait(false);
            }
            else
            {
                files.Add(new DragOutFile(path, false, child.Size, child.Modified, child.Path, nextIndex()));
            }
        }
    }

    private static string Unique(HashSet<string> taken, string name)
    {
        if (taken.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A phone name made safe for Windows: reserved characters replaced, no trailing dots or spaces.</summary>
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        return safe.Length == 0 ? "_" : safe;
    }
}

/// <summary>Drops on DeviceDeck's own window are cancelled and remembered, in CSS pixels, for the page to handle.</summary>
[ComVisible(true)]
internal sealed class DropSource(nint window, Func<double> scale) : IDropSource
{
    public DragPoint? DroppedInside { get; private set; }

    public int QueryContinueDrag(bool escapePressed, uint keyState)
    {
        if (escapePressed)
        {
            return Ole.DRAGDROP_S_CANCEL;
        }

        var button = Ole.GetSystemMetrics(Ole.SM_SWAPBUTTON) != 0 ? Ole.MK_RBUTTON : Ole.MK_LBUTTON;
        if ((keyState & button) != 0)
        {
            return Ole.S_OK;
        }

        if (window != 0 && Ole.GetCursorPos(out var point) && Ole.GetAncestor(Ole.WindowFromPoint(point), Ole.GA_ROOT) == window)
        {
            Ole.ScreenToClient(window, ref point);
            var s = Math.Max(scale(), 0.5);
            DroppedInside = new DragPoint(Math.Round(point.X / s, 1), Math.Round(point.Y / s, 1));
            return Ole.DRAGDROP_S_CANCEL;
        }

        return Ole.DRAGDROP_S_DROP;
    }

    public int GiveFeedback(uint effect) => Ole.DRAGDROP_S_USEDEFAULTCURSORS;
}
