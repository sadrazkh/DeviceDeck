using System.Net.Sockets;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Protocol.Afc;

/// <summary>
/// A few AFC connections to the Media folder kept open for big reads (photo originals, stats in bulk), so a backup of
/// thousands of photos opens a handful of lockdown sessions instead of one per file. Up to <see cref="MaxConnections"/>
/// are in use at once and further callers wait their turn. A broken connection is dropped; idle ones close after
/// <see cref="IdleTimeout"/>.
/// </summary>
internal sealed class AfcConnectionPool : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<AfcClient>> _open;
    private readonly SemaphoreSlim _slots;
    private readonly Lock _gate = new();
    private readonly Stack<(AfcClient Client, long ReturnedAt)> _idle = new();
    private readonly TimeProvider _time;
    private ITimer? _trim;
    private int _opened;
    private bool _disposed;

    public AfcConnectionPool(Func<CancellationToken, Task<AfcClient>> open, int maxConnections = 4, TimeSpan? idleTimeout = null, TimeProvider? time = null)
    {
        _open = open;
        MaxConnections = maxConnections;
        IdleTimeout = idleTimeout ?? TimeSpan.FromSeconds(20);
        _time = time ?? TimeProvider.System;
        _slots = new SemaphoreSlim(maxConnections, maxConnections);
    }

    public int MaxConnections { get; }

    public TimeSpan IdleTimeout { get; }

    /// <summary>Connections opened so far (each one is a lockdown session plus a service connection).</summary>
    public int Opened => Volatile.Read(ref _opened);

    /// <summary>Connections open and waiting for work.</summary>
    public int Idle
    {
        get
        {
            lock (_gate)
                return _idle.Count;
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a pooled connection. A connection that went stale while it waited (the phone
    /// closed it) fails on its first request; when <paramref name="started"/> says nothing was done yet, the operation
    /// runs once more on a new connection.
    /// </summary>
    public async Task<T> UseAsync<T>(Func<AfcClient, Task<T>> operation, Func<bool>? started, CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var (client, reused) = await RentAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await operation(client).ConfigureAwait(false);
                    Return(client);
                    return result;
                }
                catch (Exception ex) when (attempt == 0 && reused && !ct.IsCancellationRequested && IsDeadConnection(ex) && started?.Invoke() != true)
                {
                    await DisposeQuietlyAsync(client).ConfigureAwait(false);
                }
                catch
                {
                    if (client.IsBroken)
                        await DisposeQuietlyAsync(client).ConfigureAwait(false);
                    else
                        Return(client);
                    throw;
                }
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<(AfcClient Client, bool Reused)> RentAsync(CancellationToken ct)
    {
        var stale = new List<AfcClient>();
        AfcClient? found = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (found is null && _idle.TryPop(out var idle))
            {
                if (idle.Client.IsBroken)
                    stale.Add(idle.Client);
                else
                    found = idle.Client;
            }
        }
        foreach (var client in stale)
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
        if (found is not null)
            return (found, true);

        var opened = await _open(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _opened);
        return (opened, false);
    }

    private void Return(AfcClient client)
    {
        lock (_gate)
        {
            if (!_disposed && !client.IsBroken)
            {
                _idle.Push((client, _time.GetTimestamp()));
                _trim ??= _time.CreateTimer(_ => _ = TrimAsync(), null, IdleTimeout / 2, IdleTimeout / 2);
                return;
            }
        }
        _ = DisposeQuietlyAsync(client);
    }

    /// <summary>Closes connections that waited longer than <see cref="IdleTimeout"/>.</summary>
    internal async Task TrimAsync()
    {
        List<AfcClient> expired;
        lock (_gate)
        {
            var keep = _idle.Where(i => _time.GetElapsedTime(i.ReturnedAt) < IdleTimeout).Reverse().ToList();
            expired = _idle.Where(i => _time.GetElapsedTime(i.ReturnedAt) >= IdleTimeout).Select(i => i.Client).ToList();
            _idle.Clear();
            foreach (var item in keep)
                _idle.Push(item);
        }
        foreach (var client in expired)
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
    }

    internal static bool IsDeadConnection(Exception ex) =>
        ex is IOException or EndOfStreamException or ObjectDisposedException or SocketException
        || ex is DeviceException { Code: DeviceErrorCodes.Disconnected };

    private static async Task DisposeQuietlyAsync(AfcClient client)
    {
        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<AfcClient> idle;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            idle = _idle.Select(i => i.Client).ToList();
            _idle.Clear();
            _trim?.Dispose();
        }
        foreach (var client in idle)
            await DisposeQuietlyAsync(client).ConfigureAwait(false);
    }
}
