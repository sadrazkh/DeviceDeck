using System.Collections.Concurrent;
using System.Globalization;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Imaging;

namespace DeviceDeck.Bridge.Images;

/// <summary>An HTTP-like answer for one image request; the Shell turns it into a CoreWebView2WebResourceResponse.</summary>
public sealed record ImageResponse(int StatusCode, string ReasonPhrase, string ContentType, Stream? Body, string CacheControl)
{
    public static ImageResponse NotFound() => new(404, "Not Found", "text/plain", null, "no-store");

    public static ImageResponse Ok(byte[] bytes, string cacheControl) =>
        new(200, "OK", ImageFormats.Sniff(bytes), new MemoryStream(bytes, writable: false), cacheControl);

    /// <summary>Headers as one string, the way CreateWebResourceResponse takes them.</summary>
    public string Headers =>
        $"Content-Type: {ContentType}\r\nCache-Control: {CacheControl}\r\nAccess-Control-Allow-Origin: *" +
        (Body is { CanSeek: true } body ? $"\r\nContent-Length: {body.Length}" : "");
}

/// <summary>
/// Serves https://device.local/: thumb/{deviceId}/{assetId}?s=256 (disk-cached), photo/{deviceId}/{assetId},
/// icon/{deviceId}/{appId} and shot/{deviceId}/{shotId}. Anything missing is a 404; the page shows a placeholder.
/// </summary>
public sealed class DeviceImageHandler
{
    public const string Host = "device.local";
    public const string Origin = "https://device.local/";
    public const string Filter = "https://device.local/*";

    private static readonly int[] ThumbSizes = [128, 256, 512, 1024];
    private const string Immutable = "max-age=31536000, immutable";

    private readonly DeviceHub _hub;
    private readonly ThumbnailCache _thumbs;
    private readonly ScreenshotStore _shots;
    private readonly IImageTranscoder? _transcoder;
    private readonly string _viewerTemp;
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perDevice = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte[]> _icons = new(StringComparer.Ordinal);

    public DeviceImageHandler(DeviceHub hub, ThumbnailCache thumbs, ScreenshotStore shots, IImageTranscoder? transcoder, string viewerTempFolder)
    {
        _hub = hub;
        _thumbs = thumbs;
        _shots = shots;
        _transcoder = transcoder;
        _viewerTemp = viewerTempFolder;
    }

    /// <summary>At most this many thumbnail reads per phone at a time; USB is one pipe.</summary>
    public int ThumbnailConcurrency { get; init; } = 4;

    public static string ThumbUrl(string deviceId, string assetId, int size = 256) =>
        $"{Origin}thumb/{Uri.EscapeDataString(deviceId)}/{Uri.EscapeDataString(assetId)}?s={size}";

    public async Task<ImageResponse> HandleAsync(Uri uri, CancellationToken ct)
    {
        if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            return ImageResponse.NotFound();
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length != 3)
        {
            return ImageResponse.NotFound();
        }

        var (kind, deviceId, id) = (parts[0], parts[1], parts[2]);
        try
        {
            return kind switch
            {
                "thumb" => await ThumbAsync(deviceId, id, ParseSize(uri.Query), ct).ConfigureAwait(false),
                "photo" => await PhotoAsync(deviceId, id, ct).ConfigureAwait(false),
                "icon" => await IconAsync(deviceId, id, ct).ConfigureAwait(false),
                "shot" => _shots.TryGet(deviceId, id) is { } png ? ImageResponse.Ok(png, "private, max-age=3600") : ImageResponse.NotFound(),
                _ => ImageResponse.NotFound(),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Disconnected, not found, decode failure: the page shows its placeholder either way.
            return ImageResponse.NotFound();
        }
    }

    private async Task<ImageResponse> ThumbAsync(string deviceId, string assetId, int size, CancellationToken ct)
    {
        var cached = _thumbs.TryRead(deviceId, assetId, size);
        if (cached is not null)
        {
            return ImageResponse.Ok(cached, Immutable);
        }

        if (!_hub.TryGetDevice(deviceId, out var device))
        {
            return ImageResponse.NotFound();
        }

        // One fetch per thumbnail even when the grid asks twice.
        var key = $"{deviceId}\n{assetId}\n{size}";
        var fetch = _inFlight.GetOrAdd(key, k => new Lazy<Task<byte[]?>>(() => FetchThumbAsync(device, k, deviceId, assetId, size)));
        var bytes = await fetch.Value.WaitAsync(ct).ConfigureAwait(false);
        return bytes is null ? ImageResponse.NotFound() : ImageResponse.Ok(bytes, Immutable);
    }

