using System.Buffers;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Protocol.Afc;

/// <summary>
/// Apple File Conduit over one service socket (com.apple.afc, or house_arrest after VendDocuments).
/// Requests are strictly one at a time; the lock makes concurrent callers queue.
/// </summary>
public sealed class AfcClient(Stream stream) : IAsyncDisposable
{
    /// <summary>Bytes asked for per FileRead.</summary>
    public const int ReadChunk = 1024 * 1024;

    /// <summary>Bytes sent per FileWrite.</summary>
    public const int WriteChunk = 1024 * 1024;

    /// <summary>Largest reply we accept; a ReadChunk plus slack.</summary>
    private const ulong MaxPacket = 8 * 1024 * 1024;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly byte[] _replyHeader = new byte[AfcPacket.HeaderSize];
    private ulong _packetNumber;
    private bool _broken;

    /// <summary>True once a request was cut off half-way; the connection must be replaced.</summary>
    public bool IsBroken => _broken;

    public async Task<IReadOnlyList<string>> ReadDirectoryAsync(string path, CancellationToken ct)
    {
        var reply = await RequestAsync(AfcOperation.ReadDir, AfcPacket.Path(path), default, path, ct).ConfigureAwait(false);
        return AfcPacket.SplitStrings(DataOf(reply)).Where(n => n is not ("." or ".." or "")).ToList();
    }

