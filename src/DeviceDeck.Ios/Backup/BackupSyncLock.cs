using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.Backup;

/// <summary>
/// com.apple.mobile.notification_proxy: posts the sync notifications iTunes posts around a backup and relays the
/// phone's own (the user tapping "Cancel Sync").
/// </summary>
internal sealed class NotificationProxyClient : IAsyncDisposable
{
    public const string SyncWillStart = "com.apple.itunes-mobdev.syncWillStart";
    public const string SyncDidStart = "com.apple.itunes-mobdev.syncDidStart";
    public const string SyncDidFinish = "com.apple.itunes-mobdev.syncDidFinish";
    public const string SyncLockRequest = "com.apple.itunes-mobdev.syncLockRequest";
    public const string SyncCancelRequest = "com.apple.itunes-client.syncCancelRequest";

    private readonly PlistConnection _connection;
    private readonly SemaphoreSlim _send = new(1, 1);
    private Task _listen = Task.CompletedTask;

    private NotificationProxyClient(PlistConnection connection) => _connection = connection;

    /// <summary>Raised on a background thread with each notification the phone relays.</summary>
    public event Action<string>? Notified;

    public static async Task<NotificationProxyClient> StartAsync(DeviceConnector connector, CancellationToken ct) =>
        new(await connector.StartServiceAsync(BackupServices.NotificationProxy, ct).ConfigureAwait(false));

    public Task PostAsync(string name, CancellationToken ct) => SendAsync(new PlistDict { ["Command"] = "PostNotification", ["Name"] = name }, ct);

    public async Task ObserveAsync(string name, CancellationToken ct)
    {
        await SendAsync(new PlistDict { ["Command"] = "ObserveNotification", ["Name"] = name }, ct).ConfigureAwait(false);
        if (_listen.IsCompleted)
            _listen = Task.Run(ListenAsync, CancellationToken.None);
    }

    private async Task SendAsync(PlistDict message, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _connection.SendAsync(message, ct).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (true)
            {
                var message = await _connection.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);
                switch (message.GetString("Command"))
                {
                    case "RelayNotification" when message.GetString("Name") is { } name:
                        Notified?.Invoke(name);
                        break;
                    case "ProxyDeath":
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or FormatException or InvalidOperationException)
        {
            // The connection closed; nothing more will come.
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync(new PlistDict { ["Command"] = "Shutdown" }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        await _listen.ConfigureAwait(false);
        _send.Dispose();
    }
}

/// <summary>
/// The phone's sync lock: an exclusive AFC lock on /com.apple.itunes.lock_sync, held for a whole backup or restore so
/// iTunes, Apple Devices and DeviceDeck don't sync at once. The phone shows "Syncing" while it is held.
/// A phone without the lock file (or without AFC locks) is backed up without it, as libimobiledevice does.
/// </summary>
internal sealed class BackupSyncLock : IAsyncDisposable
{
    public const string LockFile = "/com.apple.itunes.lock_sync";
    private const AfcOperation FileLock = (AfcOperation)0x1B;
    private const ulong LockExclusiveNonBlocking = 2 | 4;
    private const ulong Unlock = 8 | 4;

    private readonly AfcClient? _afc;
    private readonly ulong _handle;
    private readonly NotificationProxyClient? _notifications;

    private BackupSyncLock(AfcClient? afc, ulong handle, NotificationProxyClient? notifications)
    {
        _afc = afc;
        _handle = handle;
        _notifications = notifications;
    }

    public bool Held => _afc is not null;

    /// <param name="waiting">Called while another app holds the lock.</param>
    /// <param name="timeout">How long to wait for another sync to finish before giving up.</param>
    public static async Task<BackupSyncLock> AcquireAsync(DeviceConnector connector, NotificationProxyClient? notifications, Action waiting,
        TimeSpan timeout, CancellationToken ct)
    {
        await PostQuietlyAsync(notifications, NotificationProxyClient.SyncWillStart, ct).ConfigureAwait(false);
        var afc = await connector.StartAfcAsync(ct).ConfigureAwait(false);
        ulong handle;
        try
        {
            handle = await afc.OpenAsync(LockFile, AfcFileMode.ReadWrite, ct).ConfigureAwait(false);
        }
        catch (DeviceException)
        {
            await afc.DisposeAsync().ConfigureAwait(false);
            await PostQuietlyAsync(notifications, NotificationProxyClient.SyncDidStart, ct).ConfigureAwait(false);
            return new BackupSyncLock(null, 0, notifications);
        }

        await PostQuietlyAsync(notifications, NotificationProxyClient.SyncLockRequest, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await afc.RequestAsync(FileLock, [.. AfcPacket.U64(handle), .. AfcPacket.U64(LockExclusiveNonBlocking)], default, LockFile, ct)
                    .ConfigureAwait(false);
                await PostQuietlyAsync(notifications, NotificationProxyClient.SyncDidStart, ct).ConfigureAwait(false);
                return new BackupSyncLock(afc, handle, notifications);
            }
            catch (DeviceException ex) when (ex.Message.Contains(nameof(AfcStatus.OpWouldBlock), StringComparison.Ordinal))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    await CloseAsync(afc, handle).ConfigureAwait(false);
                    throw new DeviceException(BackupErrors.Busy,
                        "Another app is syncing with the iPhone (iTunes, Apple Devices or a sync tool). Let it finish or close it, then try again.");
                }
                waiting();
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported or DeviceErrorCodes.Protocol)
            {
                // No AFC locks on this phone: back up without one.
                await CloseAsync(afc, handle).ConfigureAwait(false);
                await PostQuietlyAsync(notifications, NotificationProxyClient.SyncDidStart, ct).ConfigureAwait(false);
                return new BackupSyncLock(null, 0, notifications);
            }
        }
    }

    private static async Task CloseAsync(AfcClient afc, ulong handle)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await afc.CloseAsync(handle, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        await afc.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task PostQuietlyAsync(NotificationProxyClient? notifications, string name, CancellationToken ct)
    {
        if (notifications is null)
            return;
        try
        {
            await notifications.PostAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or DeviceException or ObjectDisposedException)
        {
            // Notifications are a courtesy to other sync apps; the backup works without them.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_afc is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _afc.RequestAsync(FileLock, [.. AfcPacket.U64(_handle), .. AfcPacket.U64(Unlock)], default, LockFile, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeviceException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Closing the file (or the connection dropping) releases it too.
            }
            await CloseAsync(_afc, _handle).ConfigureAwait(false);
        }
        try
        {
            using var post = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await PostQuietlyAsync(_notifications, NotificationProxyClient.SyncDidFinish, post.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
