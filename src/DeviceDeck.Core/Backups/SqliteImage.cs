using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace DeviceDeck.Core.Backups;

/// <summary>
/// A read-only SQLite database from a backup: opened in place when it is stored as is, or from decrypted bytes held
/// in memory, so nothing decrypted is written to disk.
/// </summary>
public sealed class SqliteImage : IDisposable
{
    private const int DeserializeReadOnly = 4;

    /// <summary>SQLite opens files with Windows' MAX_PATH limit (260 with the terminator); stay clear of it.</summary>
    private const int MaxSqlitePath = 240;
    private GCHandle _pin;

    private SqliteImage(SqliteConnection connection, GCHandle pin)
    {
        Connection = connection;
        _pin = pin;
    }

    public SqliteConnection Connection { get; }

    /// <summary>
    /// Opens a file read-only and immutable: SQLite neither writes nor locks it, and makes no journal next to it.
    /// A path too long for SQLite's own file calls (MAX_PATH) is read into memory instead.
    /// </summary>
    public static SqliteImage OpenFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length >= MaxSqlitePath)
        {
            return FromBytes(File.ReadAllBytes(full));
        }

        var uri = new Uri(full).AbsoluteUri + "?immutable=1";
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            return new SqliteImage(connection, default);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Opens a database image held in memory; the bytes stay pinned until disposal.</summary>
    public static SqliteImage FromBytes(byte[] image)
    {
        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        var pin = GCHandle.Alloc(image, GCHandleType.Pinned);
        try
        {
            connection.Open();
            var rc = raw.sqlite3_deserialize(connection.Handle, "main", pin.AddrOfPinnedObject(), image.Length, image.Length, DeserializeReadOnly);
            if (rc != raw.SQLITE_OK)
            {
                throw new SqliteException($"SQLite could not read the database image (error {rc}).", rc);
            }

            return new SqliteImage(connection, pin);
        }
        catch
        {
            connection.Dispose();
            pin.Free();
            throw;
        }
    }

    public void Dispose()
    {
        Connection.Dispose();
        if (_pin.IsAllocated)
        {
            _pin.Free();
        }
    }
}
