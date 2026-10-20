using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Bridge.Toolbox;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The mirror window's bridge against a phone that mirrors (packets in, inputs out, in order).</summary>
public sealed class MirrorBridgeTests : IAsyncLifetime
{
    private const string PhoneId = "mirror-phone";
    private readonly string _root = Directory.CreateTempSubdirectory("devicedeck-mirror-").FullName;
    private readonly MirrorPhone _phone = new();
    private readonly PageTransport _transport = new();
    private DeviceHub _hub = null!;
    private SqliteTransferStore _store = null!;
    private TransferQueue _queue = null!;
    private MirrorBridge _bridge = null!;
    private ToolboxFakes _toolbox = null!;
    private FakeShell _shell = null!;

    public async Task InitializeAsync()
    {
        _hub = new DeviceHub([new OnePhoneProvider(_phone)]) { DriverPollInterval = TimeSpan.Zero };
        await _hub.StartAsync(CancellationToken.None);
        _store = new SqliteTransferStore(Path.Combine(_root, "transfers.db"));
        _queue = new TransferQueue(_hub, _store, TransferHandlers.All(), new CountingKeepAwake());
        _toolbox = new ToolboxFakes(_root);
        _shell = new FakeShell();
        _bridge = new MirrorBridge(_transport, new BridgeServices
        {
            Hub = _hub,
            Transfers = _queue,
            Settings = new SettingsStore(Path.Combine(_root, "settings.json")),
            Shell = _shell,
            Screenshots = new ScreenshotStore(),
            Toolbox = _toolbox.Services,
        });
    }

