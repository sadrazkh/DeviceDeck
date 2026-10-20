using System.Globalization;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Imaging;

namespace DeviceDeck.Bridge.Images;

/// <summary>An HTTP-like answer with its own headers (status 200, 206 or 416), for the Shell to hand to WebView2.</summary>
public sealed record MediaResponse(int StatusCode, string ReasonPhrase, string Headers, Stream? Body)
{
    public static MediaResponse NotFound() => new(404, "Not Found", "Content-Type: text/plain\r\nCache-Control: no-store", null);
}

/// <summary>
/// Serves https://device.local/video/{deviceId}/{assetId}?size={bytes} with HTTP Range, so the viewer's &lt;video&gt;
/// can seek. The original is copied off the phone once, into a cache file, and each range is answered from it
/// as soon as its bytes have arrived. <c>size</c> is <see cref="Core.Abstractions.PhotoAsset.Size"/>; without it
/// the answer waits for the whole copy. The last few videos stay cached; the rest are deleted.
/// </summary>
public sealed class VideoHandler : IAsyncDisposable
{
    private readonly DeviceHub _hub;
    private readonly string _folder;
    private readonly Lock _gate = new();
    private readonly LinkedList<VideoSpool> _spools = new();
    private bool _disposed;

    public VideoHandler(DeviceHub hub, string cacheFolder)
    {
        _hub = hub;
        _folder = cacheFolder;
    }

    /// <summary>Videos kept on disk after the viewer moved on (the newest ones).</summary>
    public int KeepVideos { get; init; } = 3;

    public static string UrlFor(string deviceId, string assetId, long size) =>
        $"{DeviceImageHandler.Origin}video/{Uri.EscapeDataString(deviceId)}/{Uri.EscapeDataString(assetId)}?size={size.ToString(CultureInfo.InvariantCulture)}";

    public static bool Handles(Uri uri) =>
        string.Equals(uri.Host, DeviceImageHandler.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/video/", StringComparison.Ordinal);

    /// <param name="range">The request's Range header, or null.</param>
    public async Task<MediaResponse> HandleAsync(Uri uri, string? range, CancellationToken ct)
    {
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (!Handles(uri) || parts.Length != 3 || !_hub.TryGetDevice(parts[1], out _))
        {
            return MediaResponse.NotFound();
        }

        var spool = Open(parts[1], parts[2], ParseSize(uri.Query));
        try
        {
            var total = spool.DeclaredSize ?? await spool.WaitForEndAsync(ct).ConfigureAwait(false);
            var head = await spool.ReadHeadAsync(ct).ConfigureAwait(false);
            var type = ImageFormats.Sniff(head, "video/mp4");
            if (type == "video/quicktime" || !type.StartsWith("video/", StringComparison.Ordinal))
            {
                // iPhone .MOV files are ISO media; Chromium plays them when they are called MP4.
                type = "video/mp4";
            }

            var common = $"Content-Type: {type}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *";
            if (total == 0)
            {
                spool.Release();
                return new MediaResponse(200, "OK", common + "\r\nContent-Length: 0", new MemoryStream([], writable: false));
            }

            if (range is null)
            {
                return new MediaResponse(200, "OK", $"{common}\r\nContent-Length: {total}", new SpoolStream(spool, 0, total));
            }

            if (ParseRange(range, total) is not { } wanted)
            {
                spool.Release();
                return new MediaResponse(416, "Range Not Satisfiable", $"{common}\r\nContent-Range: bytes */{total}", null);
            }

            var (start, end) = wanted;
            var length = end - start + 1;
            return new MediaResponse(206, "Partial Content",
                $"{common}\r\nContent-Length: {length}\r\nContent-Range: bytes {start}-{end}/{total}",
                new SpoolStream(spool, start, length));
        }
        catch
        {
            spool.Release();
            throw;
        }
    }

    /// <summary>"bytes=START-END", "bytes=START-" or "bytes=-SUFFIX" (the first range only); null when it can't be served.</summary>
    internal static (long Start, long End)? ParseRange(string header, long total)
    {
        var value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var first = value[6..].Split(',')[0].Trim();
        var dash = first.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var startText = first[..dash].Trim();
        var endText = first[(dash + 1)..].Trim();
        long start, end;
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
            {
                return null;
            }

            start = Math.Max(0, total - suffix);
            end = total - 1;
        }
        else
        {
            if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out start))
            {
                return null;
            }

