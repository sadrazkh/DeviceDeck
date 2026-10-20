using SkiaSharp;

namespace DeviceDeck.Android.Photos;

/// <summary>JPEG thumbnails with SkiaSharp (MIT). Decodes JPEG at 1/2, 1/4 or 1/8 scale when that is enough, honours EXIF orientation.</summary>
internal static class Thumbnailer
{
    public const int JpegQuality = 82;

    /// <summary>Formats Skia decodes on Windows; HEIC/HEIF, video and RAW go through MediaStore's own thumbnail instead.</summary>
    public static bool CanDecode(string? mimeType) => mimeType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" or "image/png" or "image/webp" or "image/gif" or "image/bmp" or "image/x-ms-bmp" => true,
        _ => false,
    };

    /// <summary>JPEG with the longest side close to <paramref name="size"/>; null when the bytes aren't an image Skia reads.</summary>
    public static byte[]? Create(byte[] data, int size)
    {
        size = Math.Clamp(size, 16, 2048);
        using var codec = SKCodec.Create(new SKMemoryStream(data));
        if (codec == null)
        {
            return null;
        }

        var info = codec.Info;
        int longest = Math.Max(info.Width, info.Height);
        float wanted = Math.Min(1f, (float)size / longest);
        // The codec picks the nearest scale it can decode natively (JPEG DCT scaling); never smaller than asked.
        var scaled = codec.GetScaledDimensions(wanted);
        if (Math.Max(scaled.Width, scaled.Height) < size && wanted < 1f)
        {
            scaled = info.Size;
        }
        var decodeInfo = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        using var resized = Resize(decoded, size);
        using var oriented = Orient(resized ?? decoded, codec.EncodedOrigin);
        using var image = SKImage.FromBitmap(oriented ?? resized ?? decoded);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return jpeg?.ToArray();
    }

    private static SKBitmap? Resize(SKBitmap source, int size)
    {
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= size)
        {
            return null;
        }
        double scale = (double)size / longest;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    /// <summary>Applies the EXIF orientation so the thumbnail is upright; null when nothing needs doing.</summary>
    internal static SKBitmap? Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return null;
        }
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int width = swap ? source.Height : source.Width;
        int height = swap ? source.Width : source.Height;
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.SetMatrix(OriginMatrix(origin, source.Width, source.Height));
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
        SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
        SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
        SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
        SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
        _ => SKMatrix.Identity,
    };
}
