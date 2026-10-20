using DeviceDeck.Core.Backups;
using DeviceDeck.Ios.Plist;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Backup.Format;

/// <summary>One row of Manifest.db's Files table, with what its MBFile blob says.</summary>
/// <param name="FileId">SHA-1 of "{domain}-{relativePath}"; the file is stored at {first two hex digits}/{FileId}.</param>
/// <param name="WrappedKey">Encrypted backups: {4-byte class}{wrapped key}, from the blob's EncryptionKey.</param>
internal sealed record ManifestFile(
    string FileId,
    string Domain,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset? Modified,
    int ProtectionClass,
    byte[]? WrappedKey);

/// <summary>Reads Manifest.db (iOS 10 and later) into memory.</summary>
internal static class ManifestDatabase
{
    // Manifest.db's flags column.
    private const long FlagFile = 1;
    private const long FlagDirectory = 2;

    public static List<ManifestFile> Read(SqliteConnection db)
    {
        var files = new List<ManifestFile>();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT fileID, domain, relativePath, flags, file FROM Files";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var flags = reader.IsDBNull(3) ? FlagFile : reader.GetInt64(3);
            if (flags is not (FlagFile or FlagDirectory))
                continue; // symbolic links and anything newer
            var blob = reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4);
            var meta = blob is null ? default : FileMeta.Parse(blob);
            files.Add(new ManifestFile(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                (reader.IsDBNull(2) ? "" : reader.GetString(2)).Trim('/'),
                flags == FlagDirectory,
                flags == FlagDirectory ? 0 : meta.Size,
                meta.Modified,
                meta.ProtectionClass,
                meta.WrappedKey));
        }
        return files;
    }

    /// <summary>
    /// The MBFile in a row's blob: an NSKeyedArchiver binary plist ({$top: {root: UID}, $objects: […]}) whose values
    /// point at other objects by UID. A flat dictionary with the same keys is read too.
    /// </summary>
    private readonly record struct FileMeta(long Size, DateTimeOffset? Modified, int ProtectionClass, byte[]? WrappedKey)
    {
        public static FileMeta Parse(byte[] blob)
        {
            PlistDict file;
            List<object>? objects = null;
            try
            {
                var top = PlistSerializer.ParseDict(blob);
                objects = top.GetArray("$objects");
                if (objects is not null && top.GetDict("$top")?.TryGetValue("root", out var root) == true)
                    file = Resolve(objects, root) as PlistDict ?? new PlistDict();
                else
                    file = top;
            }
            catch (FormatException)
            {
                return default;
            }

            var modified = PlistValue.AsLong(Resolve(objects, file.GetValueOrDefault("LastModified"))) is { } seconds and > 0
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : (DateTimeOffset?)null;
            var key = Resolve(objects, file.GetValueOrDefault("EncryptionKey")) switch
            {
                byte[] raw => raw,
                PlistDict data => Resolve(objects, data.GetValueOrDefault("NS.data")) as byte[],
                _ => null,
            };
            return new FileMeta(
                PlistValue.AsLong(Resolve(objects, file.GetValueOrDefault("Size"))) ?? 0,
                modified,
                (int)(PlistValue.AsLong(Resolve(objects, file.GetValueOrDefault("ProtectionClass"))) ?? 0),
                key);
        }

        private static object? Resolve(List<object>? objects, object? value) =>
            value is PlistUid uid && objects is not null && uid.Value < (ulong)objects.Count ? objects[(int)uid.Value] : value;
    }
}