            end = endText.Length == 0
                ? total - 1
                : long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? Math.Min(e, total - 1) : -1;
        }

        return start < total && end >= start ? (start, end) : null;
    }

    private static long? ParseSize(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            if (cut > 0 && pair[..cut] == "size" &&
                long.TryParse(pair[(cut + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size >= 0)
            {
                return size;
            }
        }

        return null;
    }

    /// <summary>The spool for this video, started if needed, with one reader reference taken.</summary>
    private VideoSpool Open(string deviceId, string assetId, long? size)
    {
        List<VideoSpool> evicted = [];
        VideoSpool spool;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var node = _spools.First;
            while (node is not null && !(node.Value.DeviceId == deviceId && node.Value.AssetId == assetId && !node.Value.Failed))
            {
                node = node.Next;
            }

            if (node is null)
            {
                Directory.CreateDirectory(_folder);
                spool = new VideoSpool(_hub, deviceId, assetId, size, Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".video"));
                _spools.AddFirst(spool);
                spool.Start();
            }
            else
            {
                spool = node.Value;
                _spools.Remove(node);
                _spools.AddFirst(spool);
            }

            spool.Acquire();
            while (_spools.Count > KeepVideos && _spools.Last is { } last)
            {
                _spools.RemoveLast();
                evicted.Add(last.Value);
            }
        }

        foreach (var old in evicted)
        {
            old.Evict();
        }

        return spool;
    }

    public async ValueTask DisposeAsync()
    {
        List<VideoSpool> all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _spools];
            _spools.Clear();
        }

        foreach (var spool in all)
        {
            await spool.EvictAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>One video being (or already) copied into a cache file; readers wait for the bytes they need.</summary>
internal sealed class VideoSpool
{
    private readonly DeviceHub _hub;
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _copy = Task.CompletedTask;
    private long _written;
    private bool _done;
    private Exception? _error;
    private int _readers;
    private bool _evicted;
    private bool _deleted;

    public VideoSpool(DeviceHub hub, string deviceId, string assetId, long? declaredSize, string path)
    {
        _hub = hub;
        DeviceId = deviceId;
        AssetId = assetId;
        DeclaredSize = declaredSize is > 0 ? declaredSize : null;
        _path = path;
    }

    public string DeviceId { get; }
    public string AssetId { get; }
    public long? DeclaredSize { get; }

    public bool Failed
    {
        get
        {
            lock (_gate)
            {
                return _error is not null;
            }
        }
    }

    public void Start() => _copy = Task.Run(CopyAsync);

    public void Acquire()
    {
        lock (_gate)
        {
            _readers++;
        }
    }

    public void Release()
    {
        bool delete;
        lock (_gate)
        {
            _readers--;
            delete = _evicted && _readers == 0;
        }

        if (delete)
        {
            _ = EvictAsync();
        }
    }

    public void Evict() => _ = EvictAsync();

    /// <summary>Stops the copy and deletes the file once no reader holds it.</summary>
    public async Task EvictAsync()
    {
        lock (_gate)
        {
            _evicted = true;
            if (_readers > 0)
            {
                return;
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _copy.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        lock (_gate)
        {
            if (_deleted)
            {
                return;
            }

            _deleted = true;
        }

        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task CopyAsync()
    {
        try
        {
            var device = _hub.GetUsableDevice(DeviceId);
            await using var file = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
            await using var counting = new CountingStream(file, written =>
            {
                lock (_gate)
                {
                    _written = written;
                }

                Signal();
            });
            await device.Photos.ReadOriginalAsync(AssetId, counting, null, _stop.Token).ConfigureAwait(false);
            await counting.FlushAsync(_stop.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _written = counting.Written;
                _done = true;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _error = ex;
            }
        }

        Signal();
    }

    private void Signal()
    {
        TaskCompletionSource fired;
        lock (_gate)
        {
            fired = _progress;
            _progress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        fired.TrySetResult();
    }

    /// <summary>Waits until <paramref name="end"/> bytes are in the file, or the copy has ended. Returns the bytes available.</summary>
    public async Task<long> WaitForAsync(long end, CancellationToken ct)
    {
        while (true)
        {
            Task next;
            lock (_gate)
            {
                if (_error is not null)
                {
                    throw new IOException("The video couldn't be read from the phone.", _error);
                }

                if (_written >= end || _done)
                {
                    return _written;
                }

                next = _progress.Task;
            }

            await next.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<long> WaitForEndAsync(CancellationToken ct) => await WaitForAsync(long.MaxValue, ct).ConfigureAwait(false);

    public async Task<byte[]> ReadHeadAsync(CancellationToken ct)
    {
        var available = await WaitForAsync(16, ct).ConfigureAwait(false);
        var head = new byte[(int)Math.Min(16, available)];
        await ReadAtAsync(0, head, ct).ConfigureAwait(false);
        return head;
    }

    /// <summary>Reads exactly buffer.Length bytes at <paramref name="offset"/> from the cache file (they must be written already).</summary>
    public async Task ReadAtAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        using var handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        var done = 0;
        while (done < buffer.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, buffer[done..], offset + done, ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The cached video is shorter than expected.");
            }

            done += read;
        }
    }
}

/// <summary>A seekable read-only view of [start, start + length) of a spool; reads wait for bytes still on their way.</summary>
internal sealed class SpoolStream(VideoSpool spool, long start, long length) : Stream
{
    private long _position;
    private bool _released;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var want = (int)Math.Min(buffer.Length, length - _position);
        if (want <= 0)
        {
            return 0;
        }

        var from = start + _position;
        var available = await spool.WaitForAsync(from + want, cancellationToken).ConfigureAwait(false);
        var count = (int)Math.Min(want, available - from);
        if (count <= 0)
        {
            throw new EndOfStreamException("The phone sent less of the video than it said it had.");
        }

        await spool.ReadAtAsync(from, buffer[..count], cancellationToken).ConfigureAwait(false);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_released)
        {
            _released = true;
            spool.Release();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Passes writes through and reports the running total after each one.</summary>
internal sealed class CountingStream(Stream inner, Action<long> written) : Stream
{
    public long Written { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        inner.Flush();
        Written += buffer.Length;
        written(Written);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        Written += buffer.Length;
        written(Written);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
