using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Deletes;

/// <summary>
/// v0.6: <see cref="TransferKind.AppsUninstall"/>, the apps the user confirmed in Apps or Clean-up, removed one at a
/// time. With copy first (Android), each app's APK is saved to the PC before anything goes; its data is not.
/// </summary>
public sealed class AppsUninstallHandler : ItemDeleteHandler<AppsUninstallRequest, AppUninstallItem>
{
    public const string Removing = "removing";

    public override TransferKind Kind => TransferKind.AppsUninstall;

    protected override string DeletingStage => Removing;

    protected override string ChangedNote => "It is a system app now, so it was left alone.";

    protected override string MissingNote => "It isn't installed any more, so there was nothing to remove.";

    protected override void Validate(IDevice device, AppsUninstallRequest request)
    {
        var caps = device.Summary.Capabilities;
        if (!caps.HasFlag(DeviceCapability.AppsUninstall))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't remove apps from the PC.");
        }

        if (request.CopyFirstTo is not null && !caps.HasFlag(DeviceCapability.AppsExport))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "Apps on this phone can't be copied to the PC.");
        }

        if (request.Items.Any(i => string.IsNullOrEmpty(i.Id)))
        {
            throw new ArgumentException("Every app needs an id.");
        }
    }

    protected override string TitleOf(IDevice device, AppsUninstallRequest request) =>
        Title("Removing", NameOf(request.Items[0]), "apps", request.Items.Count, device.Summary.Name);

    protected override TransferItemPlan PlanOf(AppUninstallItem item) => new(item.Id, "", NameOf(item), item.Size);

    protected override async Task<ItemCheck[]> StillTheSameAsync(TransferWorkContext context, AppsUninstallRequest request, CancellationToken ct)
    {
        var installed = (await context.Device.Apps.ListAsync(true, ct).ConfigureAwait(false)).ToDictionary(a => a.Id, StringComparer.Ordinal);
        return request.Items.Select(i => !installed.TryGetValue(i.Id, out var app) ? ItemCheck.Missing
            : app.Source == AppSource.System ? ItemCheck.Changed
            : ItemCheck.Same).ToArray();
    }

    protected override async Task EnqueueCopiesAsync(TransferQueue queue, IDevice device, IReadOnlyList<AppUninstallItem> items, string folder, List<string> jobs, CancellationToken ct)
    {
        foreach (var item in items)
        {
            var job = await queue.EnqueueAsync(device.Summary.Id, TransferKind.AppExport, new AppExportRequest(item.Id, folder), ConflictRule.KeepBoth, ct).ConfigureAwait(false);
            jobs.Add(job.Id);
        }
    }

    protected override Task DeleteAsync(TransferWorkContext context, AppUninstallItem item, CancellationToken ct) =>
        context.Device.Apps.UninstallAsync(item.Id, ct);

    private static string NameOf(AppUninstallItem item) => item.Name is { Length: > 0 } name ? name : item.Id;
}
