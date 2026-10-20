using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Backups;

/// <summary>
/// An Android backup without a helper app: every user app's APK files and the shared storage people care about
/// (camera, pictures, videos, music, downloads, documents, recordings, messenger media) copied into a PC folder.
/// App data, contacts, messages and settings stay on the phone: Android only lets an app on the phone read those.
/// A run that stops (cable, app closed) continues in the same folder, skipping what is already there; unchanged files
/// are hard-linked from the previous backup instead of copied again.
/// </summary>
internal static class AndroidBackupEngine
{
    public const string AppsFolder = "apps";
    public const string MediaFolder = "media";
    public const string StorageRoot = "internal";

    /// <summary>Top-level folders of shared storage that are copied, when the phone has them.</summary>
    public static readonly string[] MediaFolders =
    [
        "DCIM", "Pictures", "Movies", "Music", "Download", "Documents", "Recordings", "Audiobooks", "Podcasts",
        "Ringtones", "Notifications", "Alarms", "Android/media",
    ];

    public sealed record Totals(int Files, long Bytes);

    /// <param name="folder">This backup's folder; it may already hold part of the backup from a run that stopped.</param>
    /// <param name="previous">The phone's last complete backup, to link unchanged files from.</param>
    /// <param name="planned">Called once with what the run will copy.</param>
    /// <param name="progress">Files and bytes done so far.</param>
    public static async Task<AndroidBackupManifest> RunAsync(IDevice device, string folder, (string Folder, AndroidBackupManifest Manifest)? previous,
        Action<Totals> planned, Action<Totals> progress, CancellationToken ct)
    {
        var summary = device.Summary;
        var startedAt = AndroidBackupManifest.TryRead(folder)?.CreatedAt ?? DateTimeOffset.Now;
        Directory.CreateDirectory(folder);

        var apps = (summary.Capabilities & (DeviceCapability.AppsList | DeviceCapability.AppsExport)) == (DeviceCapability.AppsList | DeviceCapability.AppsExport)
            ? (await device.Apps.ListAsync(includeSystem: false, ct).ConfigureAwait(false)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList()
            : [];

        var media = new List<(string Path, long Size, DateTimeOffset? Modified)>();
        var copiedFolders = new List<string>();
        if ((summary.Capabilities & DeviceCapability.Files) != 0)
        {
            await using var fs = await device.OpenFileSystemAsync(StorageRoot, ct).ConfigureAwait(false);
            foreach (var top in MediaFolders)
            {
                var path = "/" + top;
                if (await fs.StatAsync(path, ct).ConfigureAwait(false) is not { IsDirectory: true })
                {
                    continue;
                }

                copiedFolders.Add(top);
                await WalkAsync(fs, path, media, ct).ConfigureAwait(false);
            }
        }

        var manifest = new AndroidBackupManifest
        {
            DeviceId = summary.Id,
            DeviceName = summary.Name,
            Model = summary.Model,
            MarketingName = summary.MarketingName,
            OsVersion = summary.OsVersion,
            CreatedAt = startedAt,
            Complete = false,
            Folders = copiedFolders,
        };
        manifest.Write(folder);
        planned(new Totals(apps.Count + media.Count, apps.Sum(a => a.AppBytes) + media.Sum(m => m.Size)));

        var done = new Totals(0, 0);
        void Advance(int files, long bytes)
        {
            done = new Totals(done.Files + files, done.Bytes + bytes);
            progress(done);
        }

        var savedApps = new List<AndroidBackupApp>();
        foreach (var app in apps)
        {
            ct.ThrowIfCancellationRequested();
            var saved = await SaveAppAsync(device, folder, app, previous, ct).ConfigureAwait(false);
            if (saved is not null)
            {
                savedApps.Add(saved);
            }

            Advance(1, app.AppBytes);
        }

        if (media.Count > 0)
        {
            await using var fs = await device.OpenFileSystemAsync(StorageRoot, ct).ConfigureAwait(false);
            foreach (var file in media)
            {
                ct.ThrowIfCancellationRequested();
                var before = done.Bytes;
                await CopyMediaAsync(fs, folder, file, previous?.Folder, bytes => progress(done with { Bytes = before + bytes }), ct).ConfigureAwait(false);
                done = new Totals(done.Files, before);
                Advance(1, file.Size);
            }
        }

        var finished = manifest with
        {
            Complete = true,
            Apps = savedApps,
            MediaFiles = media.Count,
            MediaBytes = media.Sum(m => m.Size),
        };
        finished.Write(folder);
        return finished;
    }

    private static async Task WalkAsync(IDeviceFileSystem fs, string path, List<(string, long, DateTimeOffset?)> into, CancellationToken ct)
    {
        var folders = new List<string>();
        try
        {
            await foreach (var entry in fs.ListAsync(path, ct).ConfigureAwait(false))
            {
                // Thumbnails caches, trashed and pending files, and other hidden entries aren't the user's media.
                if (entry.Name.StartsWith('.'))
                {
                    continue;
                }

                if (entry.IsDirectory)
                {
                    folders.Add(entry.Path);
                }
                else
                {
                    into.Add((entry.Path, entry.Size, entry.Modified));
                }
            }
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound)
        {
            return;
        }
        foreach (var folder in folders)
        {
            await WalkAsync(fs, folder, into, ct).ConfigureAwait(false);
        }
    }

    private static async Task<AndroidBackupApp?> SaveAppAsync(IDevice device, string folder, AppInfo app, (string Folder, AndroidBackupManifest Manifest)? previous, CancellationToken ct)
    {
        var target = Path.Combine(folder, AppsFolder, Safe(app.Id));
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*.apk").Any())
        {
            return Describe(folder, app, target);
        }

        // The same version in the last backup: link its files rather than pull them again.
        if (previous is { } last && last.Manifest.Apps.FirstOrDefault(a => a.Id == app.Id && a.Version == app.Version) is { } earlier
            && earlier.Files.All(f => File.Exists(Path.Combine(last.Folder, f))))
        {
            var staged = target + ".ddpart";
            ResetFolder(staged);
            if (earlier.Files.All(f => HardLinks.TryLink(Path.Combine(staged, Path.GetFileName(f)), Path.Combine(last.Folder, f))))
            {
                Directory.Move(staged, target);
                return Describe(folder, app, target);
            }
            ResetFolder(staged);
        }

        var temp = target + ".ddpart";
        ResetFolder(temp);
        try
        {
            await device.Apps.ExportAsync(app.Id, temp, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PathNotFound or DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.NotSupported)
        {
            // Uninstalled meanwhile, or the phone won't hand this one out: the backup goes on without it.
            Directory.Delete(temp, recursive: true);
            return null;
        }
        Directory.Move(temp, target);
        return Describe(folder, app, target);
    }

    private static AndroidBackupApp Describe(string folder, AppInfo app, string target)
    {
        var files = Directory.EnumerateFiles(target).OrderBy(f => f, StringComparer.Ordinal).ToList();
        return new AndroidBackupApp(app.Id, app.Name, app.Version,
            files.Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).ToList(),
            files.Sum(f => new FileInfo(f).Length));
    }

    private static async Task CopyMediaAsync(IDeviceFileSystem fs, string folder, (string Path, long Size, DateTimeOffset? Modified) file,
        string? previousFolder, Action<long> progress, CancellationToken ct)
    {
        var relative = Path.Combine([MediaFolder, .. file.Path.Trim('/').Split('/').Select(Safe)]);
        var target = Path.Combine(folder, relative);
        var existing = new FileInfo(target);
        if (existing.Exists && existing.Length == file.Size)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (previousFolder is not null && file.Modified is { } modified)
        {
            var earlier = new FileInfo(Path.Combine(previousFolder, relative));
            if (earlier.Exists && earlier.Length == file.Size && earlier.LastWriteTimeUtc == modified.UtcDateTime
                && HardLinks.TryLink(target + ".ddpart", earlier.FullName))
            {
                File.Move(target + ".ddpart", target, overwrite: true);
                return;
            }
        }

        var temp = target + ".ddpart";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await fs.ReadAsync(file.Path, stream, new InlineProgress(progress), ct).ConfigureAwait(false);
        }

        if (file.Modified is { } when)
        {
            File.SetLastWriteTimeUtc(temp, when.UtcDateTime);
        }

        File.Move(temp, target, overwrite: true);
    }

