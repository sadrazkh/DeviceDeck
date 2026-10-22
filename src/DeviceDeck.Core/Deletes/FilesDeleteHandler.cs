using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Core.Deletes;

/// <summary>
/// v0.6: <see cref="TransferKind.FilesDelete"/>, the files and folders the user confirmed in Files, App files or
/// Clean-up. A folder goes with everything in it; a file or folder still listed afterwards failed.
/// </summary>
public sealed class FilesDeleteHandler : ItemDeleteHandler<FilesDeleteRequest, FileDeleteItem>
{
    public const string Deleting = "deleting";

    public override TransferKind Kind => TransferKind.FilesDelete;

    protected override string DeletingStage => Deleting;

    protected override string ChangedNote => "It changed after it was chosen (another size), so it was left alone.";

    protected override string MissingNote => "It isn't on the phone any more.";

    protected override void Validate(IDevice device, FilesDeleteRequest request)
    {
        foreach (var item in request.Items)
        {
            if (string.IsNullOrEmpty(item.Root) || string.IsNullOrEmpty(item.Path))
            {
                throw new ArgumentException("Every item needs a root and a path.");
            }

            // Never a whole shared folder: only what is inside it.
            if (item.Path.Trim('/').Length == 0)
            {
                throw new ArgumentException("A whole shared folder can't be deleted; choose what is inside it.");
            }

            var needs = item.Root.StartsWith("app:", StringComparison.Ordinal) ? DeviceCapability.AppDocuments : DeviceCapability.Files;
            if ((device.Summary.Capabilities & needs) == 0)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} doesn't let the PC delete files there.");
            }
        }
    }

    protected override string TitleOf(IDevice device, FilesDeleteRequest request) =>
        Title("Deleting", NameOf(request.Items[0].Path), "items", request.Items.Count, device.Summary.Name);

    protected override TransferItemPlan PlanOf(FileDeleteItem item) => new(item.Root + "\n" + item.Path, "", NameOf(item.Path), item.Size);

    protected override async Task<ItemCheck[]> StillTheSameAsync(TransferWorkContext context, FilesDeleteRequest request, CancellationToken ct)
    {
        var checks = new ItemCheck[request.Items.Count];
        for (var i = 0; i < checks.Length; i++)
        {
            var item = request.Items[i];
            var fs = await context.GetFileSystemAsync(item.Root, ct).ConfigureAwait(false);
            checks[i] = Check(item, await fs.StatAsync(item.Path, ct).ConfigureAwait(false));
        }

        return checks;
    }

    protected override async Task EnqueueCopiesAsync(TransferQueue queue, IDevice device, IReadOnlyList<FileDeleteItem> items, string folder, List<string> jobs, CancellationToken ct)
    {
        foreach (var root in items.GroupBy(i => i.Root, StringComparer.Ordinal))
        {
            var job = await queue.EnqueueAsync(device.Summary.Id, TransferKind.CopyToPc, new CopyToPcRequest(root.Key, root.Select(i => i.Path).ToList(), folder),
                ConflictRule.KeepBoth, ct).ConfigureAwait(false);
            jobs.Add(job.Id);
        }
    }

    protected override async Task DeleteAsync(TransferWorkContext context, FileDeleteItem item, CancellationToken ct)
    {
        var fs = await context.GetFileSystemAsync(item.Root, ct).ConfigureAwait(false);
        // Jobs take turns, so a file may have changed since it was confirmed (a log that grew, one copied over): it stays.
        if (Check(item, await fs.StatAsync(item.Path, ct).ConfigureAwait(false)) is var check and not ItemCheck.Same)
        {
            throw new ItemLeftAloneException(check, check == ItemCheck.Missing ? MissingNote : ChangedNote);
        }

        await fs.DeleteAsync(item.Path, recursive: item.IsDirectory, ct).ConfigureAwait(false);
        if (await fs.StatAsync(item.Path, ct).ConfigureAwait(false) is not null)
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, "The phone still lists it after the delete.");
        }
    }

    /// <summary>A file at the confirmed size, or a folder (what is in it may change), is as the user confirmed it.</summary>
    private static ItemCheck Check(FileDeleteItem item, FileEntry? entry) =>
        entry is null ? ItemCheck.Missing
        : entry.IsDirectory != item.IsDirectory || (!item.IsDirectory && entry.Size != item.Size) ? ItemCheck.Changed
        : ItemCheck.Same;

    private static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }
}
