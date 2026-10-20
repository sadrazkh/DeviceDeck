using DeviceDeck.Android.Parsing;

namespace DeviceDeck.Android.Adb;

/// <summary>
/// Follows host:track-devices-l on our adb server. Every message is the full device list; the callback gets it
/// as is. When the server goes away the callback gets an empty list and the tracker starts the server again.
/// </summary>
internal sealed class DeviceTracker(AdbServerHost host, Func<IReadOnlyList<AdbDeviceEntry>, Task> onSnapshot) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private readonly TaskCompletionSource _firstSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The last failure (server won't start, adb missing), cleared when tracking works again.</summary>
    public Exception? LastError { get; private set; }

    public bool IsTracking { get; private set; }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Completes after the first device list arrived or the first attempt failed.</summary>
    public Task FirstSnapshot => _firstSnapshot.Task;

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await host.EnsureStartedAsync(ct).ConfigureAwait(false);
                await using var connection = await AdbConnection.ConnectAsync(host.EndPoint, ct).ConfigureAwait(false);
                await connection.SendRequestAsync("host:track-devices-l", ct).ConfigureAwait(false);
                while (await connection.ReadLengthPrefixedStringAsync(ct).ConfigureAwait(false) is { } text)
                {
                    LastError = null;
                    IsTracking = true;
                    delay = TimeSpan.FromSeconds(1);
                    await onSnapshot(TrackDevicesParser.Parse(text)).ConfigureAwait(false);
                    _firstSnapshot.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex;
            }

            IsTracking = false;
            _firstSnapshot.TrySetResult();
            try
            {
                await onSnapshot([]).ConfigureAwait(false);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 10));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop != null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _stop.Dispose();
    }
}
