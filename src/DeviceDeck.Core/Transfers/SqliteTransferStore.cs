using System.Globalization;
using System.Text.Json;
using DeviceDeck.Core.Serialization;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.Transfers;

/// <summary>The transfer queue in SQLite (transfers.db), so jobs survive a restart.</summary>
public sealed class SqliteTransferStore : ITransferStore
{
    private const int SchemaVersion = 1;

    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public SqliteTransferStore(string path)
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
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private void Migrate()
    {
        using var check = _db.CreateCommand();
        check.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
        {
            return;
        }

        using var tx = _db.BeginTransaction();
        Execute("""
            CREATE TABLE IF NOT EXISTS jobs (
                id            TEXT PRIMARY KEY,
                seq           INTEGER NOT NULL,
                device_id     TEXT NOT NULL,
                kind          TEXT NOT NULL,
                title         TEXT NOT NULL,
                state         TEXT NOT NULL,
                request       TEXT NOT NULL,
                rule          TEXT NOT NULL,
                planned       INTEGER NOT NULL,
                conflict      TEXT NULL,
                conflict_item INTEGER NULL,
                error         TEXT NULL,
                created_at    TEXT NOT NULL,
                finished_at   TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS items (
                job_id        TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
                idx           INTEGER NOT NULL,
                source        TEXT NOT NULL,
                destination   TEXT NOT NULL,
                name          TEXT NOT NULL,
                size          INTEGER NOT NULL,
                state         TEXT NOT NULL,
                error_code    TEXT NULL,
                error_message TEXT NULL,
                decision      TEXT NULL,
                PRIMARY KEY (job_id, idx)
            );
            """, tx);
        Execute($"PRAGMA user_version = {SchemaVersion};", tx);
        tx.Commit();
    }

    public IReadOnlyList<StoredJob> LoadAll()
    {
        lock (_gate)
        {
            var jobs = new Dictionary<string, StoredJob>(StringComparer.Ordinal);
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT id, seq, device_id, kind, title, state, request, rule, planned, conflict, conflict_item, error, created_at, finished_at FROM jobs ORDER BY seq;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var job = new StoredJob
                    {
                        Id = r.GetString(0),
                        Seq = r.GetInt64(1),
                        DeviceId = r.GetString(2),
                        Kind = Enum.Parse<TransferKind>(r.GetString(3)),
                        Title = r.GetString(4),
                        State = Enum.Parse<TransferState>(r.GetString(5)),
                        RequestJson = r.GetString(6),
                        Rule = Enum.Parse<ConflictRule>(r.GetString(7)),
                        Planned = r.GetInt64(8) != 0,
                        Conflict = r.IsDBNull(9) ? null : JsonSerializer.Deserialize<TransferConflict>(r.GetString(9), DeviceDeckJson.Options),
                        ConflictItem = r.IsDBNull(10) ? null : r.GetInt32(10),
                        Error = r.IsDBNull(11) ? null : JsonSerializer.Deserialize<TransferError>(r.GetString(11), DeviceDeckJson.Options),
                        CreatedAt = DateTimeOffset.Parse(r.GetString(12), CultureInfo.InvariantCulture),
                        FinishedAt = r.IsDBNull(13) ? null : DateTimeOffset.Parse(r.GetString(13), CultureInfo.InvariantCulture),
                    };
                    jobs[job.Id] = job;
                }
            }

            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = "SELECT job_id, idx, source, destination, name, size, state, error_code, error_message, decision FROM items ORDER BY job_id, idx;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!jobs.TryGetValue(r.GetString(0), out var job))
                    {
                        continue;
                    }

