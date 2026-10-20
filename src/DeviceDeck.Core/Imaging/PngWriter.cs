using System.Buffers.Binary;
using System.IO.Compression;

namespace DeviceDeck.Core.Imaging;

/// <summary>A tiny PNG encoder (8-bit RGBA) so the fake provider can draw thumbnails, icons and screenshots without an imaging library.</summary>
public static class PngWriter
{
    /// <param name="pixel">Called for every pixel; returns 0xRRGGBBAA.</param>
    public static byte[] Encode(int width, int height, Func<int, int, uint> pixel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type: RGBA
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR"u8, header);

        using (var raw = new MemoryStream())
        {
            using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + width * 4];
                for (var y = 0; y < height; y++)
                {
                    row[0] = 0; // filter: none
                    for (var x = 0; x < width; x++)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(1 + x * 4), pixel(x, y));
                    }

                    zlib.Write(row);
                }
            }

            WriteChunk(output, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        }

        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        output.Write(type);
        output.Write(data);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
