using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DeviceDeck.Android.Apk;

internal sealed record ZipEntryInfo(string Name, int Method, long CompressedSize, long UncompressedSize, long LocalHeaderOffset);

/// <summary>
/// Reads single entries out of a ZIP (an APK) through an <see cref="IRangeReader"/>: the end record, the central
/// directory, then just the bytes of the entries asked for. Handles stored and deflated entries and ZIP64 sizes.
/// </summary>
internal sealed class ZipReader
{
    private const uint EndSignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EndSignature = 0x06064b50;
    private const uint CentralSignature = 0x02014b50;
    private const uint LocalSignature = 0x04034b50;
    private const int MaxEntryBytes = 64 * 1024 * 1024;

    private readonly IRangeReader _reader;
    private readonly Dictionary<string, ZipEntryInfo> _entries;

    private ZipReader(IRangeReader reader, Dictionary<string, ZipEntryInfo> entries)
    {
        _reader = reader;
        _entries = entries;
    }

    public IReadOnlyCollection<ZipEntryInfo> Entries => _entries.Values;

    public bool Contains(string name) => _entries.ContainsKey(name);

    public static async Task<ZipReader> OpenAsync(IRangeReader reader, CancellationToken ct)
    {
        // The end record is 22 bytes plus a comment of up to 64 KiB.
        int tailLength = (int)Math.Min(reader.Length, 22 + 0xFFFF + 20);
        long tailStart = reader.Length - tailLength;
        byte[] tail = await reader.ReadAsync(tailStart, tailLength, ct).ConfigureAwait(false);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
        {
            if (U32(tail, i) == EndSignature)
            {
                end = i;
                break;
            }
        }
        if (end < 0)
        {
            throw new InvalidDataException("Not a ZIP file (no end-of-central-directory record).");
        }

        long count = U16(tail, end + 10);
        long size = U32(tail, end + 12);
        long offset = U32(tail, end + 16);
        if ((count == 0xFFFF || size == 0xFFFFFFFF || offset == 0xFFFFFFFF) && end >= 20 && U32(tail, end - 20) == Zip64LocatorSignature)
        {
            long zip64End = (long)U64(tail, end - 20 + 8);
            byte[] record = await reader.ReadAsync(zip64End, 56, ct).ConfigureAwait(false);
            if (U32(record, 0) == Zip64EndSignature)
            {
                count = (long)U64(record, 32);
                size = (long)U64(record, 40);
                offset = (long)U64(record, 48);
            }
        }
        if (offset < 0 || size < 0 || offset + size > reader.Length || size > MaxEntryBytes)
        {
            throw new InvalidDataException("The ZIP central directory is out of range.");
        }

        byte[] directory = await reader.ReadAsync(offset, (int)size, ct).ConfigureAwait(false);
        var entries = new Dictionary<string, ZipEntryInfo>(StringComparer.Ordinal);
        int position = 0;
        for (long i = 0; i < count && position + 46 <= directory.Length; i++)
        {
            if (U32(directory, position) != CentralSignature)
            {
                break;
            }
            int method = U16(directory, position + 10);
            long compressed = U32(directory, position + 20);
            long uncompressed = U32(directory, position + 24);
            int nameLength = U16(directory, position + 28);
            int extraLength = U16(directory, position + 30);
            int commentLength = U16(directory, position + 32);
            long local = U32(directory, position + 42);
            string name = Encoding.UTF8.GetString(directory, position + 46, nameLength);

            // ZIP64 extra field: the values that were 0xFFFFFFFF, in this order.
            int extra = position + 46 + nameLength;
            int extraEnd = extra + extraLength;
            while (extra + 4 <= extraEnd)
            {
                int id = U16(directory, extra);
                int length = U16(directory, extra + 2);
                if (id == 0x0001)
                {
                    int field = extra + 4;
                    if (uncompressed == 0xFFFFFFFF) { uncompressed = (long)U64(directory, field); field += 8; }
                    if (compressed == 0xFFFFFFFF) { compressed = (long)U64(directory, field); field += 8; }
                    if (local == 0xFFFFFFFF) { local = (long)U64(directory, field); }
                }
                extra += 4 + length;
            }

            entries.TryAdd(name, new ZipEntryInfo(name, method, compressed, uncompressed, local));
            position += 46 + nameLength + extraLength + commentLength;
        }
        return new ZipReader(reader, entries);
    }

    /// <summary>The entry's uncompressed bytes, or null when it isn't in the archive.</summary>
    public async Task<byte[]?> ReadAsync(string name, CancellationToken ct)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return null;
        }
        if (entry.CompressedSize > MaxEntryBytes || entry.UncompressedSize > MaxEntryBytes)
        {
            throw new InvalidDataException($"{name} is too large to read ({entry.UncompressedSize} bytes).");
        }

        // Local header: 30 bytes + name + extra (zipalign pads the local extra, so it can differ from the central one).
        // Read a little more than the header so most entries come in one round trip.
        int guess = (int)Math.Min(_reader.Length - entry.LocalHeaderOffset, 30 + 1024 + entry.CompressedSize);
        byte[] chunk = await _reader.ReadAsync(entry.LocalHeaderOffset, guess, ct).ConfigureAwait(false);
        if (chunk.Length < 30 || U32(chunk, 0) != LocalSignature)
        {
            throw new InvalidDataException($"Bad local header for {name}.");
        }
        int dataStart = 30 + U16(chunk, 26) + U16(chunk, 28);
        byte[] compressed;
        if (dataStart + entry.CompressedSize <= chunk.Length)
        {
            compressed = chunk.AsSpan(dataStart, (int)entry.CompressedSize).ToArray();
        }
        else
        {
            compressed = await _reader.ReadAsync(entry.LocalHeaderOffset + dataStart, (int)entry.CompressedSize, ct).ConfigureAwait(false);
        }

        switch (entry.Method)
        {
            case 0:
                return compressed;
            case 8:
                using (var input = new DeflateStream(new MemoryStream(compressed), CompressionMode.Decompress))
                using (var output = new MemoryStream((int)Math.Max(0, entry.UncompressedSize)))
                {
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    return output.ToArray();
                }
            default:
                throw new InvalidDataException($"{name} uses ZIP method {entry.Method}, which isn't supported.");
        }
    }

    private static ushort U16(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));

    private static uint U32(byte[] b, int i) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i));

    private static ulong U64(byte[] b, int i) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(i));
}
