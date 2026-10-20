using DeviceDeck.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// HEIC (or anything Windows Imaging Component decodes) to JPEG. HEIC needs the HEIF and HEVC extensions
/// from the Microsoft Store; without them decoding fails and callers keep the original.
/// </summary>
internal sealed class WicImageTranscoder : IImageTranscoder
{
    public async Task<byte[]?> ToJpegAsync(Stream source, int? maxSize, CancellationToken ct)
    {
        try
        {
            using var input = source.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct).ConfigureAwait(false);
            var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
            if (maxSize is { } longest && Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight) > longest)
            {
                var scale = (double)longest / Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
                // Scaled sizes apply before orientation, so use the stored (unrotated) dimensions.
                transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
            }

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);

            using var output = new InMemoryRandomAccessStream();
            var quality = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.92f, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, quality).AsTask(ct).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

            var bytes = new byte[output.Size];
            output.Seek(0);
            using var reader = new DataReader(output.GetInputStreamAt(0));
            await reader.LoadAsync((uint)output.Size).AsTask(ct).ConfigureAwait(false);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // WINCODEC_ERR_COMPONENTNOTFOUND and friends: no codec for this format.
            ShellLog.Info($"Image conversion unavailable: 0x{ex.HResult:X8} {ex.Message}");
            return null;
        }
    }
}
