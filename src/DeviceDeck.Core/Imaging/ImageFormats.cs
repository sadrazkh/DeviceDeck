namespace DeviceDeck.Core.Imaging;

/// <summary>Content type from the first bytes, so a provider that returns PNG where JPEG was expected is still served right.</summary>
public static class ImageFormats
{
    public static string Sniff(ReadOnlySpan<byte> head, string fallback = "application/octet-stream")
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (head.Length >= 8 && head[..8].SequenceEqual(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (head.Length >= 6 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F' && head[3] == '8')
        {
            return "image/gif";
        }

        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = head.Slice(8, 4);
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8) || brand.SequenceEqual("hevc"u8))
            {
                return "image/heic";
            }

            if (brand.SequenceEqual("qt  "u8))
            {
                return "video/quicktime";
            }

            return "video/mp4";
        }

        return fallback;
    }

    public static string FromExtension(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" or ".heif" => "image/heic",
        ".mov" => "video/quicktime",
        ".mp4" or ".m4v" => "video/mp4",
        _ => "application/octet-stream",
    };

    public static bool IsHeic(string fileName) =>
        Path.GetExtension(fileName).Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(fileName).Equals(".heif", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Converts images (HEIC to JPEG, resizing). The Shell supplies one built on Windows Imaging Component;
/// without it HEIC stays HEIC.
/// </summary>
public interface IImageTranscoder
{
    /// <summary>Re-encodes <paramref name="source"/> as JPEG, longest side at most <paramref name="maxSize"/> when given. Null when the format can't be decoded.</summary>
    Task<byte[]?> ToJpegAsync(Stream source, int? maxSize, CancellationToken ct);
}