/// <summary>An opened Apple-format backup: Manifest.db in memory, files read (and decrypted) from the folder.</summary>
internal sealed class IosBackupArchive : IBackupArchive
{
    private readonly string _folder;
    private readonly BackupKeys? _keys;
    private readonly Dictionary<string, Dictionary<string, BackupEntry>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Domain, string Path), ManifestFile> _files = [];
    private readonly Dictionary<(string Domain, string Parent), List<BackupEntry>> _children = [];

    public IosBackupArchive(string folder, IEnumerable<ManifestFile> files, BackupKeys? keys)
    {
        _folder = folder;
        _keys = keys;
        var stats = new Dictionary<string, (int Files, long Bytes)>(StringComparer.Ordinal);
        // Real folder rows first, parents before children, so a folder is only made up when the backup has no row for it.
        foreach (var file in files.OrderBy(f => f.IsDirectory ? 0 : 1).ThenBy(f => f.Path, StringComparer.Ordinal))
        {
            if (file.Path.Length == 0)
                continue;
            if (!file.IsDirectory)
            {
                _files[(file.Domain, file.Path)] = file;
                var (count, bytes) = stats.GetValueOrDefault(file.Domain);
                stats[file.Domain] = (count + 1, bytes + file.Size);
            }
            Add(new BackupEntry(file.Domain, file.Path, LastSegment(file.Path), file.IsDirectory, file.Size, file.Modified));
        }
        foreach (var list in _children.Values)
            list.Sort(static (x, y) => x.IsDirectory != y.IsDirectory ? (x.IsDirectory ? -1 : 1) : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
        Domains = stats.Select(s => new BackupDomainInfo(s.Key, s.Value.Files, s.Value.Bytes)).ToList();
    }

    public bool Encrypted => _keys is not null;

    public IReadOnlyList<BackupDomainInfo> Domains { get; }

    public IReadOnlyList<BackupEntry> List(string domain, string path) =>
        _children.TryGetValue((domain, path.Trim('/')), out var list) ? list : [];

    public IReadOnlyList<BackupEntry> ListFiles(string domain, string path)
    {
        path = path.Trim('/');
        if (Find(domain, path) is { IsDirectory: false } single)
            return [single];
        var prefix = path.Length == 0 ? "" : path + "/";
        return _entries.TryGetValue(domain, out var all)
            ? all.Values.Where(e => !e.IsDirectory && e.Path.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal).ToList()
            : [];
    }

    public BackupEntry? Find(string domain, string path) =>
        _entries.TryGetValue(domain, out var all) && all.TryGetValue(path.Trim('/'), out var entry) ? entry : null;

    public string? PlainPath(BackupEntry file)
    {
        if (_keys is not null || !_files.TryGetValue((file.Domain, file.Path), out var row))
            return null;
        var path = StoredPath(row);
        return File.Exists(path) ? path : null;
    }

    public async Task CopyToAsync(BackupEntry file, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        if (!_files.TryGetValue((file.Domain, file.Path), out var row))
            throw new Core.Abstractions.DeviceException(BackupErrors.Missing, $"{file.Name} isn't in this backup.");
        var path = StoredPath(row);
        if (!File.Exists(path))
            throw new Core.Abstractions.DeviceException(BackupErrors.Missing, $"{file.Name} is listed in the backup but its data isn't there.");

        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (_keys is null)
        {
            await CopyAsync(source, destination, source.Length, progress, ct).ConfigureAwait(false);
            return;
        }
        if (row.WrappedKey is null)
            throw new Core.Abstractions.DeviceException(BackupErrors.Corrupt, $"{file.Name} has no key in the backup.");
        var key = _keys.Unwrap(row.ProtectionClass, row.WrappedKey.AsSpan(4));
        await using var plain = BackupCrypto.Decrypting(source, key);
        await CopyAsync(plain, destination, row.Size, progress, ct).ConfigureAwait(false);
    }

    private string StoredPath(ManifestFile row) => System.IO.Path.Combine(_folder, row.FileId[..2], row.FileId);

    /// <summary>Copies exactly <paramref name="length"/> bytes (the decrypted stream carries padding after them).</summary>
    private static async Task CopyAsync(Stream source, Stream destination, long length, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        long done = 0;
        while (done < length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), ct).ConfigureAwait(false);
            if (read == 0)
                throw new Core.Abstractions.DeviceException(BackupErrors.Corrupt, "A file in the backup is shorter than the backup says.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report(done);
        }
    }

    private void Add(BackupEntry entry)
    {
        if (!_entries.TryGetValue(entry.Domain, out var all))
            _entries[entry.Domain] = all = new Dictionary<string, BackupEntry>(StringComparer.Ordinal);
        if (!all.TryAdd(entry.Path, entry))
            return;
        var parent = ParentOf(entry.Path);
        if (!_children.TryGetValue((entry.Domain, parent), out var list))
            _children[(entry.Domain, parent)] = list = [];
        list.Add(entry);
        if (parent.Length > 0 && !all.ContainsKey(parent))
            Add(new BackupEntry(entry.Domain, parent, LastSegment(parent), true, 0, null));
    }

    private static string ParentOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut < 0 ? "" : path[..cut];
    }

    private static string LastSegment(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>Holds only managed memory; a reader still copying a file may finish after the backup was closed.</summary>
    public void Dispose()
    {
    }
}
