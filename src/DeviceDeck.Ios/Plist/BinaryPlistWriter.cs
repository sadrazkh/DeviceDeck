using System.Buffers.Binary;
using System.Text;

namespace DeviceDeck.Ios.Plist;

/// <summary>Writes "bplist00". Objects are not de-duplicated; that keeps it simple and is still valid.</summary>
internal sealed class BinaryPlistWriter
{
    private static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly List<object> _objects = [];

    public static byte[] Write(object root)
    {
        var writer = new BinaryPlistWriter();
        writer.Flatten(root, 0);
        return writer.Serialize();
    }

    // Assigns every object an index, parents before children, so the top object is 0.
    private int Flatten(object value, int depth)
    {
        if (depth > 64)
            throw new InvalidOperationException("Property list nests too deeply.");
        var index = _objects.Count;
        switch (value)
        {
            case IDictionary<string, object> dict:
            {
                var node = new DictNode(new int[dict.Count], new int[dict.Count]);
                _objects.Add(node);
                var i = 0;
                foreach (var (k, v) in dict)
                {
                    node.Keys[i] = Flatten(k, depth + 1);
                    node.Values[i] = Flatten(v, depth + 1);
                    i++;
                }
                break;
            }
            case string or byte[]:
                _objects.Add(value);
                break;
            case System.Collections.IEnumerable list:
            {
                var items = list.Cast<object>().ToList();
                var node = new ArrayNode(new int[items.Count]);
                _objects.Add(node);
                for (var i = 0; i < items.Count; i++)
                    node.Items[i] = Flatten(items[i] ?? throw new InvalidOperationException("Property lists cannot hold null."), depth + 1);
                break;
            }
            default:
                _objects.Add(value);
                break;
        }
        return index;
    }

    private byte[] Serialize()
    {
        var refSize = SizeFor((ulong)_objects.Count);
        using var body = new MemoryStream();
        body.Write("bplist00"u8);
        var offsets = new long[_objects.Count];
        for (var i = 0; i < _objects.Count; i++)
        {
            offsets[i] = body.Position;
            WriteObject(body, _objects[i], refSize);
        }

        var offsetTableOffset = body.Position;
        var offsetSize = SizeFor((ulong)offsetTableOffset);
        foreach (var off in offsets)
            WriteUnsigned(body, (ulong)off, offsetSize);

        Span<byte> trailer = stackalloc byte[32];
        trailer.Clear();
        trailer[6] = (byte)offsetSize;
        trailer[7] = (byte)refSize;
        BinaryPrimitives.WriteUInt64BigEndian(trailer[8..], (ulong)_objects.Count);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[16..], 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[24..], (ulong)offsetTableOffset);
        body.Write(trailer);
        return body.ToArray();
    }

    private static void WriteObject(Stream s, object value, int refSize)
    {
        switch (value)
        {
            case bool b:
                s.WriteByte(b ? (byte)0x09 : (byte)0x08);
                break;
            case long or int or short or byte or sbyte or ushort or uint:
                WriteInteger(s, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case ulong u when u <= long.MaxValue:
                WriteInteger(s, (long)u);
                break;
            case ulong u:
            {
                s.WriteByte(0x14);
                Span<byte> buf = stackalloc byte[16];
                buf.Clear();
                BinaryPrimitives.WriteUInt64BigEndian(buf[8..], u);
                s.Write(buf);
                break;
            }
            case double d:
            {
                s.WriteByte(0x23);
                Span<byte> buf = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(buf, d);
                s.Write(buf);
                break;
            }
            case float f:
                WriteObject(s, (double)f, refSize);
                break;
            case DateTimeOffset dto:
            {
                s.WriteByte(0x33);
                Span<byte> buf = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(buf, (dto - Epoch2001).TotalSeconds);
                s.Write(buf);
                break;
            }
            case DateTime dt:
                WriteObject(s, new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero), refSize);
                break;
            case byte[] data:
                WriteMarker(s, 0x4, data.Length);
                s.Write(data);
                break;
            case string str:
                if (Ascii.IsValid(str))
                {
                    WriteMarker(s, 0x5, str.Length);
                    s.Write(Encoding.ASCII.GetBytes(str));
                }
                else
                {
                    WriteMarker(s, 0x6, str.Length);
                    s.Write(Encoding.BigEndianUnicode.GetBytes(str));
                }
                break;
            case PlistUid uid:
            {
                var size = SizeFor(uid.Value);
                s.WriteByte((byte)(0x80 | (size - 1)));
                WriteUnsigned(s, uid.Value, size);
                break;
            }
            case ArrayNode arr:
                WriteMarker(s, 0xA, arr.Items.Length);
                foreach (var r in arr.Items)
                    WriteUnsigned(s, (ulong)r, refSize);
                break;
            case DictNode dict:
                WriteMarker(s, 0xD, dict.Keys.Length);
                foreach (var r in dict.Keys)
                    WriteUnsigned(s, (ulong)r, refSize);
                foreach (var r in dict.Values)
                    WriteUnsigned(s, (ulong)r, refSize);
                break;
            default:
                throw new InvalidOperationException($"Cannot write {value.GetType().Name} to a property list.");
        }
    }

    private static void WriteMarker(Stream s, int type, int count)
    {
        if (count < 15)
        {
            s.WriteByte((byte)((type << 4) | count));
            return;
        }
        s.WriteByte((byte)((type << 4) | 0xF));
        WriteInteger(s, count);
    }

    private static void WriteInteger(Stream s, long v)
    {
        if (v < 0)
        {
            s.WriteByte(0x13);
            Span<byte> neg = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(neg, v);
            s.Write(neg);
            return;
        }
        // 1, 2 and 4-byte integers are unsigned; 8-byte is signed, fine for any non-negative long.
        var size = v <= byte.MaxValue ? 1 : v <= ushort.MaxValue ? 2 : v <= uint.MaxValue ? 4 : 8;
        s.WriteByte((byte)(0x10 | (size switch { 1 => 0, 2 => 1, 4 => 2, _ => 3 })));
        WriteUnsigned(s, (ulong)v, size);
    }

    private static int SizeFor(ulong max) => max <= byte.MaxValue ? 1 : max <= ushort.MaxValue ? 2 : max <= uint.MaxValue ? 4 : 8;

    private static void WriteUnsigned(Stream s, ulong v, int size)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, v);
        s.Write(buf[(8 - size)..]);
    }

    private sealed record ArrayNode(int[] Items);

    private sealed record DictNode(int[] Keys, int[] Values);
}
