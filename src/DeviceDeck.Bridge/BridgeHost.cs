using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Extensions;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge;

/// <summary>
/// Joins a transport to the dispatcher and pushes the contract's events to the page:
/// device.added / changed / removed, drivers.changed, transfer.updated, photos.indexProgress.
/// </summary>
public sealed class BridgeHost : IDisposable
{
    private readonly IBridgeTransport _transport;
    private readonly BridgeServices _services;
    private readonly HashSet<IPhotoIndexProgressSource> _indexSources = [];
    private readonly Lock _gate = new();
    private readonly IDisposable[] _featureEvents;
    private CancellationTokenSource _page = new();

    public BridgeHost(IBridgeTransport transport, BridgeServices services)
    {
        _transport = transport;
        _services = services;
        Dispatcher = BridgeMethods.Register(new JsonRpcDispatcher(), services);

        transport.MessageReceived += OnMessage;
        services.Hub.DeviceChanged += OnDeviceChanged;
        services.Hub.DriversChanged += OnDriversChanged;
        services.Transfers.JobUpdated += OnJobUpdated;
        if (services.Backups is { } backups)
            backups.JobUpdated += OnBackupUpdated;
        _featureEvents = [CleanupMethods.PublishEvents(services, Publish), BatteryMethods.PublishEvents(services, Publish)];
        foreach (var summary in services.Hub.ListDevices())
        {
            WatchIndexing(summary.Id);
        }
    }

    public JsonRpcDispatcher Dispatcher { get; }

    /// <summary>The page navigated or reloaded: calls still running for the old page are cancelled.</summary>
    public void ResetPage()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            old = _page;
            _page = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }

    public void Publish(string method, object parameters) => _transport.Post(JsonRpcDispatcher.Notification(method, parameters));

    private async void OnMessage(object? sender, BridgeMessage message)
    {
        CancellationToken ct;
        lock (_gate)
        {
            ct = _page.Token;
        }

        try
        {
            var reply = await Dispatcher.HandleAsync(message.Json, message.FilePaths, ct).ConfigureAwait(false);
            if (reply is not null && !ct.IsCancellationRequested)
            {
                _transport.Post(reply);
            }
        }
        catch (Exception)
        {
            // HandleAsync answers every failure itself; a transport that is gone is not worth crashing for.
        }
    }

    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e)
    {
        switch (e.Change)
        {
            case DeviceChange.Added:
                WatchIndexing(e.Summary.Id);
                Publish("device.added", e.Summary);
                break;
            case DeviceChange.Changed:
                Publish("device.changed", e.Summary);
                break;
            case DeviceChange.Removed:
                Publish("device.removed", new DeviceRemovedEvent(e.Summary.Id));
                break;
        }
    }

    private void OnDriversChanged(object? sender, IReadOnlyList<DriverStatus> drivers) => Publish("drivers.changed", drivers);

    private void OnJobUpdated(object? sender, TransferJob job) => Publish("transfer.updated", job);

    private void OnBackupUpdated(object? sender, Core.Backups.BackupJob job) => Publish("backup.updated", job);

    private void WatchIndexing(string deviceId)
    {
        if (!_services.Hub.TryGetDevice(deviceId, out var device) || device.Photos is not IPhotoIndexProgressSource source)
        {
            return;
        }

        lock (_gate)
        {
            if (!_indexSources.Add(source))
            {
                return;
            }
        }

        source.IndexProgress += (_, progress) => Publish("photos.indexProgress", new IndexProgressEvent(deviceId, progress.Done, progress.Total));
    }

    public void Dispose()
    {
        _transport.MessageReceived -= OnMessage;
        _services.Hub.DeviceChanged -= OnDeviceChanged;
        _services.Hub.DriversChanged -= OnDriversChanged;
        _services.Transfers.JobUpdated -= OnJobUpdated;
        if (_services.Backups is { } backups)
            backups.JobUpdated -= OnBackupUpdated;
        foreach (var subscription in _featureEvents)
        {
            subscription.Dispose();
        }

        lock (_gate)
        {
            _page.Cancel();
            _page.Dispose();
        }
    }
}
