using System.Buffers.Binary;
using System.Text;

namespace DeviceDeck.Ios.Plist;

/// <summary>
/// Reads the "bplist00" format: header, objects, offset table, 32-byte trailer.
/// Every offset and count is bounds-checked; cycles and runaway nesting are rejected.
/// </summary>
internal sealed class BinaryPlistReader
{
    private static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int MaxDepth = 64;
    // Shared references can expand a small file into a huge tree; cap what we build.
    private const int MaxObjectsBuilt = 2_000_000;

    private readonly byte[] _data;
    private readonly long[] _offsets;
    private readonly int _refSize;
    private readonly HashSet<long> _inProgress = [];
    private int _built;

    private BinaryPlistReader(byte[] data, long[] offsets, int refSize)
    {
        _data = data;
        _offsets = offsets;
        _refSize = refSize;
    }

    public static object Read(ReadOnlySpan<byte> span)
    {
        if (span.Length < 8 + 32)
            throw new FormatException("Binary plist is too short.");
        var data = span.ToArray();
        var trailer = data.AsSpan(data.Length - 32);
        int offsetIntSize = trailer[6];
        int refSize = trailer[7];
        var numObjects = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
        var topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
        var offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);

        if (offsetIntSize is < 1 or > 8 || refSize is < 1 or > 8)
            throw new FormatException("Binary plist trailer has bad integer sizes.");
        var tableEnd = data.Length - 32;
        if (numObjects == 0 || numObjects > (ulong)tableEnd || topObject >= numObjects)
            throw new FormatException("Binary plist trailer has a bad object count.");
        if (offsetTableOffset < 8 || offsetTableOffset + numObjects * (ulong)offsetIntSize > (ulong)tableEnd)
            throw new FormatException("Binary plist offset table is out of range.");

        var offsets = new long[numObjects];
        for (ulong i = 0; i < numObjects; i++)
        {
            var at = (int)(offsetTableOffset + i * (ulong)offsetIntSize);
            var off = ReadUnsigned(data.AsSpan(at, offsetIntSize));
            if (off < 8 || off >= offsetTableOffset)
                throw new FormatException("Binary plist object offset is out of range.");
            offsets[i] = (long)off;
        }

