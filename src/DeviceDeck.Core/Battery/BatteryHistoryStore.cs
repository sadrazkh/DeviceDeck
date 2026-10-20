using System.Globalization;
using DeviceDeck.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.Battery;

/// <summary>Battery snapshots in SQLite (battery.db next to transfers.db), oldest first per phone.</summary>
public sealed class BatteryHistoryStore : IDisposable
{
    public const string FileName = "battery.db";
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();
    private bool _disposed;

    public BatteryHistoryStore(string path)
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

    /// <summary>Where the history lives for an app folder.</summary>
    public static string PathIn(AppPaths paths) => System.IO.Path.Combine(paths.Root, FileName);

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
            CREATE TABLE IF NOT EXISTS snapshots (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id        TEXT NOT NULL,
                device_name      TEXT NOT NULL,
                platform         TEXT NOT NULL,
                model            TEXT NOT NULL,
                os_version       TEXT NOT NULL,
                at_ms            INTEGER NOT NULL,
                at               TEXT NOT NULL,
                level            INTEGER NULL,
                charging         INTEGER NOT NULL,
                health           INTEGER NULL,
                health_estimated INTEGER NOT NULL,
                cycles           INTEGER NULL,
                design_mah       INTEGER NULL,
                full_mah         INTEGER NULL,
                temperature_c    REAL NULL
            );
            CREATE INDEX IF NOT EXISTS snapshots_by_device ON snapshots (device_id, at_ms);
            """, tx);
        Execute($"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    /// <summary>Stores a snapshot and returns it with its id.</summary>
    public BatterySnapshot Add(BatterySnapshot s)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO snapshots (device_id, device_name, platform, model, os_version, at_ms, at, level, charging, health, health_estimated, cycles, design_mah, full_mah, temperature_c)
                VALUES ($device, $name, $platform, $model, $os, $atMs, $at, $level, $charging, $health, $estimated, $cycles, $design, $full, $temp);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$device", s.DeviceId);
            cmd.Parameters.AddWithValue("$name", s.DeviceName);
            cmd.Parameters.AddWithValue("$platform", s.Platform.ToString());
            cmd.Parameters.AddWithValue("$model", s.Model);
            cmd.Parameters.AddWithValue("$os", s.OsVersion);
            cmd.Parameters.AddWithValue("$atMs", s.At.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$at", s.At.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$level", (object?)s.LevelPercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$charging", s.IsCharging ? 1 : 0);
            cmd.Parameters.AddWithValue("$health", (object?)s.HealthPercent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$estimated", s.HealthEstimated ? 1 : 0);
            cmd.Parameters.AddWithValue("$cycles", (object?)s.CycleCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$design", (object?)s.DesignCapacityMah ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$full", (object?)s.FullChargeCapacityMah ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$temp", (object?)s.TemperatureCelsius ?? DBNull.Value);
            var id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            return s with { Id = id };
        }
    }

    /// <summary>Every snapshot of one phone, oldest first.</summary>
    public IReadOnlyList<BatterySnapshot> List(string deviceId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM snapshots WHERE device_id = $device ORDER BY at_ms, id;";
            cmd.Parameters.AddWithValue("$device", deviceId);
            return Read(cmd);
        }
    }

    public BatterySnapshot? Latest(string deviceId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM snapshots WHERE device_id = $device ORDER BY at_ms DESC, id DESC LIMIT 1;";
            cmd.Parameters.AddWithValue("$device", deviceId);
            return Read(cmd).FirstOrDefault();
        }
    }

    /// <summary>Every phone with history, most recently seen first.</summary>
    public IReadOnlyList<BatteryDevice> Devices()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stats = new List<(string Id, int Count, long First, long Last)>();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT device_id, COUNT(*), MIN(at_ms), MAX(at_ms) FROM snapshots GROUP BY device_id ORDER BY MAX(at_ms) DESC;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    stats.Add((r.GetString(0), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3)));
                }
            }

            var devices = new List<BatteryDevice>(stats.Count);
            foreach (var (id, count, first, last) in stats)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = $"SELECT {Columns} FROM snapshots WHERE device_id = $device ORDER BY at_ms DESC, id DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$device", id);
                var latest = Read(cmd)[0];
                devices.Add(new BatteryDevice(id, latest.DeviceName, latest.Platform, latest.Model, count,
                    DateTimeOffset.FromUnixTimeMilliseconds(first), DateTimeOffset.FromUnixTimeMilliseconds(last), latest));
            }

            return devices;
        }
    }

    private const string Columns =
        "id, device_id, device_name, platform, model, os_version, at, level, charging, health, health_estimated, cycles, design_mah, full_mah, temperature_c";

    private static List<BatterySnapshot> Read(SqliteCommand cmd)
    {
        var list = new List<BatterySnapshot>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new BatterySnapshot(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                Enum.TryParse<DevicePlatform>(r.GetString(3), out var platform) ? platform : DevicePlatform.Android,
                r.GetString(4),
                r.GetString(5),
                DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture),
                r.IsDBNull(7) ? null : r.GetInt32(7),
                r.GetInt64(8) != 0,
                r.IsDBNull(9) ? null : r.GetInt32(9),
                r.GetInt64(10) != 0,
                r.IsDBNull(11) ? null : r.GetInt32(11),
                r.IsDBNull(12) ? null : r.GetInt32(12),
                r.IsDBNull(13) ? null : r.GetInt32(13),
                r.IsDBNull(14) ? null : r.GetDouble(14)));
        }

        return list;
    }

    private void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _db.Dispose();
        }
    }
}
