using System.Globalization;
using System.Text.Json;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Serialization;

namespace DeviceDeck.Core.Backups;

/// <summary>
/// The backups on this PC. DeviceDeck's folder holds, per iPhone, the folder the phone updates ("{udid}", Apple's
/// layout, as iTunes keeps it) and a hard-linked dated copy of each finished backup ("{udid}-yyyyMMdd-HHmmss", the
/// name iTunes gives an archived backup); per Android phone, one folder per backup ("{serial}-yyyyMMdd-HHmmss").
/// iTunes' and Apple Devices' folders are listed too, read-only.
/// </summary>
internal sealed class BackupLibrary(IIosBackupFormat? ios, Func<string> deviceDeckRoot, IReadOnlyList<string> appleRoots)
{
    public const string DeviceDeckPrefix = "dd";
    public const string ApplePrefix = "apple";
    public const string StampFormat = "yyyyMMdd-HHmmss";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime Stamp, BackupRecord? Record)> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, long Bytes)> _sizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _linkSupport = new(StringComparer.OrdinalIgnoreCase);

    public string Root => deviceDeckRoot();

    /// <summary>Every backup found, newest first. The folder an iPhone updates is marked hidden when a dated copy of it exists.</summary>
    public List<BackupRecord> Scan()
    {
        var records = new List<BackupRecord>();
        ScanRoot(Root, DeviceDeckPrefix, BackupSource.DeviceDeck, records);
        for (var i = 0; i < appleRoots.Count; i++)
        {
            // Pointing DeviceDeck's folder at Apple's own lists those backups once, as DeviceDeck's.
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(appleRoots[i])), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)), StringComparison.OrdinalIgnoreCase))
            {
                ScanRoot(appleRoots[i], ApplePrefix + i.ToString(CultureInfo.InvariantCulture), BackupSource.Apple, records);
            }
        }

        // A live folder whose last run finished is the same backup as its newest dated copy.
        var copies = records.Where(r => r.Ios is not null && !r.IsLive).Select(r => (r.Info.DeviceId, r.Ios!.SnapshotId)).ToHashSet();
        return records
            .Select(r => r.IsLive && r.Info.Source == BackupSource.DeviceDeck && r.Ios is { Complete: true, SnapshotId: { } id } && copies.Contains((r.Info.DeviceId, id))
                ? r with { Hidden = true }
                : r)
            .OrderByDescending(r => r.Info.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
    }

    private void ScanRoot(string root, string prefix, BackupSource source, List<BackupRecord> records)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var folder in folders)
        {
            // A dated copy being made, or a backup being deleted.
            if (folder.EndsWith(".partial", StringComparison.Ordinal) || folder.EndsWith(".deleting", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (Read(folder, prefix, source) is { } record)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A folder being written or not ours: skip it.
            }
        }
    }

    /// <summary>One backup by id, without scanning the others; null when the id names no backup.</summary>
    public BackupRecord? Load(string backupId)
    {
        if (Resolve(backupId) is not { } folder)
        {
            return null;
        }

        var prefix = backupId[..backupId.IndexOf(':')];
        return Read(folder, prefix, prefix == DeviceDeckPrefix ? BackupSource.DeviceDeck : BackupSource.Apple);
    }

    /// <summary>A folder's backup, read again only when one of the files that describe it changed.</summary>
    private BackupRecord? Read(string folder, string prefix, BackupSource source)
    {
        var stamp = new[] { "Info.plist", "Manifest.plist", "Status.plist", "Manifest.db", AndroidBackupManifest.FileName }
            .Select(f => File.GetLastWriteTimeUtc(Path.Combine(folder, f)))
            .Append(Directory.GetLastWriteTimeUtc(folder))
            .Max();
        lock (_gate)
        {
            if (_records.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
            {
                return cached.Record;
            }
        }
        var record = ReadNow(folder, prefix, source);
        lock (_gate)
        {
            _records[folder] = (stamp, record);
        }

        return record;
    }

    private BackupRecord? ReadNow(string folder, string prefix, BackupSource source)
    {
        var name = Path.GetFileName(folder);
        var id = prefix + ":" + name;
        if (source == BackupSource.DeviceDeck && AndroidBackupManifest.TryRead(folder) is { } android)
        {
            var info = new BackupInfo(id, android.DeviceId, android.DeviceName, DevicePlatform.Android, android.Model, android.MarketingName,
                android.OsVersion, android.CreatedAt, android.MediaBytes + android.Apps.Sum(a => a.Bytes), false,
                android.Complete ? BackupState.Complete : BackupState.Incomplete, source, folder, android.Apps.Count, false);
            return new BackupRecord(info, folder, null, android, false);
        }

        if (ios?.ReadHeader(folder) is not { } header)
        {
            return null;
        }

        var live = string.Equals(name, header.DeviceId, StringComparison.OrdinalIgnoreCase);
        var iosInfo = new BackupInfo(id, header.DeviceId, header.DeviceName, DevicePlatform.Ios, header.ProductType, header.MarketingName,
            header.ProductVersion, header.Date ?? StampOf(name), SizeOf(folder), header.Encrypted,
            header.Complete ? BackupState.Complete : BackupState.Incomplete, source, folder, header.AppCount, header.Encrypted);
        return new BackupRecord(iosInfo, folder, header, null, live);
    }

    /// <summary>The folder of a backup id, or null when the id doesn't name one of the known folders.</summary>
    public string? Resolve(string backupId)
    {
        var cut = backupId.IndexOf(':');
        if (cut <= 0)
        {
            return null;
        }

        var prefix = backupId[..cut];
        var name = backupId[(cut + 1)..];
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        string? root = prefix == DeviceDeckPrefix ? Root
            : prefix.StartsWith(ApplePrefix, StringComparison.Ordinal) && int.TryParse(prefix.AsSpan(ApplePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < appleRoots.Count
                ? appleRoots[i]
                : null;
        if (root is null)
        {
            return null;
        }

        var folder = Path.Combine(root, name);
        return Directory.Exists(folder) ? folder : null;
    }

    public static string IdFor(string folderName) => DeviceDeckPrefix + ":" + folderName;

    /// <summary>Can the backup folder's drive keep dated copies (hard links)? Checked once per folder.</summary>
    public bool KeepsHistory()
    {
        var root = Root;
        lock (_gate)
        {
            if (_linkSupport.TryGetValue(root, out var known))
            {
                return known;
            }
        }
        bool supported;
        try
        {
            supported = HardLinks.IsSupported(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            supported = false;
        }
        lock (_gate)
        {
            _linkSupport[root] = supported;
        }

        return supported;
    }

    /// <summary>
    /// A hard-linked dated copy of an iPhone's live folder, named after when the backup finished. Returns its folder
    /// name, or null when the drive can't link (the live folder then stays the only copy).
    /// </summary>
    public string? Snapshot(string liveFolder, string deviceId, DateTimeOffset finished, CancellationToken ct)
    {
        if (!KeepsHistory())
        {
            return null;
        }

        var baseName = deviceId + "-" + finished.ToLocalTime().ToString(StampFormat, CultureInfo.InvariantCulture);
        var name = baseName;
        for (var n = 2; Directory.Exists(Path.Combine(Root, name)) || Directory.Exists(Path.Combine(Root, name + ".partial")); n++)
        {
            name = $"{baseName}-{n}";
        }

        var partial = Path.Combine(Root, name + ".partial");
        if (!HardLinks.Mirror(liveFolder, partial, path => path.EndsWith(".ddpart", StringComparison.Ordinal), ct))
        {
            Directory.Delete(partial, recursive: true);
            lock (_gate)
            {
                _linkSupport[Root] = false;
            }

            return null;
        }
        Directory.Move(partial, Path.Combine(Root, name));
        return name;
    }

    /// <summary>What deleting <paramref name="doomed"/> frees: their files that no other backup in the folder shares.</summary>
    public long FreedBytes(IReadOnlyCollection<BackupRecord> doomed)
    {
        var doomedFolders = doomed.Select(d => Path.GetFullPath(d.Folder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only the same phones' folders can share files with them.
        var phones = doomedFolders.Select(f => PhoneKey(Path.GetFileName(f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<FileKey>();
        if (Directory.Exists(Root))
        {
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                if (!doomedFolders.Contains(Path.GetFullPath(folder)) && phones.Contains(PhoneKey(Path.GetFileName(folder))))
                {
                    Collect(folder, kept, null);
                }
            }
        }
        var freed = 0L;
        var seen = new HashSet<FileKey>();
        foreach (var folder in doomedFolders)
        {
            Collect(folder, seen, key => freed += kept.Contains(key) ? 0 : key.Size);
        }

        return freed;
    }

    /// <summary>
    /// Files of one folder as (path inside the backup, size, last write): a hard link shares all three with its other
    /// names, and a changed file is written anew, so equal keys mean shared disk space.
    /// </summary>
    private static void Collect(string folder, HashSet<FileKey> into, Action<FileKey>? added)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            var key = new FileKey(Path.GetRelativePath(folder, file.FullName), file.Length, file.LastWriteTimeUtc.Ticks);
            if (into.Add(key))
            {
                added?.Invoke(key);
            }
        }
    }

    private readonly record struct FileKey(string Path, long Size, long Written);

    /// <summary>What a folder holds, cached until its manifest or status changes.</summary>
    public long SizeOf(string folder)
    {
        var stamp = new[] { "Manifest.db", "Status.plist", "Manifest.plist" }
            .Select(f => File.GetLastWriteTimeUtc(Path.Combine(folder, f)))
            .Append(Directory.GetLastWriteTimeUtc(folder))
            .Max();
        lock (_gate)
        {
            if (_sizes.TryGetValue(folder, out var cached) && cached.Stamp == stamp)
            {
                return cached.Bytes;
            }
        }
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var bytes = new DirectoryInfo(folder).EnumerateFiles("*", options).Sum(f => f.Length);
        lock (_gate)
        {
            _sizes[folder] = (stamp, bytes);
        }

        return bytes;
    }

    public void Forget(string folder)
    {
        lock (_gate)
        {
            _sizes.Remove(folder);
            _records.Remove(folder);
        }
    }

    /// <summary>"{phone}-yyyyMMdd-HHmmss[-n]" and a live "{phone}" folder both give "{phone}".</summary>
    internal static string PhoneKey(string folderName)
    {
        var name = folderName.EndsWith(".partial", StringComparison.Ordinal) ? folderName[..^".partial".Length] : folderName;
        var dash = name.LastIndexOf('-');
        if (dash > 0 && dash < name.Length - 1 && name[(dash + 1)..].All(char.IsAsciiDigit) && name.Length - dash - 1 < 4)
        {
            name = name[..dash]; // "-2", "-3" when two backups finished in the same second
        }

        return name.Length > StampFormat.Length + 1 && StampOf(name) is not null ? name[..^(StampFormat.Length + 1)] : name;
    }

    private static DateTimeOffset? StampOf(string folderName) =>
        folderName.Length > StampFormat.Length
        && DateTime.TryParseExact(folderName[^StampFormat.Length..], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)
            ? new DateTimeOffset(when)
            : null;
}

/// <param name="IsLive">An iPhone's own folder in Apple's layout ("{udid}"), which the next backup updates.</param>
/// <param name="Hidden">A live folder holding the same backup as its newest dated copy: not listed on its own.</param>
internal sealed record BackupRecord(BackupInfo Info, string Folder, IosBackupHeader? Ios, AndroidBackupManifest? Android, bool IsLive, bool Hidden = false);

/// <summary>DeviceDeck-backup.json at the top of an Android backup folder.</summary>
public sealed record AndroidBackupManifest
{
    public const string FileName = "DeviceDeck-backup.json";
    public const string FormatName = "devicedeck-android-backup";

    public string Format { get; init; } = FormatName;
    public int Version { get; init; } = 1;
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public string? Model { get; init; }
    public string? MarketingName { get; init; }
    public string? OsVersion { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>False while it is being made, or when the run stopped part-way.</summary>
    public bool Complete { get; init; }

    public IReadOnlyList<AndroidBackupApp> Apps { get; init; } = [];
    public int MediaFiles { get; init; }
    public long MediaBytes { get; init; }

    /// <summary>The top-level folders of the phone's storage that were copied.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public static AndroidBackupManifest? TryRead(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<AndroidBackupManifest>(File.ReadAllBytes(path), DeviceDeckJson.Options);
        return manifest?.Format == FormatName ? manifest : null;
    }

    public void Write(string folder)
    {
        var path = Path.Combine(folder, FileName);
        File.WriteAllBytes(path + ".ddpart", JsonSerializer.SerializeToUtf8Bytes(this, DeviceDeckJson.Options));
        File.Move(path + ".ddpart", path, overwrite: true);
    }
}

/// <param name="Files">Paths inside the backup folder, '/' separated.</param>
public sealed record AndroidBackupApp(string Id, string Name, string Version, IReadOnlyList<string> Files, long Bytes);
