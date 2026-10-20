using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.AppFiles;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.Transfers.Handlers;

/// <summary>files.copyToPc: files and whole folders from one root on the phone into a PC folder.</summary>
public sealed class CopyToPcHandler : TransferHandler<CopyToPcRequest>
{
    public override TransferKind Kind => TransferKind.CopyToPc;

    protected override async Task<string> DescribeAsync(IDevice device, CopyToPcRequest request, CancellationToken ct)
    {
        RequireList(request.Paths, "file");
        RequirePcFolder(request.Destination, "The destination");
        if (string.IsNullOrEmpty(request.Root))
        {
            throw new ArgumentException("root is required.");
        }

        var what = request.Paths.Count != 1 ? Plural(request.Paths.Count, "item", "items")
            : IsTop(request.Paths[0]) ? await TopNameAsync(device, request.Root, ct).ConfigureAwait(false)
            : NameOf(request.Paths[0]);
        return $"Copying {what} to {request.Destination}";
    }

    protected override async Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, CopyToPcRequest request, CancellationToken ct)
    {
        var fs = await context.GetFileSystemAsync(request.Root, ct).ConfigureAwait(false);
        var plans = new List<TransferItemPlan>();
        foreach (var path in request.Paths)
        {
            var entry = await fs.StatAsync(path, ct).ConfigureAwait(false)
                ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{NameOf(path)} isn't on the phone any more.");
            // A whole root (an app's Documents) lands in a folder named after the app, not "_".
            var name = IsTop(path) ? await TopNameAsync(context.Device, request.Root, ct).ConfigureAwait(false) : entry.Name;
            var target = Path.Combine(request.Destination, SafeName(name.Length == 0 ? "Phone" : name));
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                await WalkAsync(fs, entry.Path, target, plans, ct).ConfigureAwait(false);
            }
            else
            {
                plans.Add(new TransferItemPlan(entry.Path, target, entry.Name, entry.Size));
            }
        }

        return plans;
    }

    private static async Task WalkAsync(IDeviceFileSystem fs, string folder, string target, List<TransferItemPlan> plans, CancellationToken ct)
    {
        var children = new List<FileEntry>();
        await foreach (var child in fs.ListAsync(folder, ct).ConfigureAwait(false))
        {
            children.Add(child);
        }

        foreach (var child in children)
        {
            var childTarget = Path.Combine(target, SafeName(child.Name));
            if (child.IsDirectory)
            {
                Directory.CreateDirectory(childTarget);
                await WalkAsync(fs, child.Path, childTarget, plans, ct).ConfigureAwait(false);
            }
            else
            {
                plans.Add(new TransferItemPlan(child.Path, childTarget, child.Name, child.Size));
            }
        }
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, CopyToPcRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var destination = ResolvePcDestination(context, item, item.Destination);
        if (destination is null)
        {
            return TransferItemState.Skipped;
        }

        item.Destination = destination;
        var fs = await context.GetFileSystemAsync(request.Root, ct).ConfigureAwait(false);
        await WritePcFileAsync(destination, item.Size, file => fs.ReadAsync(item.Source, file, progress, ct), ct).ConfigureAwait(false);
        return TransferItemState.Done;
    }

    private static string NameOf(string devicePath)
    {
        var name = devicePath.TrimEnd('/');
        var cut = name.LastIndexOf('/');
        return cut >= 0 ? name[(cut + 1)..] : name;
    }

    private static bool IsTop(string devicePath) => devicePath.Trim().Trim('/', '\\').Length == 0;

    private static async Task<string> TopNameAsync(IDevice device, string root, CancellationToken ct) =>
        await AppFilesHelpers.RootLabelAsync(device, root, ct).ConfigureAwait(false) ?? root;
}

/// <summary>files.copyToDevice: PC files and folders into a folder of one root on the phone (an app's Documents, internal storage).</summary>
public sealed class CopyToDeviceHandler : TransferHandler<CopyToDeviceRequest>
{
    public override TransferKind Kind => TransferKind.CopyToDevice;