    private async Task<byte[]?> FetchThumbAsync(IDevice device, string key, string deviceId, string assetId, int size)
    {
        var gate = _perDevice.GetOrAdd(deviceId, _ => new SemaphoreSlim(ThumbnailConcurrency));
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var bytes = await device.Photos.GetThumbnailAsync(assetId, size, timeout.Token).ConfigureAwait(false);
                if (bytes is not { Length: > 0 })
                {
                    // No thumbnail on the phone (an iPhone without PhotoData/Thumbnails): make one from the original.
                    bytes = await ThumbnailFromOriginalAsync(device, assetId, size, timeout.Token).ConfigureAwait(false);
                }

                if (bytes is not { Length: > 0 })
                {
                    return null;
                }

                _thumbs.Write(deviceId, assetId, size, bytes);
                return bytes;
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>Originals larger than this are not read just to make a thumbnail.</summary>
    public long MaxOriginalForThumbnail { get; init; } = 64L * 1024 * 1024;

    private async Task<byte[]?> ThumbnailFromOriginalAsync(IDevice device, string assetId, int size, CancellationToken ct)
    {
        if (_transcoder is null)
        {
            return null;
        }

        using var original = new HeadCheckedStream(MaxOriginalForThumbnail, head => ImageFormats.Sniff(head).StartsWith("image/", StringComparison.Ordinal));
        try
        {
            await device.Photos.ReadOriginalAsync(assetId, original, null, ct).ConfigureAwait(false);
        }
        catch (HeadCheckedStream.RejectedException)
        {
            return null; // a video, or too big
        }

        original.Position = 0;
        return await _transcoder.ToJpegAsync(original, size, ct).ConfigureAwait(false);
    }

    private async Task<ImageResponse> PhotoAsync(string deviceId, string assetId, CancellationToken ct)
    {
        if (!_hub.TryGetDevice(deviceId, out var device))
        {
            return ImageResponse.NotFound();
        }

        // Originals can be large videos: spool to a temp file that deletes itself when WebView2 lets go of it.
        Directory.CreateDirectory(_viewerTemp);
        var path = Path.Combine(_viewerTemp, Guid.NewGuid().ToString("N") + ".tmp");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await device.Photos.ReadOriginalAsync(assetId, file, null, ct).ConfigureAwait(false);
            file.Position = 0;
            var head = new byte[16];
            var read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
            file.Position = 0;
            var type = ImageFormats.Sniff(head.AsSpan(0, read));
            if (type == "image/heic" && _transcoder is not null)
            {
                var jpeg = await _transcoder.ToJpegAsync(file, null, ct).ConfigureAwait(false);
                await file.DisposeAsync().ConfigureAwait(false);
                return jpeg is null ? ImageResponse.NotFound() : ImageResponse.Ok(jpeg, "private, max-age=600");
            }

            return new ImageResponse(200, "OK", type, file, "private, max-age=600");
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ImageResponse> IconAsync(string deviceId, string appId, CancellationToken ct)
    {
        var key = deviceId + "\n" + appId;
        if (_icons.TryGetValue(key, out var cached))
        {
            return ImageResponse.Ok(cached, "max-age=86400");
        }

        if (!_hub.TryGetDevice(deviceId, out var device))
        {
            return ImageResponse.NotFound();
        }

        var bytes = await device.Apps.GetIconAsync(appId, ct).ConfigureAwait(false);
        if (bytes is not { Length: > 0 })
        {
            return ImageResponse.NotFound();
        }

        _icons[key] = bytes;
        return ImageResponse.Ok(bytes, "max-age=86400");
    }

    /// <summary>?s=… rounded up to 128, 256, 512 or 1024 so the cache stays small.</summary>
    internal static int ParseSize(string query)
    {
        var requested = 256;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            if (cut > 0 && pair[..cut] == "s" && int.TryParse(pair[(cut + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s > 0)
            {
                requested = s;
            }
        }

        return ThumbSizes.FirstOrDefault(t => t >= requested, ThumbSizes[^1]);
    }
}
