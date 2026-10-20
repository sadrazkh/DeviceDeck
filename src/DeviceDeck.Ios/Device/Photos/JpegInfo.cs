namespace DeviceDeck.Ios.Device.Photos;

/// <summary>Reads a JPEG's pixel size from its SOF marker without decoding it.</summary>
public static class JpegInfo
{
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return null;
        var i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
                return null;
            var marker = jpeg[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2;
                continue;
            }
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2)
                return null;
            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 9 > jpeg.Length)
                    return null;
                var height = (jpeg[i + 5] << 8) | jpeg[i + 6];
                var width = (jpeg[i + 7] << 8) | jpeg[i + 8];
                return width > 0 && height > 0 ? (width, height) : null;
            }
            if (marker == 0xDA)
                return null; // start of scan before any frame header
            i += 2 + length;
        }
        return null;
    }
}