    public async Task DisposeAsync()
    {
        _bridge.Dispose();
        await _queue.DisposeAsync();
        _store.Dispose();
        await _hub.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<string> StartAsync()
    {
        var result = await _transport.CallAsync("mirror.start", new JsonObject { ["deviceId"] = PhoneId });
        Assert.True(result.ContainsKey("result"), result.ToJsonString());
        Assert.Equal(("Pixel 7", "h264"), ((string?)result["result"]!["deviceName"], (string?)result["result"]!["codec"]));
        return (string)result["result"]!["sessionId"]!;
    }

    [Fact]
    public async Task Video_packets_reach_the_page_as_base64_in_order()
    {
        var session = await StartAsync();
        var running = _phone.Sessions.Single();
        await running.Packets.Writer.WriteAsync(new MirrorPacket(MirrorPacketKind.Session, 0, false, 1080, 2340, ReadOnlyMemory<byte>.Empty));
        await running.Packets.Writer.WriteAsync(new MirrorPacket(MirrorPacketKind.Config, 0, false, 1080, 2340, new byte[] { 0, 0, 0, 1, 0x67 }));
        for (var i = 0; i < 20; i++)
        {
            await running.Packets.Writer.WriteAsync(new MirrorPacket(MirrorPacketKind.Frame, i * 1000, i == 0, 1080, 2340, new byte[] { 0, 0, 0, 1, (byte)i }));
        }

        await Wait.UntilAsync(() => _transport.Named("mirror.video").Count() == 22, "22 video events");
        var events = _transport.Named("mirror.video").Select(e => e["params"]!).ToList();
        Assert.All(events, e => Assert.Equal(session, (string?)e["sessionId"]));
        Assert.Equal(["session", "config", "frame"], events.Take(3).Select(e => (string)e["kind"]!));
        Assert.Equal("", (string?)events[0]["data"]);
        Assert.Equal(Convert.ToBase64String([0, 0, 0, 1, 0x67]), (string?)events[1]["data"]);
        Assert.True((bool)events[2]["key"]!);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (long)i * 1000), events.Skip(2).Select(e => (long)e["pts"]!));
    }

    [Fact]
    public async Task Inputs_sent_as_notifications_arrive_in_order()
    {
        var session = await StartAsync();
        for (var i = 0; i < 100; i++)
        {
            _transport.Notify("mirror.input", new JsonObject
            {
                ["sessionId"] = session,
                ["kind"] = "touch",
                ["action"] = i == 0 ? "down" : "move",
                ["pointerId"] = -2,
                ["x"] = i,
                ["y"] = 2 * i,
                ["width"] = 1080,
                ["height"] = 2340,
                ["pressure"] = 1,
            });
        }

        _transport.Notify("mirror.input", new JsonObject { ["sessionId"] = session, ["kind"] = "key", ["down"] = true, ["keyCode"] = 3 });
        _transport.Notify("mirror.input", new JsonObject { ["sessionId"] = session, ["kind"] = "text", ["text"] = "سلام" });
        _transport.Notify("mirror.input", new JsonObject { ["sessionId"] = session, ["kind"] = "command", ["command"] = "rotate" });
        var running = _phone.Sessions.Single();
        await Wait.UntilAsync(() => running.Inputs.Count == 103, "103 inputs");

        var inputs = running.Inputs.ToArray();
        Assert.Equal(Enumerable.Range(0, 100), inputs.Take(100).Cast<MirrorTouch>().Select(t => t.X));
        Assert.Equal(new MirrorTouch(MirrorTouchAction.Down, MirrorTouch.MouseFinger, 0, 0, 1080, 2340, 1), inputs[0]);
        Assert.Equal(new MirrorKey(true, 3), inputs[100]);
        Assert.Equal(new MirrorText("سلام"), inputs[101]);
        Assert.Equal(new MirrorCommand(MirrorCommandKind.Rotate), inputs[102]);
        Assert.Empty(_transport.Replies); // notifications get no answer

        var bad = await _transport.CallAsync("mirror.input", new JsonObject { ["sessionId"] = session, ["kind"] = "teleport" });
        Assert.Equal(-32602, (int)bad["error"]!["code"]!);
    }

    [Fact]
    public async Task Stop_or_a_reloaded_page_ends_the_session_on_the_phone()
    {
        var first = await StartAsync();
        await _transport.CallAsync("mirror.stop", new JsonObject { ["sessionId"] = first });
        Assert.True(_phone.Sessions[0].Disposed);
        Assert.Equal(0, _bridge.Sessions);

        await StartAsync();
        _bridge.ResetPage();
        await Wait.UntilAsync(() => _phone.Sessions[1].Disposed, "the session to stop with the page");
        Assert.Empty(_transport.Named("mirror.ended")); // the page that asked is gone
    }

    [Fact]
    public async Task A_phone_that_goes_away_ends_the_session_with_the_reason()
    {
        var session = await StartAsync();
        _phone.Sessions.Single().Packets.Writer.Complete(new DeviceException(DeviceErrorCodes.Disconnected, "The mirroring connection to the phone dropped."));
        await Wait.UntilAsync(() => _transport.Named("mirror.ended").Any(), "mirror.ended");
        var ended = _transport.Named("mirror.ended").Single()["params"]!;
        Assert.Equal((session, "device_disconnected"), ((string?)ended["sessionId"], (string?)ended["error"]!["code"]));
        Assert.True(_phone.Sessions.Single().Disposed);
        var late = await _transport.CallAsync("mirror.input", new JsonObject { ["sessionId"] = session, ["kind"] = "key", ["keyCode"] = 4 });
        Assert.Equal("device_disconnected", (string?)late["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Screenshots_are_saved_in_pictures_and_can_be_shown()
    {
        var shot = await _transport.CallAsync("mirror.screenshot", new JsonObject { ["deviceId"] = PhoneId });
        var path = (string)shot["result"]!["path"]!;
        Assert.StartsWith(Path.Combine(_root, "pictures", "Screenshot Pixel 7 "), path, StringComparison.Ordinal);
        Assert.Equal(MirrorPhone.Png, await File.ReadAllBytesAsync(path));
        await _transport.CallAsync("shell.revealInExplorer", new JsonObject { ["path"] = path });
        Assert.Equal([path], _shell.Revealed);
        Assert.NotNull((string?)(await _transport.CallAsync("settings.get", null))["result"]!["theme"]); // the window follows the app's theme
    }

    // ---- fakes ----

    private sealed class PageTransport : IBridgeTransport
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
        private long _nextId;

        public event EventHandler<BridgeMessage>? MessageReceived;

        public ConcurrentQueue<JsonObject> Events { get; } = new();

        public ConcurrentQueue<JsonObject> Replies { get; } = new();

        public void Post(string json)
        {
            var message = JsonNode.Parse(json)!.AsObject();
            if (message["id"] is JsonValue id && _pending.TryRemove(id.GetValue<long>(), out var waiter))
            {
                waiter.TrySetResult(message);
            }
            else if (message.ContainsKey("id"))
            {
                Replies.Enqueue(message);
            }
            else
            {
                Events.Enqueue(message);
            }
        }

        public IEnumerable<JsonObject> Named(string method) => Events.Where(e => (string?)e["method"] == method);

        public Task<JsonObject> CallAsync(string method, JsonObject? parameters)
        {
            var id = Interlocked.Increment(ref _nextId);
            var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiter;
            var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
            MessageReceived?.Invoke(this, BridgeMessage.Text(request.ToJsonString()));
            return waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Notify(string method, JsonObject parameters) =>
            MessageReceived?.Invoke(this, BridgeMessage.Text(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }.ToJsonString()));
    }

    private sealed class OnePhoneProvider(IDevice phone) : IDeviceProvider
    {
        public string Id => "mirror-test";
        public IReadOnlyList<IDevice> Devices => [phone];
        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "1", null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Unused() => DeviceChanged?.Invoke(this, null!);
    }

    private sealed class MirrorPhone : IDevice, IScreenMirrorSource, IDeviceActions
    {
        public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        public List<FakeSession> Sessions { get; } = [];

        public DeviceSummary Summary { get; } = new(PhoneId, "Pixel 7", DevicePlatform.Android, "Pixel 7", "Pixel 7", "14", ConnectionKind.Usb,
            DeviceState.Ready, DeviceMode.Full, DeviceCapability.Info | DeviceCapability.Screenshot | DeviceCapability.ScreenMirror);

        public IPhotoLibrary Photos => throw new NotSupportedException();
        public IAppManager Apps => throw new NotSupportedException();
        public IDeviceActions Actions => this;

        public Task<IScreenMirrorSession> StartMirrorAsync(MirrorOptions options, CancellationToken ct)
        {
            var session = new FakeSession();
            Sessions.Add(session);
            return Task.FromResult<IScreenMirrorSession>(session);
        }

        public Task<byte[]> TakeScreenshotAsync(CancellationToken ct) => Task.FromResult(Png);
        public Task RebootAsync(RebootTarget target, CancellationToken ct) => throw new NotSupportedException();
        public Task ShutdownAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeSession : IScreenMirrorSession
    {
        public Channel<MirrorPacket> Packets { get; } = Channel.CreateUnbounded<MirrorPacket>();
        public ConcurrentQueue<MirrorInput> Inputs { get; } = new();
        public bool Disposed { get; private set; }
        public string DeviceName => "Pixel 7";
        public string Codec => "h264";

        public async IAsyncEnumerable<MirrorPacket> ReadPacketsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var packet in Packets.Reader.ReadAllAsync(ct))
            {
                yield return packet;
            }
        }

        public Task SendAsync(MirrorInput input, CancellationToken ct)
        {
            Inputs.Enqueue(input);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Packets.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
