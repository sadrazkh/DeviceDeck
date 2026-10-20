using System.Buffers.Binary;
using System.Text;

namespace DeviceDeck.Core.Imaging;

/// <summary>
/// Carries a photo's EXIF (date taken, camera, GPS, Apple's maker note that pairs a Live Photo) from a HEIC into the
/// JPEG made from it. Re-encoding through Windows Imaging Component keeps only pixels, so the EXIF item is read from the
/// HEIF container (ISO/IEC 23008-12: meta → iinf / iloc) and written into the JPEG as its APP1 segment. The pixels
/// come out upright, so Orientation becomes 1 and the pixel size is the JPEG's own.
/// </summary>
public static class PhotosHeicMetadata
{
    /// <summary>EXIF in a JPEG APP1 is at most 65,535 bytes with its header.</summary>
    private const int MaxApp1Tiff = 65535 - 2 - 6;

    private const int MaxMetaBox = 4 * 1024 * 1024;

    private static ReadOnlySpan<byte> ExifHeader => "Exif\0\0"u8;

    /// <summary>The EXIF block (from its TIFF header on) of a HEIF/HEIC file, or null when it has none or isn't HEIF.</summary>
    public static byte[]? ReadExif(Stream heif)
    {
        try
        {
            if (!heif.CanSeek)
            {
                return null;
            }

            heif.Position = 0;
            var meta = FindTopLevelBox(heif, "meta");
            if (meta is null)
            {
                return null;
            }

            // meta is a FullBox: version and flags before its children.
            var body = meta.AsSpan(4);
            uint? exifItem = null;
            ReadOnlySpan<byte> iloc = default, idat = default;
            foreach (var (type, payload) in Boxes(body))
            {
                switch (type)
                {
                    case "iinf":
                        exifItem = FindExifItem(payload);
                        break;
                    case "iloc":
                        iloc = payload;
                        break;
                    case "idat":
                        idat = payload;
                        break;
                }
            }

            if (exifItem is not { } item || iloc.IsEmpty)
            {
                return null;
            }

            var data = ReadItem(heif, iloc, idat, item);
            if (data is null || data.Length < 4)
            {
                return null;
            }

            // The item starts with the offset of the TIFF header after these four bytes (usually 6, skipping "Exif\0\0").
            var skip = 4 + (long)BinaryPrimitives.ReadUInt32BigEndian(data);
            if (skip > data.Length - 8)
            {
                return null;
            }

            var tiff = data[(int)skip..];
            return IsTiff(tiff) ? tiff : null;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="jpeg"/> with <paramref name="tiff"/> as its EXIF (an APP1 the encoder wrote is replaced), Orientation 1,
    /// and PixelXDimension/PixelYDimension set to the JPEG's size. Unchanged when either side is unusable.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, byte[] tiff)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || !IsTiff(tiff) || tiff.Length > MaxApp1Tiff)
        {
            return jpeg;
        }

        var segments = new List<(int Start, int Length, byte Marker)>();
        var pos = 2;
        int? width = null, height = null;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker == 0xDA || marker == 0xD9)
            {
                break; // start of scan: no more headers
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > jpeg.Length)
            {
                return jpeg;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC && length >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 5));
                width = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 7));
            }

            segments.Add((pos, 2 + length, marker));
            pos += 2 + length;
        }

        var exif = (byte[])tiff.Clone();
        PatchTiff(exif, width, height);

        using var output = new MemoryStream(jpeg.Length + exif.Length + 16);
        output.Write(jpeg, 0, 2);
        var rest = 2;
        // Keep a JFIF APP0 first, as readers expect; the EXIF APP1 follows it.
        if (segments.Count > 0 && segments[0].Marker == 0xE0)
        {
            output.Write(jpeg, segments[0].Start, segments[0].Length);
            rest = segments[0].Start + segments[0].Length;
        }

        output.WriteByte(0xFF);
        output.WriteByte(0xE1);
        Span<byte> size = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)(2 + ExifHeader.Length + exif.Length));
        output.Write(size);
        output.Write(ExifHeader);
        output.Write(exif);

        // Everything after, minus any EXIF APP1 the encoder wrote itself.
        foreach (var (start, length, marker) in segments.Where(s => s.Start >= rest))
        {
            if (marker == 0xE1 && length >= 10 && jpeg.AsSpan(start + 4, 6).SequenceEqual(ExifHeader))
            {
                output.Write(jpeg, rest, start - rest);
                rest = start + length;
            }
        }

        output.Write(jpeg, rest, jpeg.Length - rest);
        return output.ToArray();
    }

    /// <summary>The EXIF block of a JPEG (TIFF header on), or null.</summary>
    public static byte[]? ReadJpegExif(byte[] jpeg)
    {
        var pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] is not (0xDA or 0xD9))
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (jpeg[pos + 1] == 0xE1 && length >= 8 && pos + 2 + length <= jpeg.Length && jpeg.AsSpan(pos + 4, 6).SequenceEqual(ExifHeader))
            {
                return jpeg.AsSpan(pos + 10, length - 8).ToArray();
            }

            pos += 2 + length;
        }

        return null;
    }

    /// <summary>A SHORT or LONG value of IFD0 (<paramref name="exifIfd"/> false) or the Exif IFD, or null.</summary>
    public static uint? ReadTag(byte[] tiff, ushort tag, bool exifIfd = false)
    {
        if (!IsTiff(tiff))
        {
            return null;
        }

        var little = tiff[0] == 'I';
        var ifd = U32(tiff, 4, little);
        if (exifIfd)
        {
            ifd = FindEntry(tiff, ifd, 0x8769, little) is { } pointer ? U32(tiff, pointer + 8, little) : 0;
        }

        return FindEntry(tiff, ifd, tag, little) is { } entry ? ReadValue(tiff, entry, little) : null;
    }

    private static void PatchTiff(byte[] tiff, int? width, int? height)
    {
        var little = tiff[0] == 'I';
        var ifd0 = U32(tiff, 4, little);
        if (FindEntry(tiff, ifd0, 0x0112, little) is { } orientation)
        {
            WriteValue(tiff, orientation, 1, little);
        }

        if (width is null || height is null || FindEntry(tiff, ifd0, 0x8769, little) is not { } pointer)
        {
            return;
        }

        var exifIfd = U32(tiff, pointer + 8, little);
        if (FindEntry(tiff, exifIfd, 0xA002, little) is { } x)
        {
            WriteValue(tiff, x, (uint)width.Value, little);
        }

        if (FindEntry(tiff, exifIfd, 0xA003, little) is { } y)
        {
            WriteValue(tiff, y, (uint)height.Value, little);
        }
    }

    /// <summary>Offset of the 12-byte IFD entry for <paramref name="tag"/>, or null.</summary>
    private static uint? FindEntry(byte[] tiff, uint ifd, ushort tag, bool little)
    {
        if (ifd == 0 || ifd + 2 > tiff.Length)
        {
            return null;
        }

        var count = U16(tiff, ifd, little);
        for (uint i = 0; i < count; i++)
        {
            var entry = ifd + 2 + (i * 12);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            if (U16(tiff, entry, little) == tag)
            {
                return entry;
            }
        }

        return null;
    }

    private static uint? ReadValue(byte[] tiff, uint entry, bool little) => U16(tiff, entry + 2, little) switch
    {
        3 => U16(tiff, entry + 8, little),
        4 => U32(tiff, entry + 8, little),
        _ => null,
    };

    /// <summary>Overwrites a one-value SHORT or LONG entry in place (the value sits inside the entry).</summary>
    private static void WriteValue(byte[] tiff, uint entry, uint value, bool little)
    {
        var type = U16(tiff, entry + 2, little);
        if (U32(tiff, entry + 4, little) != 1)
        {
            return;
        }

        var at = tiff.AsSpan((int)entry + 8, 4);
        if (type == 3 && value <= ushort.MaxValue)
        {
            if (little)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(at, (ushort)value);
            }
        }
        else if (type == 4)
        {
            if (little)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(at, value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(at, value);
            }
        }
    }

    private static ushort U16(byte[] b, uint at, bool little) =>
        at + 2 > b.Length ? (ushort)0 : little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)at)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan((int)at));

    private static uint U32(byte[] b, uint at, bool little) =>
        at + 4 > b.Length ? 0 : little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan((int)at));

    private static bool IsTiff(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42));

    /// <summary>The payload of the first top-level box of <paramref name="type"/>, read into memory.</summary>
    private static byte[]? FindTopLevelBox(Stream s, string type)
    {
        Span<byte> header = stackalloc byte[16];
        while (s.Position + 8 <= s.Length)
        {
            var start = s.Position;
            s.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var name = Encoding.ASCII.GetString(header.Slice(4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                s.ReadExactly(header.Slice(8, 8));
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = s.Length - start;
            }

            if (size < headerLength || start + size > s.Length)
            {
                return null;
            }

            if (name == type)
            {
                if (size - headerLength > MaxMetaBox)
                {
                    return null;
                }

                var payload = new byte[size - headerLength];
                s.ReadExactly(payload);
                return payload;
            }

            s.Position = start + size;
        }

        return null;
    }

    /// <summary>Child boxes of a payload (type, payload), stopping at the first malformed one.</summary>
    private static List<(string Type, byte[] Payload)> Boxes(ReadOnlySpan<byte> body)
    {
        var boxes = new List<(string, byte[])>();
        var pos = 0;
        while (pos + 8 <= body.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(body[pos..]);
            var type = Encoding.ASCII.GetString(body.Slice(pos + 4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                if (pos + 16 > body.Length)
                {
                    break;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(body[(pos + 8)..]);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = body.Length - pos;
            }

            if (size < headerLength || pos + size > body.Length)
            {
                break;
            }

            boxes.Add((type, body.Slice(pos + headerLength, (int)size - headerLength).ToArray()));
            pos += (int)size;
        }

        return boxes;
    }

    /// <summary>The item id whose infe has item_type "Exif".</summary>
    private static uint? FindExifItem(ReadOnlySpan<byte> iinf)
    {
        if (iinf.Length < 6)
        {
            return null;
        }

        var version = iinf[0];
        var children = iinf[(version == 0 ? 6 : 8)..];
        foreach (var (type, infe) in Boxes(children))
        {
            if (type != "infe" || infe.Length < 4)
            {
                continue;
            }

            var infeVersion = infe[0];
            if (infeVersion < 2)
            {
                continue; // versions 0 and 1 carry no item type
            }

            var idLength = infeVersion == 2 ? 2 : 4;
            if (infe.Length < 4 + idLength + 2 + 4)
            {
                continue;
            }

            var id = idLength == 2 ? BinaryPrimitives.ReadUInt16BigEndian(infe.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(infe.AsSpan(4));
            var itemType = Encoding.ASCII.GetString(infe, 4 + idLength + 2, 4);
            if (itemType == "Exif")
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>An item's bytes, from the file (construction method 0) or from idat (1).</summary>
    private static byte[]? ReadItem(Stream s, ReadOnlySpan<byte> iloc, ReadOnlySpan<byte> idat, uint wanted)
    {
        var version = iloc[0];
        var pos = 4;
        var offsetSize = iloc[pos] >> 4;
        var lengthSize = iloc[pos] & 0xF;
        var baseOffsetSize = iloc[pos + 1] >> 4;
        var indexSize = version is 1 or 2 ? iloc[pos + 1] & 0xF : 0;
        pos += 2;
        uint count;
        if (version < 2)
        {
            count = BinaryPrimitives.ReadUInt16BigEndian(iloc[pos..]);
            pos += 2;
        }
        else
        {
            count = BinaryPrimitives.ReadUInt32BigEndian(iloc[pos..]);
            pos += 4;
        }

        for (uint i = 0; i < count; i++)
        {
            uint id;
            if (version < 2)
            {
                id = BinaryPrimitives.ReadUInt16BigEndian(iloc[pos..]);
                pos += 2;
            }
            else
            {
                id = BinaryPrimitives.ReadUInt32BigEndian(iloc[pos..]);
                pos += 4;
            }

            var method = 0;
            if (version is 1 or 2)
            {
                method = BinaryPrimitives.ReadUInt16BigEndian(iloc[pos..]) & 0xF;
                pos += 2;
            }

            pos += 2; // data_reference_index
            var baseOffset = ReadSized(iloc, ref pos, baseOffsetSize);
            var extents = BinaryPrimitives.ReadUInt16BigEndian(iloc[pos..]);
            pos += 2;
            var parts = new List<(long Offset, long Length)>();
            for (var e = 0; e < extents; e++)
            {
                ReadSized(iloc, ref pos, indexSize);
                var offset = ReadSized(iloc, ref pos, offsetSize);
                var length = ReadSized(iloc, ref pos, lengthSize);
                parts.Add((baseOffset + offset, length));
            }

            if (id != wanted)
            {
                continue;
            }

            if (method is not (0 or 1) || parts.Sum(p => p.Length) is <= 0 or > MaxMetaBox)
            {
                return null;
            }

            using var data = new MemoryStream();
            foreach (var (offset, length) in parts)
            {
                if (method == 1)
                {
                    if (offset + length > idat.Length)
                    {
                        return null;
                    }

                    data.Write(idat.Slice((int)offset, (int)length));
                }
                else
                {
                    if (offset + length > s.Length)
                    {
                        return null;
                    }

                    var chunk = new byte[length];
                    s.Position = offset;
                    s.ReadExactly(chunk);
                    data.Write(chunk);
                }
            }

            return data.ToArray();
        }

        return null;
    }

    private static long ReadSized(ReadOnlySpan<byte> b, ref int pos, int size)
    {
        long value = size switch
        {
            0 => 0,
            4 => BinaryPrimitives.ReadUInt32BigEndian(b[pos..]),
            8 => (long)BinaryPrimitives.ReadUInt64BigEndian(b[pos..]),
            _ => throw new IOException($"iloc field size {size} isn't allowed."),
        };
        pos += size;
        return value;
    }
}