    protected override async Task<string> DescribeAsync(IDevice device, CopyToDeviceRequest request, CancellationToken ct)
    {
        RequireList(request.Files, "file");
        if (string.IsNullOrEmpty(request.Root))
        {
            throw new ArgumentException("root is required.");
        }

        if (string.IsNullOrEmpty(request.Path) || !request.Path.StartsWith('/'))
        {
            throw new ArgumentException("path must be a folder on the phone, starting with '/'.");
        }

        foreach (var file in request.Files)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                throw new ArgumentException("Each file must be a full path on this PC.");
            }
        }

        var label = await AppFilesHelpers.RootLabelAsync(device, request.Root, ct).ConfigureAwait(false)
            ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That folder isn't on the phone.");
        var what = request.Files.Count == 1 ? Path.GetFileName(request.Files[0].TrimEnd('\\', '/')) : Plural(request.Files.Count, "item", "items");
        return $"Copying {what} into {label}";
    }

    protected override async Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, CopyToDeviceRequest request, CancellationToken ct)
    {
        var fs = await context.GetFileSystemAsync(request.Root, ct).ConfigureAwait(false);
        var folder = await fs.StatAsync(request.Path, ct).ConfigureAwait(false);
        if (folder is not { IsDirectory: true })
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That folder isn't on the phone any more.");
        }

        var plans = new List<TransferItemPlan>();
        foreach (var local in request.Files)
        {
            var trimmed = local.TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (Directory.Exists(trimmed))
            {
                var top = JoinDevice(request.Path, name);
                await EnsureDeviceFolderAsync(fs, top, ct).ConfigureAwait(false);
                foreach (var dir in Directory.EnumerateDirectories(trimmed, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                {
                    await EnsureDeviceFolderAsync(fs, JoinDevice(top, Relative(trimmed, dir)), ct).ConfigureAwait(false);
                }

                foreach (var file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                {
                    plans.Add(new TransferItemPlan(file, JoinDevice(top, Relative(trimmed, file)), Path.GetFileName(file), new FileInfo(file).Length));
                }
            }
            else if (File.Exists(trimmed))
            {
                plans.Add(new TransferItemPlan(trimmed, JoinDevice(request.Path, name), name, new FileInfo(trimmed).Length));
            }
            else
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{name} isn't on this PC any more.", new FileNotFoundException(local));
            }
        }

        return plans;
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, CopyToDeviceRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var fs = await context.GetFileSystemAsync(request.Root, ct).ConfigureAwait(false);
        var destination = item.Destination;
        var replace = false;
        var existing = await fs.StatAsync(destination, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            switch (context.Decide(item, existing.IsDirectory ? 0 : existing.Size, item.Size))
            {
                case ConflictChoice.Skip:
                    return TransferItemState.Skipped;
                case ConflictChoice.KeepBoth:
                    destination = await UniqueDevicePathAsync(fs, destination, ct).ConfigureAwait(false);
                    item.Destination = destination;
                    break;
                default:
                    if (existing.IsDirectory)
                    {
                        throw new DeviceException(DeviceErrorCodes.AlreadyExists, "A folder with that name is already there.");
                    }

                    replace = true;
                    break;
            }
        }

        // Upload under a temporary name so an app never sees half a film, then rename.
        var partial = destination + PartialSuffix;
        try
        {
            await using var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = source.Length;
            item.Size = length;
            await fs.WriteAsync(partial, source, length, progress, ct).ConfigureAwait(false);
            var written = await fs.StatAsync(partial, ct).ConfigureAwait(false);
            if (written is null || written.Size != length)
            {
                throw new DeviceException(DeviceErrorCodes.Protocol, "The copy came out the wrong size.", new IOException($"Expected {length} bytes, the phone has {written?.Size}."));
            }

            if (replace)
            {
                await fs.DeleteAsync(destination, false, ct).ConfigureAwait(false);
            }

            await fs.RenameAsync(partial, destination, ct).ConfigureAwait(false);
            return TransferItemState.Done;
        }
        catch (Exception ex) when (!TransferQueue.IsDeviceUnavailable(ex))
        {
            await TryDeleteOnDeviceAsync(fs, partial).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task TryDeleteOnDeviceAsync(IDeviceFileSystem fs, string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (await fs.StatAsync(path, timeout.Token).ConfigureAwait(false) is not null)
            {
                await fs.DeleteAsync(path, false, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Best effort: a leftover .ddpart is overwritten when the item runs again.
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>photos.import: PC pictures and videos into the phone's camera folder (Android in v1).</summary>
public sealed class PhotosImportHandler : TransferHandler<PhotosImportRequest>
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".dng", ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".webm",
    };

    public override TransferKind Kind => TransferKind.PhotosImport;

    protected override Task<string> DescribeAsync(IDevice device, PhotosImportRequest request, CancellationToken ct)
    {
        RequireList(request.Files, "photo");
        if ((device.Summary.Capabilities & DeviceCapability.PhotosImport) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't take photos from a PC.");
        }

        var what = request.Files.Count == 1 ? Path.GetFileName(request.Files[0]) : Plural(request.Files.Count, "photo", "photos");
        return Task.FromResult($"Adding {what} to {device.Summary.Name}");
    }

    protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, PhotosImportRequest request, CancellationToken ct)
    {
        var plans = new List<TransferItemPlan>();
        foreach (var local in request.Files)
        {
            var trimmed = local.TrimEnd('\\', '/');
            if (Directory.Exists(trimmed))
            {
                foreach (var file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories)
                             .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    plans.Add(new TransferItemPlan(file, Path.GetFileName(file), Path.GetFileName(file), new FileInfo(file).Length));
                }
            }
            else if (File.Exists(trimmed))
            {
                plans.Add(new TransferItemPlan(trimmed, Path.GetFileName(trimmed), Path.GetFileName(trimmed), new FileInfo(trimmed).Length));
            }
            else
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{Path.GetFileName(trimmed)} isn't on this PC any more.", new FileNotFoundException(local));
            }
        }

        return Task.FromResult<IReadOnlyList<TransferItemPlan>>(plans);
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, PhotosImportRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        await using var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        item.Size = source.Length;
        await context.Device.Photos.ImportAsync(item.Name, source, source.Length, progress, ct).ConfigureAwait(false);
        return TransferItemState.Done;
    }
}
