using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Photos;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.Transfers.Handlers;

/// <summary>
/// photos.importToApp: PC pictures and videos into the Documents folder of an app that shares files (iPhone root
/// "app:&lt;bundleId&gt;"). There is no supported way to write into an iPhone's Photos library over USB; this puts the
/// files where the user can open them in that app (or in Files › On My iPhone) and save them to Photos from there.
/// A name that is taken gets "(1)": nothing in the app is replaced.
/// </summary>
public sealed class PhotosToAppHandler : TransferHandler<PhotosToAppRequest>
{
    public static readonly IReadOnlySet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".dng",
        ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".webm", ".avi",
    };

    public override TransferKind Kind => TransferKind.PhotosToApp;

    public static string RootOf(string appId) => "app:" + appId;

    protected override async Task<string> DescribeAsync(IDevice device, PhotosToAppRequest request, CancellationToken ct)
    {
        RequireList(request.Files, "photo");
        if (string.IsNullOrWhiteSpace(request.AppId))
        {
            throw new ArgumentException("appId is required.");
        }

        foreach (var file in request.Files)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                throw new ArgumentException("Each file must be a full path on this PC.");
            }
        }

        // iPhones only: an Android phone takes photos straight into its gallery (photos.import). Since app files came to
        // Android too, AppDocuments alone no longer tells the two apart.
        if (device.Summary.Platform != DevicePlatform.Ios)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} adds photos straight to its gallery; this is for iPhones.");
        }

        if ((device.Summary.Capabilities & DeviceCapability.AppDocuments) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't share app folders.");
        }

        var roots = await device.GetFileRootsAsync(ct).ConfigureAwait(false);
        var label = roots.FirstOrDefault(r => r.Key == RootOf(request.AppId))?.Label
            ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That app doesn't share files with a PC, or isn't on the phone.");
        var what = request.Files.Count == 1 ? Path.GetFileName(request.Files[0].TrimEnd('\\', '/')) : Plural(request.Files.Count, "item", "items");
        return $"Adding {what} to {label} on {device.Summary.Name}";
    }

    protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, PhotosToAppRequest request, CancellationToken ct)
    {
        var plans = new List<TransferItemPlan>();
        foreach (var local in request.Files)
        {
            var trimmed = local.TrimEnd('\\', '/');
            if (Directory.Exists(trimmed))
            {
                // A folder brings its pictures and videos, flattened into Documents.
                foreach (var file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories)
                             .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    plans.Add(Plan(file));
                }
            }
            else if (File.Exists(trimmed))
            {
                plans.Add(Plan(trimmed));
            }
            else
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{Path.GetFileName(trimmed)} isn't on this PC any more.", new FileNotFoundException(local));
            }
        }

        return Task.FromResult<IReadOnlyList<TransferItemPlan>>(plans);
    }

    private static TransferItemPlan Plan(string file)
    {
        var name = PhotoBackupNaming.SafeName(Path.GetFileName(file));
        return new TransferItemPlan(file, "/" + name, name, new FileInfo(file).Length);
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, PhotosToAppRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var fs = await context.GetFileSystemAsync(RootOf(request.AppId), ct).ConfigureAwait(false);
        var destination = item.Destination;
        if (await fs.StatAsync(destination, ct).ConfigureAwait(false) is not null)
        {
            destination = await UniqueDevicePathAsync(fs, destination, ct).ConfigureAwait(false);
            item.Destination = destination;
        }

        // Upload under a temporary name so the app never shows half a video, then rename.
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
            // Best effort: our own leftover .ddpart, overwritten when the item runs again.
        }
    }
}
