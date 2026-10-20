using System.Net;
using System.Text;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Adb;

/// <summary>
/// The real <see cref="IAdbChannel"/>: shell v2 and exec through our own smart-socket client (exit codes and
/// error text survive), push/pull through AdvancedSharpAdbClient's sync service.
/// </summary>
internal sealed class AdbChannel(EndPoint endPoint, AdbDeviceRef device) : IAdbChannel, Mirror.IAdbSocketChannel
{
    // Shell v2 packet ids (system/core/adb/shell_protocol.h).
    private const byte IdStdout = 1;
    private const byte IdStderr = 2;
    private const byte IdExit = 3;
    private const byte IdCloseStdin = 4;

    public string Serial => device.Serial;

    public async Task<ShellResult> ShellAsync(string command, CancellationToken ct)
    {
        using var stdout = new MemoryStream();
        var result = await ShellToStreamAsync(command, stdout, ct).ConfigureAwait(false);
        return result with { Output = Encoding.UTF8.GetString(stdout.GetBuffer(), 0, (int)stdout.Length) };
    }

    public Task<ShellResult> ShellToStreamAsync(string command, Stream stdout, CancellationToken ct) => GuardAsync(async () =>
    {
        await using var connection = await OpenServiceAsync("shell,v2,raw:" + command, ct).ConfigureAwait(false);
        // Close stdin at once so a command that reads it sees EOF instead of hanging.
        await connection.Stream.WriteAsync(new byte[] { IdCloseStdin, 0, 0, 0, 0 }, ct).ConfigureAwait(false);

        using var stderr = new MemoryStream();
        byte[] header = new byte[5];
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            if (!await connection.ReadExactOrEndAsync(header, ct).ConfigureAwait(false))
            {
                throw new AdbConnectionLostException("The phone closed the shell without an exit status; it may have been unplugged.");
            }
            byte id = header[0];
            long remaining = AdbConnection.ReadUInt32(header.AsSpan(1));
            if (id == IdExit)
            {
                byte[] code = new byte[Math.Max(1, (int)remaining)];
                await connection.ReadExactOrEndAsync(code, ct).ConfigureAwait(false);
                return new ShellResult(code[0], "", Encoding.UTF8.GetString(stderr.GetBuffer(), 0, (int)stderr.Length));
            }
            Stream? target = id switch { IdStdout => stdout, IdStderr => stderr, _ => null };
            while (remaining > 0)
            {
                var chunk = buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length));
                if (!await connection.ReadExactOrEndAsync(chunk, ct).ConfigureAwait(false))
                {
                    throw new AdbConnectionLostException("The shell stream ended mid-packet.");
                }
                if (target != null)
                {
                    await target.WriteAsync(chunk, ct).ConfigureAwait(false);
                }
                remaining -= chunk.Length;
            }
        }
    });

    public Task<string> ExecAsync(string command, Stream? input, IProgress<long>? progress, CancellationToken ct) => GuardAsync(async () =>
    {
        await using var connection = await OpenServiceAsync("exec:" + command, ct).ConfigureAwait(false);
        if (input != null)
        {
            byte[] buffer = new byte[64 * 1024];
            long sent = 0;
            int read;
            try
            {
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await connection.Stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sent += read;
                    progress?.Report(sent);
                }
            }
            catch (IOException)
            {
                // The phone stopped reading early (pm rejected the APK); its answer is still in the stream.
            }
        }
        using var output = new MemoryStream();
        try
        {
            await connection.Stream.CopyToAsync(output, ct).ConfigureAwait(false);
        }
        catch (IOException) when (output.Length > 0)
        {
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    });

    public Task<IAdbSyncSession> OpenSyncAsync(CancellationToken ct) => GuardAsync<IAdbSyncSession>(async () =>
    {
        var data = new DeviceData
        {
            Serial = device.Serial,
            TransportId = device.TransportId?.ToString() ?? "",
            State = AdvancedSharpAdbClient.Models.DeviceState.Online,
        };
        // The constructor connects and sends "sync:" synchronously.
        var service = await Task.Run(() => new SyncService(endPoint, data), ct).ConfigureAwait(false);
        return new SyncSession(service, device.Serial);
    });

    public Task RebootAsync(string target, CancellationToken ct) => GuardAsync(async () =>
    {
        await using var connection = await OpenServiceAsync("reboot:" + target, ct).ConfigureAwait(false);
        return true;
    });

    /// <summary>A raw device socket ("localabstract:name") on a connection of its own; dispose the stream to close it.</summary>
    public Task<Stream> OpenSocketAsync(string service, CancellationToken ct) => GuardAsync<Stream>(async () =>
        (await OpenServiceAsync(service, ct).ConfigureAwait(false)).Stream);

    private async Task<AdbConnection> OpenServiceAsync(string service, CancellationToken ct)
    {
        var connection = await AdbConnection.ConnectAsync(endPoint, ct).ConfigureAwait(false);
        try
        {
            await connection.SelectDeviceAsync(device, ct).ConfigureAwait(false);
            await connection.SendRequestAsync(service, ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not DeviceException and not OperationCanceledException)
        {
            throw AdbErrors.Map(ex, device.Serial);
        }
    }

    private sealed class SyncSession(SyncService service, string serial) : IAdbSyncSession
    {
        // A sync connection carries one request at a time.
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task PullAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await service.PullAsync(remotePath, destination,
                    progress == null ? null : e => progress.Report((long)e.ReceivedBytesSize),
                    useV2: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not DeviceException and not OperationCanceledException)
            {
                throw AdbErrors.Map(ex, serial, remotePath);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task PushAsync(Stream source, string remotePath, DateTimeOffset modified, IProgress<long>? progress, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Regular file, rw-r--r--; FUSE on /sdcard applies its own modes anyway.
                var mode = UnixFileStatus.Regular | (UnixFileStatus)0x1A4;
                await service.PushAsync(source, remotePath, mode, modified,
                    progress == null ? null : e => progress.Report((long)e.ReceivedBytesSize),
                    useV2: false, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not DeviceException and not OperationCanceledException)
            {
                throw AdbErrors.Map(ex, serial, remotePath);
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            service.Dispose();
            _gate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
