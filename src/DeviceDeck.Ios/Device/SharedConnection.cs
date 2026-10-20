using System.Net.Sockets;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Device;

/// <summary>
/// One long-lived service connection reused by many small calls (thumbnails, icons), so a grid of 50
/// images does not open 50 lockdown sessions. A connection that broke is replaced, and a call that failed
/// on a dead connection is retried once on a fresh one; calls must therefore be idempotent.
/// </summary>
internal sealed class SharedConnection<T>(Func<CancellationToken, Task<T>> open, Func<T, bool> isBroken, bool serialize) : IAsyncDisposable
    where T : class, IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private T? _current;
    private bool _disposed;

    public async Task<TResult> UseAsync<TResult>(Func<T, Task<TResult>> operation, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            T connection;
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_current is not null && isBroken(_current))
                    await DropLockedAsync().ConfigureAwait(false);
                connection = _current ??= await open(ct).ConfigureAwait(false);
            }
            catch
            {
                _lock.Release();
                throw;
            }
            if (!serialize)
                _lock.Release();

            try
            {
                return await operation(connection).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && IsDeadConnection(ex))
            {
                if (serialize)
                    await DropLockedAsync().ConfigureAwait(false);
                else
                    await DropAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                if (serialize)
                    _lock.Release();
            }
        }
    }

    private async Task DropAsync(T connection)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_current, connection))
                await DropLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DropLockedAsync()
    {
        var old = _current;
        _current = null;
        if (old is not null)
        {
            try
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
            }
        }
    }

    private static bool IsDeadConnection(Exception ex) =>
        ex is IOException or EndOfStreamException or ObjectDisposedException or SocketException
        || ex is DeviceException { Code: DeviceErrorCodes.Disconnected };

    public async ValueTask DisposeAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await DropLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}
