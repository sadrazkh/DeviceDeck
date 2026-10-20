using System.Collections.Concurrent;
using System.Threading.Channels;
using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Bridge.Toolbox;

/// <summary>
/// The bridge of a mirror window's page (web/mirror.html). Methods: app.hello, settings.get, mirror.start,
/// mirror.input (send it as a notification: no reply, strictly in order), mirror.screenshot, mirror.stop,
/// shell.revealInExplorer. Events: mirror.video (base64 H.264 packets), mirror.ended.
/// </summary>
public sealed class MirrorBridge : IDisposable
{
    private readonly IBridgeTransport _transport;
    private readonly BridgeServices _services;
    private readonly ConcurrentDictionary<string, Running> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private CancellationTokenSource _page = new();

    public MirrorBridge(IBridgeTransport transport, BridgeServices services)
    {
        _transport = transport;
        _services = services;
        Dispatcher = new JsonRpcDispatcher();
        Register(Dispatcher);
        transport.MessageReceived += OnMessage;
    }

    public JsonRpcDispatcher Dispatcher { get; }

    /// <summary>Mirroring sessions running now.</summary>
    public int Sessions => _sessions.Count;

    /// <summary>Video quality asked of the phone; the window may ask for less.</summary>
    public MirrorOptions Defaults { get; init; } = new();

    private void Register(JsonRpcDispatcher rpc)
    {
        var hub = _services.Hub;
        rpc.Register("app.hello", _ => Task.FromResult(new HelloResult(_services.Version, IsWebView: true)));
        rpc.Register("settings.get", _ => Task.FromResult(_services.Settings.Current));
        rpc.Register<MirrorStartParams, MirrorStartResult>("mirror.start", async (p, ct) =>
        {
            var device = hub.GetUsableDevice(p.DeviceId);
            if (!device.Summary.Capabilities.HasFlag(DeviceCapability.ScreenMirror) || device is not IScreenMirrorSource source)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't be mirrored.");
            }

            var options = Defaults with
            {
                MaxSize = p.MaxSize > 0 ? Math.Min(p.MaxSize, 4096) : Defaults.MaxSize,
                BitRate = p.BitRate > 0 ? Math.Min(p.BitRate, 40_000_000) : Defaults.BitRate,
                MaxFps = p.MaxFps > 0 ? p.MaxFps : Defaults.MaxFps,
            };
            var session = await source.StartMirrorAsync(options, ct).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N")[..12];
            var running = new Running(id, p.DeviceId, session, CancellationTokenSource.CreateLinkedTokenSource(ct));
            _sessions[id] = running;
            running.Start(Publish, () => _sessions.TryRemove(id, out _));
            return new MirrorStartResult(id, session.DeviceName, session.Codec);
        });
        rpc.Register<MirrorInputParams, Empty>("mirror.input", (p, _) =>
        {
            if (!_sessions.TryGetValue(p.SessionId, out var running))
            {
                throw new DeviceException(DeviceErrorCodes.Disconnected, "Mirroring has stopped.");
            }

            running.Enqueue(ToInput(p));
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<SessionParams, Empty>("mirror.stop", async (p, _) =>
        {
            if (_sessions.TryRemove(p.SessionId, out var running))
            {
                await running.DisposeAsync().ConfigureAwait(false);
            }

            return Empty.Value;
        });
        rpc.Register<DeviceParams, MirrorShotResult>("mirror.screenshot", async (p, ct) =>
        {
            var device = hub.GetUsableDevice(p.DeviceId);
            if (!device.Summary.Capabilities.HasFlag(DeviceCapability.Screenshot))
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't take screenshots.");
            }

            var png = await device.Actions.TakeScreenshotAsync(ct).ConfigureAwait(false);
            var path = ToolboxMethods.FreePath(_services.Toolbox.PicturesFolder, $"Screenshot {device.Summary.Name} {DateTime.Now:yyyy-MM-dd HHmmss}", ".png");
            await File.WriteAllBytesAsync(path, png, ct).ConfigureAwait(false);
            return new MirrorShotResult(path);
        });
        rpc.Register<PathParams, Empty>("shell.revealInExplorer", (p, _) =>
        {
            if (!Path.IsPathFullyQualified(p.Path))
            {
                throw new ArgumentException("path must be a full path on this PC.");
            }

            _services.Shell.RevealInExplorer(p.Path);
            return Task.FromResult(Empty.Value);
        });
    }

    internal static MirrorInput ToInput(MirrorInputParams p) => p.Kind switch
    {
        "touch" => new MirrorTouch(p.Action, p.PointerId, p.X, p.Y, p.Width, p.Height, p.Pressure),
        "key" => new MirrorKey(p.Down, p.KeyCode),
        "text" => new MirrorText(p.Text ?? ""),
        "scroll" => new MirrorScroll(p.X, p.Y, p.Width, p.Height, p.HScroll, p.VScroll),
        "command" => new MirrorCommand(p.Command),
        _ => throw new ArgumentException("kind is touch, key, text, scroll or command."),
    };

    private void Publish(string method, object parameters) => _transport.Post(JsonRpcDispatcher.Notification(method, parameters));

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
            // HandleAsync answers every failure itself.
        }
    }

    /// <summary>The page reloaded or the window is closing: its sessions stop.</summary>
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

    public void Dispose()
    {
        _transport.MessageReceived -= OnMessage;
        lock (_gate)
        {
            _page.Cancel();
        }

        foreach (var running in _sessions.Values)
        {
            _ = running.DisposeAsync().AsTask();
        }

        _sessions.Clear();
    }

    /// <summary>One session: a pump from the phone to the page and an ordered queue of inputs to the phone.</summary>
    private sealed class Running(string id, string deviceId, IScreenMirrorSession session, CancellationTokenSource life) : IAsyncDisposable
    {
        private readonly Channel<MirrorInput> _inputs = Channel.CreateUnbounded<MirrorInput>(new UnboundedChannelOptions { SingleReader = true });
        private Task _pump = Task.CompletedTask;
        private Task _sender = Task.CompletedTask;
        private int _disposed;

        public string DeviceId { get; } = deviceId;

        public void Enqueue(MirrorInput input) => _inputs.Writer.TryWrite(input);

        public void Start(Action<string, object> publish, Action onEnded)
        {
            var ct = life.Token;
            _sender = Task.Run(async () =>
            {
                try
                {
                    await foreach (var input in _inputs.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        await session.SendAsync(input, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or DeviceException)
                {
                    // The pump reports how the session ended.
                }
            });
            _pump = Task.Run(async () =>
            {
                ToolboxError? error = null;
                try
                {
                    await foreach (var packet in session.ReadPacketsAsync(ct).ConfigureAwait(false))
                    {
                        publish("mirror.video", new MirrorVideoEvent(id, packet.Kind, packet.Pts, packet.KeyFrame, packet.Width, packet.Height,
                            packet.Data.IsEmpty ? "" : Convert.ToBase64String(packet.Data.Span)));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
                catch (DeviceException ex)
                {
                    error = new ToolboxError(ex.Code, ex.Message);
                }
                catch (Exception ex)
                {
                    error = new ToolboxError("internal", ex.Message);
                }

                onEnded();
                if (Volatile.Read(ref _disposed) == 0)
                {
                    publish("mirror.ended", new MirrorEndedEvent(id, error));
                }

                await DisposeAsync().ConfigureAwait(false);
            });
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _inputs.Writer.TryComplete();
            await life.CancelAsync().ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _sender.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            life.Dispose();
        }
    }
}
