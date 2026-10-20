using DeviceDeck.Core.Toolbox;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeviceDeck.Shell.Toolbox;

/// <summary>
/// HEIC → JPEG with Windows Imaging Component: upright pixels, the chosen quality, and the date taken, camera and
/// location copied over. Decoding needs the HEIF Image Extensions (and HEVC Video Extensions) from the Store.
/// </summary>
internal sealed class WicHeicConverter : IHeicConverter
{
    private static readonly string[] CopiedProperties =
    [
        "System.Photo.DateTaken",
        "System.Photo.CameraManufacturer",
        "System.Photo.CameraModel",
        "System.Photo.LensModel",
        "System.Photo.FNumber",
        "System.Photo.ExposureTime",
        "System.Photo.ISOSpeed",
        "System.Photo.FocalLength",
        "System.GPS.Latitude",
        "System.GPS.LatitudeRef",
        "System.GPS.Longitude",
        "System.GPS.LongitudeRef",
        "System.GPS.Altitude",
        "System.GPS.AltitudeRef",
    ];

    private bool? _available;

    public Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        _available ??= BitmapDecoder.GetDecoderInformationEnumerator().Any(d => d.CodecId == BitmapDecoder.HeifDecoderId);
        return Task.FromResult(_available.Value);
    }

    public async Task ConvertAsync(string source, string destination, int quality, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(source).AsTask(ct).ConfigureAwait(false);
            using var input = await file.OpenReadAsync().AsTask(ct).ConfigureAwait(false);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct).ConfigureAwait(false);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);

            var metadata = new BitmapPropertySet();
            foreach (var name in CopiedProperties)
            {
                try
                {
                    var found = await decoder.BitmapProperties.GetPropertiesAsync([name]).AsTask(ct).ConfigureAwait(false);
                    if (found.TryGetValue(name, out var value))
                    {
                        metadata[name] = value;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not in this file, or not readable through this codec.
                }
            }

            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(Math.Clamp(quality, 1, 100) / 100f, Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options).AsTask(ct).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            foreach (var (name, value) in metadata)
            {
                try
                {
                    await encoder.BitmapProperties.SetPropertiesAsync(new BitmapPropertySet { [name] = value }).AsTask(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // JPEG can't hold it in that form; the photo is still right.
                }
            }

            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
            await using var target = File.Create(destination);
            output.Seek(0);
            await output.AsStreamForRead().CopyToAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex.HResult is unchecked((int)0x88982F50) or unchecked((int)0x88982F8B) or unchecked((int)0xC00D5212))
        {
            // WINCODEC_ERR_COMPONENTNOTFOUND / UNSUPPORTEDOPERATION / MF_E_TOPO_CODEC_NOT_FOUND: HEVC isn't installed.
            throw new InvalidDataException("Windows can't decode this photo; install HEVC Video Extensions from the Microsoft Store.", ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException))
        {
            throw new InvalidDataException($"Windows couldn't read this photo (0x{ex.HResult:X8}).", ex);
        }
    }
}