    private static void ResetFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>A name Windows accepts: reserved characters become '_'.</summary>
    internal static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)).TrimEnd('.', ' ');
        return safe.Length == 0 ? "_" : safe;
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

/// <summary>An Android backup folder browsed like an iPhone backup: two domains, "apps" and "media".</summary>
internal sealed class FolderArchive(string folder) : IBackupArchive
{
    public bool Encrypted => false;

    public IReadOnlyList<BackupDomainInfo> Domains { get; } =
        new[] { AndroidBackupEngine.AppsFolder, AndroidBackupEngine.MediaFolder }
            .Where(d => Directory.Exists(Path.Combine(folder, d)))
            .Select(d =>
            {
                var files = new DirectoryInfo(Path.Combine(folder, d)).EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f => !f.Name.EndsWith(".ddpart", StringComparison.Ordinal)).ToList();
                return new BackupDomainInfo(d, files.Count, files.Sum(f => f.Length));
            })
            .ToList();

    public IReadOnlyList<BackupEntry> List(string domain, string path)
    {
        var dir = Local(domain, path);
        if (dir is null || !Directory.Exists(dir))
        {
            return [];
        }

        return new DirectoryInfo(dir).EnumerateFileSystemInfos()
            .Where(i => !i.Name.EndsWith(".ddpart", StringComparison.Ordinal))
            .Select(i => Entry(domain, i))
            .OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<BackupEntry> ListFiles(string domain, string path)
    {
        var local = Local(domain, path);
        if (local is null)
        {
            return [];
        }

        if (File.Exists(local))
        {
            return [Entry(domain, new FileInfo(local))];
        }

        if (!Directory.Exists(local))
        {
            return [];
        }

        return new DirectoryInfo(local).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !f.Name.EndsWith(".ddpart", StringComparison.Ordinal))
            .Select(f => Entry(domain, f))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();
    }

    public BackupEntry? Find(string domain, string path)
    {
        var local = Local(domain, path);
        return local is null ? null
            : File.Exists(local) ? Entry(domain, new FileInfo(local))
            : Directory.Exists(local) ? Entry(domain, new DirectoryInfo(local))
            : null;
    }

    public async Task CopyToAsync(BackupEntry file, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var local = Local(file.Domain, file.Path);
        if (local is null || !File.Exists(local))
        {
            throw new DeviceException(BackupErrors.Missing, $"{file.Name} isn't in this backup.");
        }

        await using var source = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[256 * 1024];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report(done);
        }
    }

    public string? PlainPath(BackupEntry file) => Local(file.Domain, file.Path) is { } local && File.Exists(local) ? local : null;

    private string? Local(string domain, string path)
    {
        if (domain is not (AndroidBackupEngine.AppsFolder or AndroidBackupEngine.MediaFolder))
        {
            return null;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.Contains('\\') || p.Contains(':')))
        {
            return null;
        }

        return Path.Combine([folder, domain, .. parts]);
    }

    private BackupEntry Entry(string domain, FileSystemInfo info)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        var relative = Path.GetRelativePath(Path.Combine(folder, domain), info.FullName).Replace('\\', '/');
        return new BackupEntry(domain, relative == "." ? "" : relative, info.Name, isDirectory,
            isDirectory ? 0 : ((FileInfo)info).Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    public void Dispose()
    {
    }
}
