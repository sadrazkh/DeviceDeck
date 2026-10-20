using System.Globalization;
using DeviceDeck.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.Photos;

/// <summary>One photo the PC holds a copy of. <see cref="PcPath"/> is the still (or the video); <see cref="MotionPcPath"/> a Live Photo's video.</summary>
public sealed record PhotoBackupRecord(
    string Key,
    string? SourcePath,
    string FileName,
    long Size,
    DateTimeOffset? TakenAt,
    string PcPath,
    string? MotionPcPath,
    bool Duplicate,
    string? JobId,
    DateTimeOffset BackedUpAt);

/// <summary>
/// What a phone's backups already hold, for telling new photos from old ones without touching the PC's disk. A photo is
/// known by its key; failing that by its phone path (when the size agrees or isn't known yet), or by name + size + date —
/// so a photo whose key changed (an iPhone read without Photos.sqlite, an Android file moved to another folder) is not
/// counted as new.
/// </summary>
public sealed class PhotoBackupKnownSet
{
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Name, long Size, long Taken)> _fingerprints = [];

    public int Count => _keys.Count;

    internal void Add(string key, string? sourcePath, string fileName, long size, long? taken)
    {
        _keys.Add(key);
        if (sourcePath is not null)
        {
            _paths[sourcePath] = size;
        }

        if (size > 0 && taken is { } t)
        {
            _fingerprints.Add((fileName.ToUpperInvariant(), size, t));
        }
    }

    public bool Contains(PhotoBackupAsset asset)
    {
        if (_keys.Contains(asset.Key))
        {
            return true;
        }

        if (asset.SourcePath is { } path && _paths.TryGetValue(path, out var size) && (asset.Size == 0 || size == 0 || size == asset.Size))
        {
            return true;
        }

        return asset.Size > 0 && asset.TakenAt is { } when && _fingerprints.Contains((asset.FileName.ToUpperInvariant(), asset.Size, when.ToUnixTimeSeconds()));
    }
}