                    job.Items.Add(new TransferItem
                    {
                        Index = r.GetInt32(1),
                        Source = r.GetString(2),
                        Destination = r.GetString(3),
                        Name = r.GetString(4),
                        Size = r.GetInt64(5),
                        State = Enum.Parse<TransferItemState>(r.GetString(6)),
                        ErrorCode = r.IsDBNull(7) ? null : r.GetString(7),
                        ErrorMessage = r.IsDBNull(8) ? null : r.GetString(8),
                        Decision = r.IsDBNull(9) ? null : Enum.Parse<ConflictChoice>(r.GetString(9)),
                    });
                }
            }

            return jobs.Values.OrderBy(j => j.Seq).ToArray();
        }
    }

    public void UpsertJob(StoredJob job)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO jobs (id, seq, device_id, kind, title, state, request, rule, planned, conflict, conflict_item, error, created_at, finished_at)
                VALUES ($id, $seq, $device, $kind, $title, $state, $request, $rule, $planned, $conflict, $conflictItem, $error, $created, $finished)
                ON CONFLICT(id) DO UPDATE SET
                    state = excluded.state, rule = excluded.rule, planned = excluded.planned, conflict = excluded.conflict,
                    conflict_item = excluded.conflict_item, error = excluded.error, finished_at = excluded.finished_at;
                """;
            cmd.Parameters.AddWithValue("$id", job.Id);
            cmd.Parameters.AddWithValue("$seq", job.Seq);
            cmd.Parameters.AddWithValue("$device", job.DeviceId);
            cmd.Parameters.AddWithValue("$kind", job.Kind.ToString());
            cmd.Parameters.AddWithValue("$title", job.Title);
            cmd.Parameters.AddWithValue("$state", job.State.ToString());
            cmd.Parameters.AddWithValue("$request", job.RequestJson);
            cmd.Parameters.AddWithValue("$rule", job.Rule.ToString());
            cmd.Parameters.AddWithValue("$planned", job.Planned ? 1 : 0);
            cmd.Parameters.AddWithValue("$conflict", job.Conflict is null ? DBNull.Value : JsonSerializer.Serialize(job.Conflict, DeviceDeckJson.Options));
            cmd.Parameters.AddWithValue("$conflictItem", job.ConflictItem is { } index ? index : DBNull.Value);
            cmd.Parameters.AddWithValue("$error", job.Error is null ? DBNull.Value : JsonSerializer.Serialize(job.Error, DeviceDeckJson.Options));
            cmd.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$finished", job.FinishedAt is { } finished ? finished.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void ReplaceItems(string jobId, IReadOnlyList<TransferItem> items)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var delete = _db.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM items WHERE job_id = $job;";
                delete.Parameters.AddWithValue("$job", jobId);
                delete.ExecuteNonQuery();
            }

            using (var insert = _db.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO items (job_id, idx, source, destination, name, size, state, error_code, error_message, decision)
                    VALUES ($job, $idx, $source, $destination, $name, $size, $state, $errorCode, $errorMessage, $decision);
                    """;
                var p = BindItem(insert, jobId);
                foreach (var item in items)
                {
                    SetItem(p, item);
                    insert.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
    }

    public void UpdateItems(string jobId, IEnumerable<TransferItem> items)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var update = _db.CreateCommand();
            update.Transaction = tx;
            update.CommandText = """
                UPDATE items SET source = $source, destination = $destination, name = $name, size = $size, state = $state,
                    error_code = $errorCode, error_message = $errorMessage, decision = $decision
                WHERE job_id = $job AND idx = $idx;
                """;
            var p = BindItem(update, jobId);
            foreach (var item in items)
            {
                SetItem(p, item);
                update.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void DeleteJob(string jobId)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM items WHERE job_id = $job; DELETE FROM jobs WHERE id = $job;";
            cmd.Parameters.AddWithValue("$job", jobId);
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

    private static ItemParameters BindItem(SqliteCommand cmd, string jobId)
    {
        cmd.Parameters.AddWithValue("$job", jobId);
        return new ItemParameters(
            cmd.Parameters.Add("$idx", SqliteType.Integer),
            cmd.Parameters.Add("$source", SqliteType.Text),
            cmd.Parameters.Add("$destination", SqliteType.Text),
            cmd.Parameters.Add("$name", SqliteType.Text),
            cmd.Parameters.Add("$size", SqliteType.Integer),
            cmd.Parameters.Add("$state", SqliteType.Text),
            cmd.Parameters.Add("$errorCode", SqliteType.Text),
            cmd.Parameters.Add("$errorMessage", SqliteType.Text),
            cmd.Parameters.Add("$decision", SqliteType.Text));
    }

    private static void SetItem(ItemParameters p, TransferItem item)
    {
        p.Index.Value = item.Index;
        p.Source.Value = item.Source;
        p.Destination.Value = item.Destination;
        p.Name.Value = item.Name;
        p.Size.Value = item.Size;
        p.State.Value = item.State.ToString();
        p.ErrorCode.Value = (object?)item.ErrorCode ?? DBNull.Value;
        p.ErrorMessage.Value = (object?)item.ErrorMessage ?? DBNull.Value;
        p.Decision.Value = item.Decision is { } d ? d.ToString() : DBNull.Value;
    }

    private void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed record ItemParameters(
        SqliteParameter Index,
        SqliteParameter Source,
        SqliteParameter Destination,
        SqliteParameter Name,
        SqliteParameter Size,
        SqliteParameter State,
        SqliteParameter ErrorCode,
        SqliteParameter ErrorMessage,
        SqliteParameter Decision);
}
