using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.WhatsApp;

// Round 4: WhatsApp on Android. Its chats (Databases/msgstore.db.crypt14 or .crypt15) are encrypted with a key that
// only the phone holds, so DeviceDeck never opens them; it copies WhatsApp's folders as they are, so the media can be
// kept and the encrypted backups put back on a phone for WhatsApp itself to restore.

/// <summary>whatsapp.backupAndroid: the dated PC folder WhatsApp's folders are copied into.</summary>
public sealed record WhatsAppMediaRequest(string Folder);

/// <summary>Where WhatsApp keeps its files on an Android phone, and what of them there is.</summary>
public static class WhatsAppAndroid
{
    /// <summary>The two apps: package, account, the folder's name (also the app's name on the PC).</summary>
    public static readonly IReadOnlyList<(string Package, WhatsAppAccount Account, string Name)> Apps =
    [
        ("com.whatsapp", WhatsAppAccount.WhatsApp, "WhatsApp"),
        ("com.whatsapp.w4b", WhatsAppAccount.Business, "WhatsApp Business"),
    ];

    /// <summary>The parts copied: photos, videos, voice notes and documents, then WhatsApp's own encrypted backups.</summary>
    public static readonly IReadOnlyList<string> Parts = ["Media", "Databases", "Backups"];

    /// <summary>The storage root the folders are read from.</summary>
    public const string Root = "internal";

    /// <summary>Android/media/&lt;package&gt;/&lt;name&gt; since 2021; /&lt;name&gt; at the top of the storage before that.</summary>
    public static string FolderOf(string package, string name) => $"/Android/media/{package}/{name}";

    /// <summary>"WhatsApp - Galaxy S25 - 2026-10-21" in <paramref name="parent"/>, with " (2)" when that is taken.</summary>
    public static string DatedFolder(string parent, string deviceName, DateTimeOffset now)
    {
        var path = Path.Combine(parent, SafeName($"WhatsApp - {deviceName} - {now:yyyy-MM-dd}"));
        return File.Exists(path) || Directory.Exists(path) ? UniquePcPath(path) : path;
    }

    /// <summary>The folder each app keeps on this phone (the current place first, then the old one).</summary>
    internal static async Task<List<(string Package, WhatsAppAccount Account, string Name, string Folder)>> FindAsync(IDeviceFileSystem fs, CancellationToken ct)
    {
        var found = new List<(string, WhatsAppAccount, string, string)>();
        foreach (var (package, account, name) in Apps)
        {
            foreach (var folder in new[] { FolderOf(package, name), "/" + name })
            {
                if (await fs.StatAsync(folder, ct).ConfigureAwait(false) is { IsDirectory: true })
                {
                    found.Add((package, account, name, folder));
                    break;
                }
            }
        }
        return found;
    }

    public static async Task<WhatsAppAndroidInfo> GetInfoAsync(IDevice device, CancellationToken ct)
    {
        RequireAndroid(device);
        IReadOnlyList<AppInfo>? apps = null;
        if ((device.Summary.Capabilities & DeviceCapability.AppsList) != 0)
        {
            try
            {
                apps = await device.Apps.ListAsync(includeSystem: false, ct).ConfigureAwait(false);
            }
            catch (DeviceException)
            {
                // The folders still say what there is.
            }
        }

        var result = new List<WhatsAppAndroidApp>();
        await using var fs = await device.OpenFileSystemAsync(Root, ct).ConfigureAwait(false);
        var found = await FindAsync(fs, ct).ConfigureAwait(false);
        foreach (var (package, account, name) in Apps)
        {
            bool? installed = apps is null ? null : apps.Any(a => a.Id == package);
            var here = found.FirstOrDefault(f => f.Package == package);
            if (here.Folder is null)
            {
                if (installed == true)
                {
                    result.Add(new WhatsAppAndroidApp(package, account, name, FolderOf(package, name), false, true, null, null, []));
                }

                continue;
            }

            var backups = new List<WhatsAppAndroidBackupFile>();
            if (await fs.StatAsync(here.Folder + "/Databases", ct).ConfigureAwait(false) is { IsDirectory: true })
            {
                await foreach (var file in fs.ListAsync(here.Folder + "/Databases", ct).ConfigureAwait(false))
                {
                    if (!file.IsDirectory && file.Name.StartsWith("msgstore", StringComparison.OrdinalIgnoreCase))
                    {
                        backups.Add(new WhatsAppAndroidBackupFile(file.Name, file.Size, file.Modified));
                    }
                }
            }

            int? mediaFiles = null;
            long? mediaBytes = null;
            if (device is IAppFilesSource source && here.Folder.StartsWith("/Android/media/", StringComparison.Ordinal))
            {
                try
                {
                    var usage = await source.MeasureAsync("appmedia:" + package, [$"/{name}/Media"], ct).ConfigureAwait(false);
                    (mediaFiles, mediaBytes) = (usage.Files, usage.Bytes);
                }
                catch (DeviceException)
                {
                }
            }

            result.Add(new WhatsAppAndroidApp(package, account, name, here.Folder, true, installed, mediaFiles, mediaBytes,
                backups.OrderByDescending(b => b.Modified ?? DateTimeOffset.MinValue).ToList()));
        }
        return new WhatsAppAndroidInfo(device.Summary.Id, result);
    }