        var reader = new BinaryPlistReader(data, offsets, refSize);
        return reader.ReadObject((long)topObject, 0);
    }

    private object ReadObject(long index, int depth)
    {
        if (index < 0 || index >= _offsets.Length)
            throw new FormatException("Binary plist object reference is out of range.");
        if (depth > MaxDepth)
            throw new FormatException("Binary plist nests too deeply.");
        if (++_built > MaxObjectsBuilt)
            throw new FormatException("Binary plist expands to too many objects.");

        var pos = _offsets[index];
        var marker = Byte(pos);
        var type = marker >> 4;
        var info = marker & 0x0F;
        pos++;

        switch (type)
        {
            case 0x0:
                return info switch
                {
                    0x8 => false,
                    0x9 => true,
                    _ => throw new FormatException($"Unsupported binary plist simple value 0x{marker:X2}."),
                };
            case 0x1:
                return ReadInteger(pos, info);
            case 0x2:
            {
                var len = 1 << info;
                if (len == 4)
                    return (double)BinaryPrimitives.ReadSingleBigEndian(Slice(pos, 4));
                if (len == 8)
                    return BinaryPrimitives.ReadDoubleBigEndian(Slice(pos, 8));
                throw new FormatException("Unsupported binary plist real size.");
            }
            case 0x3:
                if (marker != 0x33)
                    throw new FormatException("Bad binary plist date marker.");
                return Epoch2001.AddSeconds(BinaryPrimitives.ReadDoubleBigEndian(Slice(pos, 8)));
            case 0x4:
            {
                var count = ReadCount(ref pos, info);
                return Slice(pos, count).ToArray();
            }
            case 0x5:
            {
                var count = ReadCount(ref pos, info);
                return Encoding.ASCII.GetString(Slice(pos, count));
            }
            case 0x6:
            {
                var count = ReadCount(ref pos, info);
                return Encoding.BigEndianUnicode.GetString(Slice(pos, checked(count * 2)));
            }
            case 0x7:
            {
                var count = ReadCount(ref pos, info);
                return Encoding.UTF8.GetString(Slice(pos, count));
            }
            case 0x8:
                return new PlistUid(ReadUnsigned(Slice(pos, info + 1)));
            case 0xA:
            case 0xB:
            case 0xC:
            {
                var count = ReadCount(ref pos, info);
                Enter(index);
                var list = new List<object>(Math.Min(count, 4096));
                for (var i = 0; i < count; i++)
                    list.Add(ReadObject(ReadRef(pos + (long)i * _refSize), depth + 1));
                Leave(index);
                return list;
            }
            case 0xD:
            {
                var count = ReadCount(ref pos, info);
                Enter(index);
                var dict = new PlistDict();
                var valuesAt = pos + (long)count * _refSize;
                for (var i = 0; i < count; i++)
                {
                    var key = ReadObject(ReadRef(pos + (long)i * _refSize), depth + 1) as string
                              ?? throw new FormatException("Binary plist dictionary key is not a string.");
                    dict[key] = ReadObject(ReadRef(valuesAt + (long)i * _refSize), depth + 1);
                }
                Leave(index);
                return dict;
            }
            default:
                throw new FormatException($"Unknown binary plist marker 0x{marker:X2}.");
        }
    }

    private object ReadInteger(long pos, int info)
    {
        switch (info)
        {
            case 0: return (long)Byte(pos);
            case 1: return (long)BinaryPrimitives.ReadUInt16BigEndian(Slice(pos, 2));
            case 2: return (long)BinaryPrimitives.ReadUInt32BigEndian(Slice(pos, 4));
            case 3: return BinaryPrimitives.ReadInt64BigEndian(Slice(pos, 8));
            case 4:
            {
                // 128-bit: CoreFoundation uses it for unsigned 64-bit values above long.MaxValue.
                var span = Slice(pos, 16);
                var high = BinaryPrimitives.ReadUInt64BigEndian(span);
                var low = BinaryPrimitives.ReadUInt64BigEndian(span[8..]);
                if (high != 0)
                    throw new FormatException("Binary plist integer does not fit in 64 bits.");
                return low <= long.MaxValue ? (long)low : low;
            }
            default:
                throw new FormatException("Unsupported binary plist integer size.");
        }
    }

    private int ReadCount(ref long pos, int info)
    {
        if (info != 0xF)
            return info;
        var marker = Byte(pos);
        if (marker >> 4 != 0x1)
            throw new FormatException("Binary plist length is not an integer.");
        var size = 1 << (marker & 0xF);
        var value = ReadUnsigned(Slice(pos + 1, size));
        pos += 1 + size;
        if (value > int.MaxValue)
            throw new FormatException("Binary plist length is too large.");
        return (int)value;
    }

    private long ReadRef(long pos) => (long)ReadUnsigned(Slice(pos, _refSize));

    private void Enter(long index)
    {
        if (!_inProgress.Add(index))
            throw new FormatException("Binary plist contains a reference cycle.");
    }

    private void Leave(long index) => _inProgress.Remove(index);

    private byte Byte(long pos)
    {
        if (pos < 0 || pos >= _data.Length - 32)
            throw new FormatException("Binary plist read past the object area.");
        return _data[pos];
    }

    private ReadOnlySpan<byte> Slice(long pos, int length)
    {
        if (length < 0 || pos < 0 || pos + length > _data.Length - 32)
            throw new FormatException("Binary plist read past the object area.");
        return _data.AsSpan((int)pos, length);
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 8)
            throw new FormatException("Binary plist integer is wider than 64 bits.");
        ulong v = 0;
        foreach (var b in bytes)
            v = (v << 8) | b;
        return v;
    }
}
