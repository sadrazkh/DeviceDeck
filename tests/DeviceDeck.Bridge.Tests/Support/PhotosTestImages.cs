using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>JPEG headers with no real image: enough for anything that only reads segments (APPn, SOF).</summary>
internal static class TestJpeg
{
    public static byte[] Make(int width, int height, bool encoderExif = false, long payload = 0)
    {
        using var s = new MemoryStream();
        s.Write([0xFF, 0xD8]);
        s.Write([0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        if (encoderExif)
        {
            // What an encoder might write on its own: replaced by the photo's EXIF.
            var tiff = TestTiff.Make(orientation: 1, dateTaken: null, pixelX: null, pixelY: null);
            s.Write([0xFF, 0xE1]);
            Span<byte> length = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(2 + 6 + tiff.Length));
            s.Write(length);
            s.Write("Exif\0\0"u8);
            s.Write(tiff);
        }

        s.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        s.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
        // "Scan data" that differs per input, so two converted photos never look like one.
        s.Write(SHA256.HashData(BitConverter.GetBytes(payload ^ width ^ (long)height << 20)));
        s.Write(SHA256.HashData(BitConverter.GetBytes(payload)));
        s.Write([0xFF, 0xD9]);
        return s.ToArray();
    }
}

/// <summary>A TIFF/EXIF block: IFD0 with Orientation and the Exif IFD pointer; the Exif IFD with DateTimeOriginal and the pixel size.</summary>
internal static class TestTiff
{
    public static byte[] Make(ushort orientation, string? dateTaken = "2026:10:15 13:00:00", ushort? pixelX = 4032, ushort? pixelY = 3024, bool littleEndian = true, int padding = 0)
    {
        var exifEntries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>();
        var ifd0Size = 2 + (2 * 12) + 4;
        var exifIfd = 8 + ifd0Size;
        var exifCount = (dateTaken is null ? 0 : 1) + (pixelX is null ? 0 : 1) + (pixelY is null ? 0 : 1);
        var exifSize = 2 + (exifCount * 12) + 4;
        var stringAt = exifIfd + exifSize;
        if (dateTaken is not null)
        {
            exifEntries.Add((0x9003, 2, (uint)dateTaken.Length + 1, (uint)stringAt));
        }

        if (pixelX is { } x)
        {
            exifEntries.Add((0xA002, 3, 1, x));
        }

        if (pixelY is { } y)
        {
            exifEntries.Add((0xA003, 3, 1, y));
        }

        var bytes = new byte[stringAt + (dateTaken?.Length + 1 ?? 0) + padding];
        void U16(int at, ushort v)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), v);
            else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), v);
        }

        void U32(int at, uint v)
        {
            if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), v);
            else BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), v);
        }

        void Entry(int at, ushort tag, ushort type, uint count, uint value)
        {
            U16(at, tag);
            U16(at + 2, type);
            U32(at + 4, count);
            if (type == 3 && count == 1)
            {
                U16(at + 8, (ushort)value);
            }
            else
            {
                U32(at + 8, value);
            }
        }

        bytes[0] = bytes[1] = (byte)(littleEndian ? 'I' : 'M');
        U16(2, 42);
        U32(4, 8);
        U16(8, 2);
        Entry(10, 0x0112, 3, 1, orientation);
        Entry(22, 0x8769, 4, 1, (uint)exifIfd);
        U16(exifIfd, (ushort)exifEntries.Count);
        for (var i = 0; i < exifEntries.Count; i++)
        {
            var (tag, type, count, value) = exifEntries[i];
            Entry(exifIfd + 2 + (i * 12), tag, type, count, value);
        }

        if (dateTaken is not null)
        {
            Encoding.ASCII.GetBytes(dateTaken).CopyTo(bytes, stringAt);
        }

        return bytes;
    }
}

/// <summary>A HEIF container (ftyp, meta with iinf/iloc, mdat) holding a dummy image item and an Exif item.</summary>
internal static class TestHeif
{
    public static byte[] Make(byte[] tiff, int ilocVersion = 1, bool exifInIdat = false, int imageBytes = 300)
    {
        var exif = Concat(U32(6), "Exif\0\0"u8.ToArray(), tiff);
        var image = Enumerable.Range(0, imageBytes).Select(i => (byte)i).ToArray();
        var ftyp = Box("ftyp", Ascii("heic"), U32(0), Ascii("mif1"), Ascii("heic"));

        byte[] Meta(uint imageOffset, uint exifOffset) => FullBox("meta", 0,
            FullBox("hdlr", 0, U32(0), Ascii("pict"), new byte[12], [0]),
            FullBox("pitm", 0, U16(1)),
            FullBox("iinf", 0, U16(2),
                FullBox("infe", 2, U16(1), U16(0), Ascii("hvc1"), [0]),
                FullBox("infe", 2, U16(2), U16(0), Ascii("Exif"), [0])),
            Iloc(ilocVersion, (1, 0, imageOffset, (uint)image.Length), (2, exifInIdat ? 1 : 0, exifInIdat ? 0 : exifOffset, (uint)exif.Length)),
            exifInIdat ? Box("idat", exif) : []);

        var metaLength = Meta(0, 0).Length;
        var mdatStart = (uint)(ftyp.Length + metaLength + 8);
        var meta = Meta(mdatStart, mdatStart + (uint)image.Length);
        var mdat = Box("mdat", image, exifInIdat ? [] : exif);
        return Concat(ftyp, meta, mdat);
    }

    private static byte[] Iloc(int version, params (ushort Id, int Method, uint Offset, uint Length)[] items)
    {
        // Version 2 widens the item count and ids to 32 bits.
        var parts = new List<byte[]> { new byte[] { 0x44, 0x00 }, version < 2 ? U16((ushort)items.Length) : U32((uint)items.Length) };
        foreach (var (id, method, offset, length) in items)
        {
            parts.Add(version < 2 ? U16(id) : U32(id));
            if (version >= 1)
            {
                parts.Add(U16((ushort)method));
            }

            parts.Add(U16(0)); // data_reference_index
            parts.Add(U16(1)); // one extent
            parts.Add(U32(offset));
            parts.Add(U32(length));
        }

        return FullBox("iloc", (byte)version, [.. parts]);
    }

    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = Concat(parts);
        return Concat(U32((uint)(8 + body.Length)), Ascii(type), body);
    }

    private static byte[] FullBox(string type, byte version, params byte[][] parts) => Box(type, [[version, 0, 0, 0], .. parts]);

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