    internal static void RequireAndroid(IDevice device)
    {
        if (device.Summary.Platform != DevicePlatform.Android)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "On an iPhone, WhatsApp is read from a backup: open Contacts and messages › WhatsApp.");
        }
    }
}

/// <summary>
/// whatsapp.backupAndroid: WhatsApp's Media, Databases and Backups folders (and WhatsApp Business's) copied as they are
/// into "&lt;dated folder&gt;\WhatsApp\Media\…", through the transfer queue like any copy to the PC.
/// </summary>
public sealed class WhatsAppMediaHandler : TransferHandler<WhatsAppMediaRequest>
{
    public override TransferKind Kind => TransferKind.WhatsAppMedia;

    protected override Task<string> DescribeAsync(IDevice device, WhatsAppMediaRequest request, CancellationToken ct)
    {
        RequirePcFolder(request.Folder, "The destination");
        WhatsAppAndroid.RequireAndroid(device);
        return Task.FromResult($"Backing up WhatsApp media from {device.Summary.Name} to {request.Folder}");
    }

    protected override async Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, WhatsAppMediaRequest request, CancellationToken ct)
    {
        var fs = await context.GetFileSystemAsync(WhatsAppAndroid.Root, ct).ConfigureAwait(false);
        var found = await WhatsAppAndroid.FindAsync(fs, ct).ConfigureAwait(false);
        if (found.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound,
                "WhatsApp's folder isn't on this phone (Android/media/com.whatsapp/WhatsApp). Open WhatsApp once, then try again.");
        }

        var plans = new List<TransferItemPlan>();
        foreach (var app in found)
        {
            foreach (var part in WhatsAppAndroid.Parts)
            {
                var folder = app.Folder + "/" + part;
                if (await fs.StatAsync(folder, ct).ConfigureAwait(false) is { IsDirectory: true })
                {
                    await WalkAsync(fs, folder, Path.Combine(request.Folder, SafeName(app.Name), part), plans, ct).ConfigureAwait(false);
                }
            }
        }

        if (plans.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "WhatsApp hasn't saved any media or backups on this phone yet.");
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
                await WalkAsync(fs, child.Path, childTarget, plans, ct).ConfigureAwait(false);
            }
            else
            {
                plans.Add(new TransferItemPlan(child.Path, childTarget, child.Name, child.Size));
            }
        }
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, WhatsAppMediaRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var destination = ResolvePcDestination(context, item, item.Destination);
        if (destination is null)
        {
            return TransferItemState.Skipped;
        }

        item.Destination = destination;
        var fs = await context.GetFileSystemAsync(WhatsAppAndroid.Root, ct).ConfigureAwait(false);
        await WritePcFileAsync(destination, item.Size, file => fs.ReadAsync(item.Source, file, progress, ct), ct).ConfigureAwait(false);
        return TransferItemState.Done;
    }
}
