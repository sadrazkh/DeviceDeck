using DeviceDeck.Core.Photos;
using Windows.Graphics.Imaging;

namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// Can this PC turn HEIC into JPEG? Windows decodes HEIC through Windows Imaging Component only with the
/// "HEIF Image Extensions" installed (and "HEVC Video Extensions" for the pixels), both from the Microsoft Store.
/// Without them the backup keeps HEIC files as they are and says so; a conversion that fails at run time also
/// switches the status to MissingCodec.
/// </summary>
internal static class PhotosHeicProbe
{
    public static HeicConversion Check()
    {
        try
        {
            var heif = BitmapDecoder.HeifDecoderId;
            return BitmapDecoder.GetDecoderInformationEnumerator().Any(d => d.CodecId == heif)
                ? HeicConversion.Available
                : HeicConversion.MissingCodec;
        }
        catch (Exception ex)
        {
            ShellLog.Info($"HEIF codec check failed: 0x{ex.HResult:X8} {ex.Message}");
            return HeicConversion.Unknown;
        }
    }
}
