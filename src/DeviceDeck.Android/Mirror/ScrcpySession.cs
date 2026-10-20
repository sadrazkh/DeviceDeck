using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Mirror;

/// <summary>
/// One mirroring session: scrcpy-server pushed and started through a shell, then two sockets to its abstract socket
/// (video first, it sends a dummy byte, the device name and the codec; then control). Disposing closes the sockets
/// and the shell, and the server cleans up and exits.
/// </summary>
internal sealed class ScrcpySession : IScreenMirrorSession
{
    private readonly Stream _video;
    private readonly Stream _control;
    private readonly CancellationTokenSource _life;
    private readonly Task _server;
    private readonly Task _drain;
    private readonly ServerOutput _output;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed;

    private ScrcpySession(Stream video, Stream control, string deviceName, CancellationTokenSource life, Task server, ServerOutput output)
    {
        _video = video;
        _control = control;
        DeviceName = deviceName;
        _life = life;
        _server = server;
        _output = output;
        // Device messages (clipboard, acks) arrive on the control socket; nothing here needs them, but they must be read.
        _drain = Task.Run(() => DrainAsync(control, life.Token));
    }

    public string DeviceName { get; }

    public string Codec => "h264";

    /// <summary>What the server printed (its log), for errors.</summary>
    public string ServerLog => _output.Text;

    /// <summary>How long to keep trying to reach the server's socket after starting it.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public static async Task<ScrcpySession> StartAsync(IAdbChannel channel, IAdbSocketChannel sockets, string serverJar, MirrorOptions options, CancellationToken ct)
    {
        await using (var jar = File.OpenRead(serverJar))
        {
            await channel.PushAsync(jar, ScrcpyProtocol.DevicePath, null, ct).ConfigureAwait(false);
        }

        var scid = Random.Shared.Next(1, int.MaxValue);
        var life = new CancellationTokenSource();
        var output = new ServerOutput();
        var server = Task.Run(() => channel.ShellToStreamAsync(ScrcpyProtocol.ServerCommand(scid, options), output, life.Token), CancellationToken.None);
        Stream? video = null;
        Stream? control = null;
        try
        {
            var service = "localabstract:" + ScrcpyProtocol.SocketName(scid);
            video = await ConnectFirstAsync(sockets, service, server, output, ct).ConfigureAwait(false);
            control = await sockets.OpenSocketAsync(service, ct).ConfigureAwait(false);

            var meta = new byte[ScrcpyProtocol.DeviceNameLength + 4];
            if (!await ReadExactAsync(video, meta, ct).ConfigureAwait(false))
            {
                throw Failed("The phone's mirroring server closed before it started streaming.", output);
            }

            var codec = BinaryPrimitives.ReadUInt32BigEndian(meta.AsSpan(ScrcpyProtocol.DeviceNameLength));
            if (codec != ScrcpyProtocol.CodecH264)
            {
                throw Failed(codec is 0 or 1
                    ? "The phone couldn't start a video encoder for mirroring."
                    : $"The phone's mirroring server sent codec 0x{codec:X8} instead of H.264.", output);
            }

            return new ScrcpySession(video, control, ScrcpyProtocol.DeviceName(meta.AsSpan(0, ScrcpyProtocol.DeviceNameLength)), life, server, output);
        }
        catch
        {
            await life.CancelAsync().ConfigureAwait(false);
            if (video is not null) await video.DisposeAsync().ConfigureAwait(false);
            if (control is not null) await control.DisposeAsync().ConfigureAwait(false);
            await Quietly(server).ConfigureAwait(false);
            life.Dispose();
            throw;
        }
    }

