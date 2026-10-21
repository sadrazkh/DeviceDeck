using System.Globalization;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Photos;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.Transfers.Handlers;

/// <summary>
/// The photosToApp job's request. <paramref name="Folder"/> is the dated folder the files go into ("DeviceDeck 2026-10-21");
/// jobs saved before it existed have none and go straight into Documents.
/// </summary>
public sealed record PhotosToAppJob(string AppId, IReadOnlyList<string> Files, string? Folder = null);

/// <summary>One folder to open in the Files app and save all at once: the dated folder, or one of its parts of 200.</summary>
/// <param name="Path">Inside the app's Documents: "DeviceDeck 2026-10-21" or "DeviceDeck 2026-10-21/201-400".</param>
public sealed record PhotosToAppPart(string Path, int Count);

/// <summary>photos.importToApp's result: the job and where on the phone the files will be.</summary>
public sealed record PhotosToAppStarted(string JobId, string Folder, IReadOnlyList<PhotosToAppPart> Parts);

/// <summary>
/// photos.importToApp: PC pictures and videos into the Documents folder of an app that shares files (iPhone root
/// "app:&lt;bundleId&gt;"). There is no supported way to write into an iPhone's Photos library over USB; this puts the
/// files where the user can save them all at once from the Files app (Files › On My iPhone › app › folder › Select ›
/// Select All › Share › Save N Images). Each send gets a dated folder of its own, and a big one is split into folders
/// of <see cref="PartSize"/> (the share sheet gets slow with more). A name that is taken gets "(1)": nothing is replaced.
/// </summary>
public sealed class PhotosToAppHandler : TransferHandler<PhotosToAppJob>
{
    public const int PartSize = 200;

    public static readonly IReadOnlySet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".dng",
        ".mp4", ".mov", ".m4v", ".3gp", ".mkv", ".webm", ".avi",
    };

    public override TransferKind Kind => TransferKind.PhotosToApp;

    public static string RootOf(string appId) => "app:" + appId;

    /// <summary>
    /// Checks the request, picks a dated folder that isn't in the app yet ("DeviceDeck 2026-10-21", then "… (2)") and
    /// queues the job. A new folder per send keeps Select All to this send's photos.
    /// </summary>
    public static async Task<PhotosToAppStarted> EnqueueAsync(DeviceHub hub, TransferQueue queue, string deviceId, string appId, IReadOnlyList<string> files,
        CancellationToken ct, DateTime? today = null)
    {
        var device = hub.GetUsableDevice(deviceId);
        _ = await ValidateAsync(device, appId, files, ct).ConfigureAwait(false);
        var count = Expand(files).Count;
        var folder = await ChooseFolderAsync(device, appId, today ?? DateTime.Now, ct).ConfigureAwait(false);
        var job = await queue.EnqueueAsync(deviceId, TransferKind.PhotosToApp, new PhotosToAppJob(appId, files, folder), ConflictRule.KeepBoth, ct).ConfigureAwait(false);
        return new PhotosToAppStarted(job.Id, folder, Parts(folder, count));
    }

    /// <summary>The folders a send of <paramref name="count"/> files fills: the dated one, or its parts "1-200", "201-400", ….</summary>
    public static IReadOnlyList<PhotosToAppPart> Parts(string folder, int count)
    {
        if (count <= PartSize)
        {
            return [new PhotosToAppPart(folder, count)];
        }

        var parts = new List<PhotosToAppPart>();
        for (var start = 0; start < count; start += PartSize)
        {
            var size = Math.Min(PartSize, count - start);
            parts.Add(new PhotosToAppPart($"{folder}/{PartName(start, size)}", size));
        }

        return parts;
    }

    private static string PartName(int start, int size) => $"{start + 1}-{start + size}";

    private static async Task<string> ChooseFolderAsync(IDevice device, string appId, DateTime today, CancellationToken ct)
    {
        var name = "DeviceDeck " + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using var fs = await device.OpenFileSystemAsync(RootOf(appId), ct).ConfigureAwait(false);
        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? name : $"{name} ({n})";
            if (await fs.StatAsync("/" + candidate, ct).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }
    }

    /// <summary>Checks the request and returns the app's name as the phone shows it.</summary>
    private static async Task<string> ValidateAsync(IDevice device, string appId, IReadOnlyList<string> files, CancellationToken ct)
    {
        RequireList(files, "photo");
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("appId is required.");
        }

        foreach (var file in files)
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
        return roots.FirstOrDefault(r => r.Key == RootOf(appId))?.Label
            ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That app doesn't share files with a PC, or isn't on the phone.");
    }

    protected override async Task<string> DescribeAsync(IDevice device, PhotosToAppJob request, CancellationToken ct)
    {
        var label = await ValidateAsync(device, request.AppId, request.Files, ct).ConfigureAwait(false);
        var what = request.Files.Count == 1 ? Path.GetFileName(request.Files[0].TrimEnd('\\', '/')) : Plural(request.Files.Count, "item", "items");
        var where = request.Folder is null ? label : $"{label} › {request.Folder}";
        return $"Adding {what} to {where} on {device.Summary.Name}";
    }

    /// <summary>The PC files a request brings: files as they are, a folder's pictures and videos (flattened), in order.</summary>
    private static List<string> Expand(IReadOnlyList<string> files)
    {
        var expanded = new List<string>();
        foreach (var local in files)
        {
            var trimmed = local.TrimEnd('\\', '/');
            if (Directory.Exists(trimmed))
            {
                expanded.AddRange(Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories)
                    .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
                    .Order(StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(trimmed))
            {
                expanded.Add(trimmed);
            }
            else
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{Path.GetFileName(trimmed)} isn't on this PC any more.", new FileNotFoundException(local));
            }
        }

        return expanded;
    }

    protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, PhotosToAppJob request, CancellationToken ct)
    {
        var files = Expand(request.Files);
        var plans = new List<TransferItemPlan>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var folder = request.Folder switch
            {
                null => "",
                var dated when files.Count <= PartSize => "/" + dated,
                var dated => $"/{dated}/{PartName(i / PartSize * PartSize, Math.Min(PartSize, files.Count - i / PartSize * PartSize))}",
            };
            var name = PhotoBackupNaming.SafeName(Path.GetFileName(files[i]));
            plans.Add(new TransferItemPlan(files[i], $"{folder}/{name}", name, new FileInfo(files[i]).Length));
        }

        return Task.FromResult<IReadOnlyList<TransferItemPlan>>(plans);
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, PhotosToAppJob request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var fs = await context.GetFileSystemAsync(RootOf(request.AppId), ct).ConfigureAwait(false);
        var destination = item.Destination;
        // The dated folder, then its part ("/DeviceDeck 2026-10-21/1-200"): one level at a time.
        for (var cut = destination.IndexOf('/', 1); cut > 0; cut = destination.IndexOf('/', cut + 1))
        {
            await EnsureDeviceFolderAsync(fs, destination[..cut], ct).ConfigureAwait(false);
        }

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