    /// <summary>Null when the path does not exist.</summary>
    public async Task<AfcFileInfo?> GetFileInfoAsync(string path, CancellationToken ct)
    {
        try
        {
            var reply = await RequestAsync(AfcOperation.GetFileInfo, AfcPacket.Path(path), default, path, ct).ConfigureAwait(false);
            return AfcFileInfo.FromPairs(AfcPacket.ParsePairs(DataOf(reply)));
        }
        catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.PathNotFound)
        {
            return null;
        }
    }

    /// <summary>Model, FSTotalBytes, FSFreeBytes, FSBlockSize.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetDeviceInfoAsync(CancellationToken ct)
    {
        var reply = await RequestAsync(AfcOperation.GetDeviceInfo, [], default, null, ct).ConfigureAwait(false);
        return AfcPacket.ParsePairs(DataOf(reply));
    }

    public Task MakeDirectoryAsync(string path, CancellationToken ct) =>
        RequestAsync(AfcOperation.MakeDir, AfcPacket.Path(path), default, path, ct);

    public Task RemovePathAsync(string path, CancellationToken ct) =>
        RequestAsync(AfcOperation.RemovePath, AfcPacket.Path(path), default, path, ct);

    /// <summary>Recursive delete in one call (iOS 8+).</summary>
    public Task RemovePathAndContentsAsync(string path, CancellationToken ct) =>
        RequestAsync(AfcOperation.RemovePathAndContents, AfcPacket.Path(path), default, path, ct);

    public Task RenamePathAsync(string from, string to, CancellationToken ct) =>
        RequestAsync(AfcOperation.RenamePath, [.. AfcPacket.Path(from), .. AfcPacket.Path(to)], default, from, ct);

    public async Task<ulong> OpenAsync(string path, AfcFileMode mode, CancellationToken ct)
    {
        var reply = await RequestAsync(AfcOperation.FileOpen, [.. AfcPacket.U64((ulong)mode), .. AfcPacket.Path(path)], default, path, ct)
            .ConfigureAwait(false);
        var content = reply.Content;
        if (reply.Operation != AfcOperation.FileOpenResult || content.Length < 8)
            throw new FormatException($"AFC FileOpen answered with {reply.Operation}.");
        return BitConverter.ToUInt64(content, 0);
    }

    /// <summary>Up to <paramref name="count"/> bytes; an empty array means end of file.</summary>
    public async Task<byte[]> ReadAsync(ulong handle, int count, CancellationToken ct)
    {
        var reply = await RequestAsync(AfcOperation.FileRead, [.. AfcPacket.U64(handle), .. AfcPacket.U64((ulong)count)], default, null, ct)
            .ConfigureAwait(false);
        return DataOf(reply);
    }

    public Task WriteAsync(ulong handle, ReadOnlyMemory<byte> data, CancellationToken ct) =>
        RequestAsync(AfcOperation.FileWrite, AfcPacket.U64(handle), data, null, ct);

    public Task CloseAsync(ulong handle, CancellationToken ct) =>
        RequestAsync(AfcOperation.FileClose, AfcPacket.U64(handle), default, null, ct);

    /// <summary>
    /// Streams a file off the phone into one pooled buffer. Progress reports bytes copied so far. With
    /// <paramref name="readAhead"/> above 1 that many FileReads are kept in flight, so the phone is already sending the
    /// next chunk while this one is written to disk; the replies must come back in order (checked by packet number).
    /// </summary>
    public async Task ReadFileAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct, int readAhead = 1)
    {
        var handle = await OpenAsync(path, AfcFileMode.ReadOnly, ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunk);
        try
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_broken)
                    throw new DeviceException(DeviceErrorCodes.Disconnected, "The file service connection was interrupted; open it again.");
                await StreamLockedAsync(handle, destination, buffer, progress, Math.Clamp(readAhead, 1, 8), ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await CloseQuietlyAsync(handle).ConfigureAwait(false);
        }
    }

    private async Task StreamLockedAsync(ulong handle, Stream destination, byte[] buffer, IProgress<long>? progress, int readAhead, CancellationToken ct)
    {
        var pending = new Queue<ulong>(readAhead);
        DeviceException? failure = null;
        try
        {
            for (var i = 0; i < readAhead; i++)
                await SendReadAsync(handle, pending, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);

            long done = 0;
            var ended = false;
            while (pending.TryDequeue(out var expected))
            {
                var (op, number, length) = await ReceiveIntoAsync(buffer, ct).ConfigureAwait(false);
                if (number != expected)
                    throw new FormatException($"AFC answered packet {number} where {expected} was due.");
                if (op == AfcOperation.Status)
                {
                    // Success here is "nothing more"; an error after the end is only the read-ahead running past it.
                    var status = length >= 8 ? (AfcStatus)BitConverter.ToUInt64(buffer, 0) : AfcStatus.Success;
                    if (status != AfcStatus.Success && !ended)
                        failure ??= AfcPacket.ErrorFor(status, AfcOperation.FileRead, null);
                    ended = true;
                    continue;
                }
                if (op != AfcOperation.Data)
                    throw new FormatException($"AFC FileRead answered with {op}.");
                if (ended || length == 0)
                {
                    ended = true;
                    continue;
                }

                if (failure is null)
                {
                    // Keep the pipe full while this chunk goes to disk.
                    await SendReadAsync(handle, pending, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                await destination.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                done += length;
                progress?.Report(done);
            }
        }
        catch
        {
            // Replies still on their way would be read as answers to the next request.
            _broken = true;
            throw;
        }
        if (failure is not null)
            throw failure;
    }

    private async Task SendReadAsync(ulong handle, Queue<ulong> pending, CancellationToken ct)
    {
        var number = _packetNumber++;
        await stream.WriteAsync(AfcPacket.EncodeHeader(AfcOperation.FileRead, number, [.. AfcPacket.U64(handle), .. AfcPacket.U64(ReadChunk)], 0), ct)
            .ConfigureAwait(false);
        pending.Enqueue(number);
    }

    /// <summary>Reads one reply's content (header data, then payload) into <paramref name="buffer"/>.</summary>
    private async Task<(AfcOperation Operation, ulong PacketNumber, int Length)> ReceiveIntoAsync(byte[] buffer, CancellationToken ct)
    {
        await stream.ReadExactlyAsync(_replyHeader, ct).ConfigureAwait(false);
        var (entire, thisLength, number, op) = AfcPacket.DecodeHeader(_replyHeader);
        var length = entire - AfcPacket.HeaderSize;
        if (entire > MaxPacket || length > (ulong)buffer.Length)
            throw new FormatException($"AFC reply of {entire} bytes is too large.");
        await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)length), ct).ConfigureAwait(false);
        return (op, number, (int)length);
    }

    /// <summary>Streams <paramref name="length"/> bytes onto the phone, replacing the file.</summary>
    public async Task WriteFileAsync(string path, Stream source, long length, IProgress<long>? progress, CancellationToken ct)
    {
        var handle = await OpenAsync(path, AfcFileMode.WriteOnlyCreateTruncate, ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(WriteChunk);
        try
        {
            long done = 0;
            while (done < length)
            {
                var want = (int)Math.Min(WriteChunk, length - done);
                var read = await source.ReadAtLeastAsync(buffer.AsMemory(0, want), want, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException($"The source ended after {done} of {length} bytes.");
                await WriteAsync(handle, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                progress?.Report(done);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            await CloseQuietlyAsync(handle).ConfigureAwait(false);
        }
    }

    private async Task CloseQuietlyAsync(ulong handle)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await CloseAsync(handle, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The handle dies with the connection anyway.
        }
    }

    /// <summary>Sends one request and returns the reply; a non-zero STATUS reply throws.</summary>
    public async Task<AfcPacket> RequestAsync(AfcOperation op, byte[] headerData, ReadOnlyMemory<byte> payload, string? path, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_broken)
                throw new DeviceException(DeviceErrorCodes.Disconnected, "The file service connection was interrupted; open it again.");
            AfcPacket reply;
            try
            {
                var header = AfcPacket.EncodeHeader(op, _packetNumber++, headerData, payload.Length);
                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                if (!payload.IsEmpty)
                    await stream.WriteAsync(payload, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                reply = await ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // A half-sent request or half-read reply leaves the stream out of step for good.
                _broken = true;
                throw;
            }
            if (reply.Operation == AfcOperation.Status)
            {
                var content = reply.Content;
                var status = content.Length >= 8 ? (AfcStatus)BitConverter.ToUInt64(content, 0) : AfcStatus.Success;
                if (status != AfcStatus.Success)
                    throw AfcPacket.ErrorFor(status, op, path);
            }
            return reply;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A DATA reply's bytes; a bare success STATUS means "nothing".</summary>
    private static byte[] DataOf(AfcPacket reply) => reply.Operation == AfcOperation.Status ? [] : reply.Content;

    private async Task<AfcPacket> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[AfcPacket.HeaderSize];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var (entire, thisLength, number, op) = AfcPacket.DecodeHeader(header);
        if (entire > MaxPacket)
            throw new FormatException($"AFC reply of {entire} bytes is too large.");
        var headerData = new byte[thisLength - AfcPacket.HeaderSize];
        await stream.ReadExactlyAsync(headerData, ct).ConfigureAwait(false);
        var payload = new byte[entire - thisLength];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new AfcPacket(op, number, headerData, payload);
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }
}