/// <summary>
/// photo-backup.db: per phone, its own backup options, every photo copied (or found already there), the runs, and a
/// cache of SHA-256 sums of PC files so duplicates are found without re-reading big videos each time.
/// Synchronous like the transfer store: SQLite is local and each call is short.
/// </summary>
public sealed class PhotoBackupLedger : IDisposable
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public PhotoBackupLedger(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Migrate();
    }

    private void Migrate()
    {
        using var check = _db.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) >= SchemaVersion)
        {
            return;
        }

        using var tx = _db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS devices (
                device_id      TEXT PRIMARY KEY,
                destination    TEXT NULL,
                pattern        TEXT NULL,
                convert_heic   INTEGER NULL,
                include_hidden INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS assets (
                device_id      TEXT NOT NULL,
                asset_key      TEXT NOT NULL,
                source_path    TEXT NULL,
                file_name      TEXT NOT NULL,
                size           INTEGER NOT NULL,
                taken_at       INTEGER NULL,
                pc_path        TEXT NOT NULL,
                motion_pc_path TEXT NULL,
                duplicate      INTEGER NOT NULL,
                job_id         TEXT NULL,
                backed_up_at   TEXT NOT NULL,
                PRIMARY KEY (device_id, asset_key)
            );
            CREATE INDEX IF NOT EXISTS assets_path ON assets (device_id, source_path COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS runs (
                job_id      TEXT PRIMARY KEY,
                device_id   TEXT NOT NULL,
                destination TEXT NOT NULL,
                state       TEXT NOT NULL,
                planned     INTEGER NOT NULL DEFAULT 0,
                copied      INTEGER NOT NULL DEFAULT 0,
                duplicates  INTEGER NOT NULL DEFAULT 0,
                failed      INTEGER NOT NULL DEFAULT 0,
                bytes       INTEGER NOT NULL DEFAULT 0,
                kept_heic   INTEGER NOT NULL DEFAULT 0,
                started_at  TEXT NOT NULL,
                finished_at TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS runs_device ON runs (device_id, started_at);
            CREATE TABLE IF NOT EXISTS pc_hashes (
                path     TEXT PRIMARY KEY COLLATE NOCASE,
                size     INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                sha256   BLOB NOT NULL
            );
            """, tx);
        Execute($"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    // ---------- per-phone options ----------

    /// <summary>The phone's own options; null fields fall back to Settings.</summary>
    public (string? Destination, string? Pattern, bool? ConvertHeic, bool? IncludeHidden) GetOptions(string deviceId)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT destination, pattern, convert_heic, include_hidden FROM devices WHERE device_id = $d;", ("$d", deviceId));
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return default;
            }

            return (
                r.IsDBNull(0) ? null : r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2) != 0,
                r.IsDBNull(3) ? null : r.GetInt64(3) != 0);
        }
    }

    public void SetOptions(string deviceId, string? destination, string? pattern, bool? convertHeic, bool? includeHidden)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO devices (device_id, destination, pattern, convert_heic, include_hidden) VALUES ($d, $dest, $p, $c, $h)
                ON CONFLICT(device_id) DO UPDATE SET destination = excluded.destination, pattern = excluded.pattern,
                    convert_heic = excluded.convert_heic, include_hidden = excluded.include_hidden;
                """,
                ("$d", deviceId), ("$dest", destination), ("$p", pattern), ("$c", convertHeic), ("$h", includeHidden));
            cmd.ExecuteNonQuery();
        }
    }

    // ---------- what was backed up ----------

    public PhotoBackupKnownSet LoadKnown(string deviceId)
    {
        var known = new PhotoBackupKnownSet();
        lock (_gate)
        {
            using var cmd = Command("SELECT asset_key, source_path, file_name, size, taken_at FROM assets WHERE device_id = $d;", ("$d", deviceId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                known.Add(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4));
            }
        }

        return known;
    }

    /// <summary>The same test as <see cref="PhotoBackupKnownSet.Contains"/>, for one photo, straight from the database.</summary>
    public bool IsKnown(string deviceId, PhotoBackupAsset asset)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT 1 FROM assets WHERE device_id = $d AND (
                    asset_key = $k
                    OR ($path IS NOT NULL AND source_path = $path COLLATE NOCASE AND ($size = 0 OR size = 0 OR size = $size))
                    OR ($size > 0 AND $taken IS NOT NULL AND size = $size AND taken_at = $taken AND file_name = $name COLLATE NOCASE))
                LIMIT 1;
                """,
                ("$d", deviceId), ("$k", asset.Key), ("$path", asset.SourcePath), ("$size", asset.Size),
                ("$taken", asset.TakenAt?.ToUnixTimeSeconds()), ("$name", asset.FileName));
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void Record(string deviceId, PhotoBackupRecord record)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO assets (device_id, asset_key, source_path, file_name, size, taken_at, pc_path, motion_pc_path, duplicate, job_id, backed_up_at)
                VALUES ($d, $k, $path, $name, $size, $taken, $pc, $motion, $dup, $job, $at)
                ON CONFLICT(device_id, asset_key) DO UPDATE SET source_path = excluded.source_path, file_name = excluded.file_name,
                    size = excluded.size, taken_at = excluded.taken_at, pc_path = excluded.pc_path, motion_pc_path = excluded.motion_pc_path,
                    duplicate = excluded.duplicate, job_id = excluded.job_id, backed_up_at = excluded.backed_up_at;
                """,
                ("$d", deviceId), ("$k", record.Key), ("$path", record.SourcePath), ("$name", record.FileName), ("$size", record.Size),
                ("$taken", record.TakenAt?.ToUnixTimeSeconds()), ("$pc", record.PcPath), ("$motion", record.MotionPcPath),
                ("$dup", record.Duplicate), ("$job", record.JobId), ("$at", Text(record.BackedUpAt)));
            cmd.ExecuteNonQuery();
        }
    }

    public PhotoBackupRecord? Find(string deviceId, string key)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                SELECT asset_key, source_path, file_name, size, taken_at, pc_path, motion_pc_path, duplicate, job_id, backed_up_at
                FROM assets WHERE device_id = $d AND asset_key = $k;
                """, ("$d", deviceId), ("$k", key));
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new PhotoBackupRecord(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetString(2),
                    r.GetInt64(3),
                    r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)),
                    r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetInt64(7) != 0,
                    r.IsDBNull(8) ? null : r.GetString(8),
                    DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture))
                : null;
        }
    }

    public int CountBackedUp(string deviceId)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT COUNT(*) FROM assets WHERE device_id = $d;", ("$d", deviceId));
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Forgets what was backed up from the phone (the files on the PC stay). Returns how many photos it forgot.</summary>
    public int Forget(string deviceId)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM assets WHERE device_id = $d;", ("$d", deviceId));
            return cmd.ExecuteNonQuery();
        }
    }

    // ---------- runs ----------

    public void StartRun(string jobId, string deviceId, string destination, DateTimeOffset startedAt)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT OR IGNORE INTO runs (job_id, device_id, destination, state, started_at) VALUES ($j, $d, $dest, $s, $at);
                """,
                ("$j", jobId), ("$d", deviceId), ("$dest", destination), ("$s", PhotoBackupRunState.Running.ToString()), ("$at", Text(startedAt)));
            cmd.ExecuteNonQuery();
        }
    }

    public void SetPlanned(string jobId, int planned)
    {
        lock (_gate)
        {
            using var cmd = Command("UPDATE runs SET planned = $n WHERE job_id = $j;", ("$j", jobId), ("$n", planned));
            cmd.ExecuteNonQuery();
        }
    }

    public void AddToRun(string jobId, int copied, int duplicates, long bytes, int keptHeic)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                UPDATE runs SET copied = copied + $c, duplicates = duplicates + $dup, bytes = bytes + $b, kept_heic = kept_heic + $h
                WHERE job_id = $j;
                """, ("$j", jobId), ("$c", copied), ("$dup", duplicates), ("$b", bytes), ("$h", keptHeic));
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateRun(string jobId, PhotoBackupRunState state, int failed, DateTimeOffset? finishedAt)
    {
        lock (_gate)
        {
            using var cmd = Command("UPDATE runs SET state = $s, failed = $f, finished_at = $at WHERE job_id = $j;",
                ("$j", jobId), ("$s", state.ToString()), ("$f", failed), ("$at", finishedAt is { } at ? Text(at) : null));
            cmd.ExecuteNonQuery();
        }
    }

    public PhotoBackupRun? GetRun(string jobId) => ReadRun("WHERE job_id = $x", jobId);

    /// <summary>The phone's newest run.</summary>
    public PhotoBackupRun? GetLastRun(string deviceId) => ReadRun("WHERE device_id = $x ORDER BY started_at DESC, rowid DESC LIMIT 1", deviceId);

    private PhotoBackupRun? ReadRun(string where, string value)
    {
        lock (_gate)
        {
            using var cmd = Command($"""
                SELECT job_id, destination, state, planned, copied, duplicates, failed, bytes, kept_heic, started_at, finished_at FROM runs {where};
                """, ("$x", value));
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new PhotoBackupRun(
                    r.GetString(0),
                    r.GetString(1),
                    Enum.Parse<PhotoBackupRunState>(r.GetString(2)),
                    r.GetInt32(3),
                    r.GetInt32(4),
                    r.GetInt32(5),
                    r.GetInt32(6),
                    r.GetInt64(7),
                    r.GetInt32(8),
                    DateTimeOffset.Parse(r.GetString(9), CultureInfo.InvariantCulture),
                    r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture))
                : null;
        }
    }

    // ---------- PC file hashes ----------

    /// <summary>The cached SHA-256 of a PC file, when it still has the size and write time it had then.</summary>
    public byte[]? GetHash(string path, long size, long modifiedTicks)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT sha256 FROM pc_hashes WHERE path = $p AND size = $s AND modified = $m;",
                ("$p", path), ("$s", size), ("$m", modifiedTicks));
            return cmd.ExecuteScalar() as byte[];
        }
    }

    public void PutHash(string path, long size, long modifiedTicks, byte[] sha256)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO pc_hashes (path, size, modified, sha256) VALUES ($p, $s, $m, $h)
                ON CONFLICT(path) DO UPDATE SET size = excluded.size, modified = excluded.modified, sha256 = excluded.sha256;
                """, ("$p", path), ("$s", size), ("$m", modifiedTicks), ("$h", sha256));
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }

    private static string Text(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                bool b => b ? 1 : 0,
                _ => value,
            });
        }

        return cmd;
    }

    private void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