    /// <summary>The first socket: the server sends one dummy byte once it has accepted, so a read tells us it is really there.</summary>
    private static async Task<Stream> ConnectFirstAsync(IAdbSocketChannel sockets, string service, Task<ShellResult> server, ServerOutput output, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ConnectTimeout;
        var dummy = new byte[1];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (server.IsCompleted)
            {
                var exit = server.IsCompletedSuccessfully ? $" (exit code {server.Result.ExitCode})" : "";
                throw Failed("The phone's mirroring server stopped before it was ready" + exit + ".", output, server.Exception?.InnerException);
            }

            Stream? stream = null;
            try
            {
                stream = await sockets.OpenSocketAsync(service, ct).ConfigureAwait(false);
                if (await ReadExactAsync(stream, dummy, ct).ConfigureAwait(false))
                {
                    return stream;
                }
            }
            catch (DeviceException ex) when (ex.Code is not (DeviceErrorCodes.Unauthorized or DeviceErrorCodes.NotFound))
            {
                // Nothing listens yet ("closed"): try again.
            }

            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            if (DateTime.UtcNow > deadline)
            {
                throw Failed("The phone's mirroring server didn't answer in time.", output);
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<MirrorPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        var header = new byte[12];
        int width = 0, height = 0;
        while (true)
        {
            if (!await ReadVideoAsync(header, linked.Token).ConfigureAwait(false))
            {
                yield break;
            }

            var (kind, pts, key, widthOrSize, h) = ScrcpyProtocol.ReadHeader(header);
            if (kind == MirrorPacketKind.Session)
            {
                width = widthOrSize;
                height = h;
                yield return new MirrorPacket(kind, 0, false, width, height, ReadOnlyMemory<byte>.Empty);
                continue;
            }

            if (widthOrSize is <= 0 or > ScrcpyProtocol.MaxPacketBytes)
            {
                throw new DeviceException(DeviceErrorCodes.Protocol, $"The mirroring stream sent a packet of {widthOrSize} bytes.");
            }

            var data = new byte[widthOrSize];
            if (!await ReadVideoAsync(data, linked.Token).ConfigureAwait(false))
            {
                yield break;
            }

            yield return new MirrorPacket(kind, pts, key, width, height, data);
        }
    }

    /// <summary>False when the stream ended or the session is closing; the phone going away mid-packet is device_disconnected.</summary>
    private async Task<bool> ReadVideoAsync(byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await ReadExactAsync(_video, buffer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (Volatile.Read(ref _disposed) == 1 || ct.IsCancellationRequested)
            {
                return false;
            }

            throw new DeviceException(DeviceErrorCodes.Disconnected, "The mirroring connection to the phone dropped.", ex);
        }
    }

    public async Task SendAsync(MirrorInput input, CancellationToken ct)
    {
        var messages = ScrcpyProtocol.Serialize(input);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var message in messages)
            {
                await _control.WriteAsync(message, ct).ConfigureAwait(false);
            }

            await _control.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new DeviceException(DeviceErrorCodes.Disconnected, "The mirroring connection to the phone dropped.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task DrainAsync(Stream control, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (await control.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _life.CancelAsync().ConfigureAwait(false);
        await _video.DisposeAsync().ConfigureAwait(false);
        await _control.DisposeAsync().ConfigureAwait(false);
        await Quietly(_server).ConfigureAwait(false);
        await Quietly(_drain).ConfigureAwait(false);
        _life.Dispose();
        _sendLock.Dispose();
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing: the shell ends with a cancellation or a dropped connection, both expected.
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static DeviceException Failed(string message, ServerOutput output, Exception? inner = null)
    {
        var log = output.Text.Trim();
        // The server prints "ERROR: …" lines; the last one usually says why.
        var reason = log.Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal))?.Trim();
        return new DeviceException(DeviceErrorCodes.Protocol, reason is null ? message : $"{message} {reason}", inner ?? (log.Length > 0 ? new InvalidOperationException(log) : null));
    }

    /// <summary>The server's stdout, last 16 KB kept.</summary>
    private sealed class ServerOutput : Stream
    {
        private const int Keep = 16 * 1024;
        private readonly StringBuilder _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_text)
            {
                _text.Append(Encoding.UTF8.GetString(buffer));
                if (_text.Length > Keep)
                {
                    _text.Remove(0, _text.Length - Keep);
                }
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Finds the scrcpy-server file fetched by tools/fetch-scrcpy-server.ps1.</summary>
internal static class ScrcpyServerFile
{
    public const string EnvironmentVariable = "DEVICEDECK_SCRCPY_SERVER";

    /// <summary>The configured path, DEVICEDECK_SCRCPY_SERVER, "scrcpy/scrcpy-server" next to the app, then third_party/scrcpy in any parent folder.</summary>
    public static string? Locate(string? configured)
    {
        if (!string.IsNullOrEmpty(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return File.Exists(fromEnvironment) ? fromEnvironment : null;
        }

        var shipped = Path.Combine(AppContext.BaseDirectory, "scrcpy", "scrcpy-server");
        if (File.Exists(shipped))
        {
            return shipped;
        }

        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "third_party", "scrcpy", "scrcpy-server");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
