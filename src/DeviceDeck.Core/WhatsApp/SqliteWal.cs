using System.Buffers.Binary;

namespace DeviceDeck.Core.WhatsApp;

/// <summary>
/// Folds a SQLite write-ahead log (the "-wal" file a backup may hold next to a database) into a copy of the database
/// in memory, the way SQLite itself would on a checkpoint: committed frames only, each checked by its salts and running
/// checksum (sqlite.org/fileformat2.html, "The WAL File Format"). Nothing is written to disk.
/// </summary>
public static class SqliteWal
{
    private const int HeaderSize = 32;
    private const int FrameHeaderSize = 24;

    /// <summary>
    /// The database with the log's committed pages applied, or <paramref name="database"/> itself when the log is empty,
    /// damaged or for another page size. Frames after the last good commit are left out, as SQLite leaves them.
    /// </summary>
    public static byte[] Apply(byte[] database, ReadOnlySpan<byte> wal)
    {
        if (wal.Length < HeaderSize + FrameHeaderSize)
        {
            return database;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(wal);
        if (magic is not (0x377F0682 or 0x377F0683))
        {
            return database;
        }

        var bigEndian = magic == 0x377F0683;
        var pageSize = (int)BinaryPrimitives.ReadUInt32BigEndian(wal[8..]);
        if (pageSize is < 512 or > 65536 || (pageSize & (pageSize - 1)) != 0 || (database.Length >= 100 && DatabasePageSize(database) != pageSize))
        {
            return database;
        }

        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal[16..]);
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal[20..]);
        uint s0 = 0, s1 = 0;
        Checksum(wal[..24], bigEndian, ref s0, ref s1);
        if (s0 != BinaryPrimitives.ReadUInt32BigEndian(wal[24..]) || s1 != BinaryPrimitives.ReadUInt32BigEndian(wal[28..]))
        {
            return database;
        }

        var committed = new List<(uint Page, int Offset)>();
        var pending = new List<(uint Page, int Offset)>();
        uint pages = 0;
        for (var at = HeaderSize; at + FrameHeaderSize + pageSize <= wal.Length; at += FrameHeaderSize + pageSize)
        {
            var frame = wal.Slice(at, FrameHeaderSize);
            if (BinaryPrimitives.ReadUInt32BigEndian(frame[8..]) != salt1 || BinaryPrimitives.ReadUInt32BigEndian(frame[12..]) != salt2)
            {
                break;
            }

            Checksum(frame[..8], bigEndian, ref s0, ref s1);
            Checksum(wal.Slice(at + FrameHeaderSize, pageSize), bigEndian, ref s0, ref s1);
            if (s0 != BinaryPrimitives.ReadUInt32BigEndian(frame[16..]) || s1 != BinaryPrimitives.ReadUInt32BigEndian(frame[20..]))
            {
                break;
            }

            var page = BinaryPrimitives.ReadUInt32BigEndian(frame);
            if (page == 0)
            {
                break;
            }

            pending.Add((page, at + FrameHeaderSize));
            var commit = BinaryPrimitives.ReadUInt32BigEndian(frame[4..]);
            if (commit != 0)
            {
                committed.AddRange(pending);
                pending.Clear();
                pages = commit;
            }
        }

        var size = (long)pages * pageSize;
        if (committed.Count == 0 || size > Array.MaxLength)
        {
            return database;
        }

        var result = new byte[size];
        database.AsSpan(0, (int)Math.Min(database.Length, size)).CopyTo(result);
        foreach (var (page, offset) in committed)
        {
            var target = (long)(page - 1) * pageSize;
            if (target + pageSize <= size)
            {
                wal.Slice(offset, pageSize).CopyTo(result.AsSpan((int)target, pageSize));
            }
        }
        return result;
    }

    /// <summary>The page size in a database header (offset 16, big-endian; 1 means 65536).</summary>
    private static int DatabasePageSize(byte[] database)
    {
        int size = BinaryPrimitives.ReadUInt16BigEndian(database.AsSpan(16));
        return size == 1 ? 65536 : size;
    }

    /// <summary>SQLite's WAL checksum: pairs of 32-bit words in the log's byte order, carried from frame to frame.</summary>
    private static void Checksum(ReadOnlySpan<byte> data, bool bigEndian, ref uint s0, ref uint s1)
    {
        unchecked
        {
            for (var i = 0; i + 8 <= data.Length; i += 8)
            {
                var x0 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                var x1 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
                s0 += x0 + s1;
                s1 += x1 + s0;
            }
        }
    }
}
