using DeviceDeck.Core.Abstractions;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.Transfers.Handlers;

/// <summary>apps.install: one .ipa, or the .apk files of one app (split APKs), as a single item.</summary>
public sealed class AppInstallHandler : TransferHandler<AppInstallRequest>
{
    private static readonly HashSet<string> AndroidPackages = new(StringComparer.OrdinalIgnoreCase) { ".apk", ".apks", ".xapk", ".apkm" };

    public override TransferKind Kind => TransferKind.AppInstall;

    protected override Task<string> DescribeAsync(IDevice device, AppInstallRequest request, CancellationToken ct)
    {
        RequireList(request.Files, "app file");
        foreach (var file in request.Files)
        {
            if (!Path.IsPathFullyQualified(file) || file.Contains('\n'))
            {
                throw new ArgumentException("Each file must be a full path on this PC.");
            }
        }

        // The wrong kind of package fails the call at once, before a job exists.
        if (device.Summary.Platform == DevicePlatform.Ios
                ? request.Files.Count != 1 || !Path.GetExtension(request.Files[0]).Equals(".ipa", StringComparison.OrdinalIgnoreCase)
                : request.Files.Any(f => !AndroidPackages.Contains(Path.GetExtension(f))))
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, device.Summary.Platform == DevicePlatform.Ios
                ? "An iPhone installs one .ipa file at a time."
                : "Android installs .apk, .apks, .xapk or .apkm files.");
        }

        return Task.FromResult($"Installing {Path.GetFileName(request.Files[0])}");
    }

    protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, AppInstallRequest request, CancellationToken ct)
    {
        var missing = request.Files.FirstOrDefault(f => !File.Exists(f));
        if (missing is not null)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{Path.GetFileName(missing)} isn't on this PC any more.", new FileNotFoundException(missing));
        }

        var name = Path.GetFileName(request.Files[0]);
        if (request.Files.Count > 1)
        {
            name += $" + {request.Files.Count - 1}";
        }

        var size = request.Files.Sum(f => new FileInfo(f).Length);
        IReadOnlyList<TransferItemPlan> plans = [new TransferItemPlan(string.Join('\n', request.Files), "", name, size)];
        return Task.FromResult(plans);
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, AppInstallRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var size = item.Size;
        var report = new InlineProgress<InstallProgress>(p => progress.Report((long)(size * Math.Clamp(p.Percent ?? 0, 0, 100) / 100)));
        await context.Device.Apps.InstallAsync(item.Source.Split('\n'), report, ct).ConfigureAwait(false);
        progress.Report(size);
        return TransferItemState.Done;
    }
}

/// <summary>apps.export: an Android app's APK files into a PC folder.</summary>
public sealed class AppExportHandler : TransferHandler<AppExportRequest>
{
    public override TransferKind Kind => TransferKind.AppExport;

    protected override async Task<string> DescribeAsync(IDevice device, AppExportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.AppId))
        {
            throw new ArgumentException("appId is required.");
        }

        RequirePcFolder(request.Destination, "The destination");
        if ((device.Summary.Capabilities & DeviceCapability.AppsExport) == 0)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"Apps can't be saved from {device.Summary.Name}.");
        }

        var app = await FindAsync(device, request.AppId, ct).ConfigureAwait(false);
        return $"Saving {app.Name} to {request.Destination}";
    }

    protected override async Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, AppExportRequest request, CancellationToken ct)
    {
        var app = await FindAsync(context.Device, request.AppId, ct).ConfigureAwait(false);
        return [new TransferItemPlan(app.Id, request.Destination, app.Name, app.AppBytes)];
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, AppExportRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(request.Destination);
        await context.Device.Apps.ExportAsync(item.Source, request.Destination, ct).ConfigureAwait(false);
        progress.Report(item.Size);
        return TransferItemState.Done;
    }

    private static async Task<AppInfo> FindAsync(IDevice device, string appId, CancellationToken ct)
    {
        var apps = await device.Apps.ListAsync(includeSystem: true, ct).ConfigureAwait(false);
        return apps.FirstOrDefault(a => a.Id == appId)
            ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That app isn't on the phone.");
    }
}

public static class TransferHandlers
{
    /// <summary>One handler per <see cref="TransferKind"/>.</summary>
    public static IReadOnlyList<ITransferHandler> All(Imaging.IImageTranscoder? transcoder = null) =>
    [
        new PhotosExportHandler(transcoder),
        new PhotosImportHandler(),
        new CopyToPcHandler(),
        new CopyToDeviceHandler(),
        new AppInstallHandler(),
        new AppExportHandler(),
        new PhotosToAppHandler(),
        // Round 4, WhatsApp on Android.
        new WhatsApp.WhatsAppMediaHandler(),
        // v0.4.1: deleting photos as a job of its own (Photos and Clean-up).
        new PhotoDelete.PhotoDeleteHandler(),
    ];
}
