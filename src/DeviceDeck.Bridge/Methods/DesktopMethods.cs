using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Bridge.Pc;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Notifications;

namespace DeviceDeck.Bridge.Methods;

// Round 2, desktop: device.rename, transfers.retry / clear, notifications.show, pc.*, shell.dragOut / openFile,
// app.updateStatus / checkForUpdates / applyUpdate (docs/bridge-contract.md, "Desktop").

public sealed record DeviceRenameParams(string DeviceId, string Name);

public sealed record TransfersClearParams(IReadOnlyList<string>? JobIds = null);

public sealed record ClearedResult(int Cleared);

public sealed record NotificationButtonParams(string Label, string Command);

public sealed record NotificationShowParams(
    string Title,
    string? Body = null,
    string? Tag = null,
    string? DeviceId = null,
    string? Command = null,
    IReadOnlyList<NotificationButtonParams>? Buttons = null,
    IReadOnlyDictionary<string, string>? Args = null);

public sealed record PcListParams(string Path, bool ShowHidden = false);

public sealed record PcRenameParams(string Path, string Name);

public static class DesktopMethods
{
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        var hub = s.Hub;

        rpc.Register<DeviceRenameParams, DeviceSummary>("device.rename", async (p, ct) =>
        {
            var device = hub.GetUsableDevice(p.DeviceId);
            if (device is not IDeviceRenamer renamer || (device.Summary.Capabilities & DeviceCapability.Rename) == 0)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't be renamed from this PC.");
            }

            await renamer.RenameAsync(DeviceNames.Validate(p.Name), ct).ConfigureAwait(false);
            return device.Summary;
        });

        rpc.Register<JobParams, Empty>("transfers.retry", (p, _) =>
        {
            s.Transfers.Retry(p.JobId);
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<TransfersClearParams, ClearedResult>("transfers.clear", (p, _) =>
            Task.FromResult(new ClearedResult(s.Transfers.Clear(p.JobIds))));

        rpc.Register<NotificationShowParams, Empty>("notifications.show", (p, _) =>
        {
            if (string.IsNullOrWhiteSpace(p.Title))
            {
                throw new ArgumentException("title is required.");
            }

            if (p.Buttons is { Count: > 3 })
            {
                throw new ArgumentException("A notification has at most three buttons.");
            }

            s.Notifications.Show(new AppNotification(p.Title, p.Body)
            {
                Tag = p.Tag,
                DeviceId = p.DeviceId,
                Command = string.IsNullOrEmpty(p.Command) ? NotificationCommands.Open : p.Command,
                Buttons = (p.Buttons ?? []).Select(b => new NotificationButton(b.Label, b.Command)).ToArray(),
                Args = p.Args ?? new Dictionary<string, string>(),
            });
            return Task.FromResult(Empty.Value);
        });

        // This PC
        rpc.Register("pc.roots", _ => Task.FromResult(PcFolders.Roots()));
        rpc.Register<PcListParams, IReadOnlyList<PcEntry>>("pc.list", (p, ct) => Task.Run(() => PcFolders.List(p.Path, p.ShowHidden), ct));
        rpc.Register<PathParams, PcEntry>("pc.mkdir", (p, _) => Task.FromResult(PcFolders.Mkdir(p.Path)));
        rpc.Register<PcRenameParams, PcEntry>("pc.rename", (p, _) => Task.FromResult(PcFolders.Rename(p.Path, p.Name)));
        rpc.Register<PathParams, Empty>("shell.openFile", (p, _) =>
        {
            var path = PcFolders.RequireFullPath(p.Path);
            if (!File.Exists(path))
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That file isn't on this PC any more.");
            }

            RequireDesktop(s).OpenFile(path);
            return Task.FromResult(Empty.Value);
        });

        rpc.Register<DragOutParams, DragOutResult>("shell.dragOut", (p, ct) =>
        {
            switch (p.Source)
            {
                case "photos" when !string.IsNullOrEmpty(p.DeviceId) && p.Items is { Count: > 0 } && p.Items.All(i => !i.IsDirectory):
                case "files" when !string.IsNullOrEmpty(p.DeviceId) && !string.IsNullOrEmpty(p.Root) && (p.Items is { Count: > 0 } || p.Paths is { Count: > 0 }):
                case "pc" when p.Paths is { Count: > 0 } && p.Paths.All(Path.IsPathFullyQualified):
                    return RequireDesktop(s).DragOutAsync(p, ct);
                default:
                    throw new ArgumentException("source must be photos (deviceId, items), files (deviceId, root, items or paths) or pc (full paths).");
            }
        });

        // Updates
        rpc.Register("app.updateStatus", _ => Task.FromResult(RequireDesktop(s).Updates.Status));
        rpc.Register("app.checkForUpdates", ct => RequireDesktop(s).Updates.CheckAsync(ct));
        rpc.Register("app.applyUpdate", _ =>
        {
            RequireDesktop(s).Updates.ApplyAndRestart();
            return Task.FromResult(Empty.Value);
        });
    }

    private static IDesktopShell RequireDesktop(BridgeServices s) =>
        s.Desktop ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "This needs the DeviceDeck window.");
}

/// <summary>Pushes notification.activated and app.updateChanged to the page.</summary>
internal sealed class DesktopEvents : IDisposable
{
    private readonly BridgeServices _services;
    private readonly Action<string, object> _publish;

    public DesktopEvents(BridgeServices services, Action<string, object> publish)
    {
        _services = services;
        _publish = publish;
        services.Notifications.Activated += OnActivated;
        if (services.Desktop is { } desktop)
        {
            desktop.Updates.Changed += OnUpdateChanged;
        }
    }

    private void OnActivated(object? sender, NotificationActivation activation) => _publish("notification.activated", activation);

    private void OnUpdateChanged(object? sender, UpdateStatus status) => _publish("app.updateChanged", status);

    public void Dispose()
    {
        _services.Notifications.Activated -= OnActivated;
        if (_services.Desktop is { } desktop)
        {
            desktop.Updates.Changed -= OnUpdateChanged;
        }
    }
}
