using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>A whole host (fake phones, hub, queue, settings, bridge) in a temp folder, wired to an in-memory transport.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private int _nextId;

    private TestHost(string root, FakeDeviceProvider fake, DeviceHub hub, SqliteTransferStore store, TransferQueue queue, CountingKeepAwake awake)
    {
        Root = root;
        Paths = new AppPaths(Path.Combine(root, "app"));
        Fake = fake;
        Hub = hub;
        Store = store;
        Queue = queue;
        KeepAwake = awake;
        Settings = new SettingsStore(Paths.SettingsFile);
        // Never the user's own backup folder, nor iTunes' backups.
        Settings.Update(new JsonObject { ["backupFolder"] = Path.Combine(root, "backups") });
        Backups = new BackupService(hub, Settings, new DeviceDeck.Ios.Backup.Format.IosBackupFormat(), new BackupServiceOptions
        {
            AppleBackupFolders = [],
            KeepAwake = awake,
            RetryDelay = TimeSpan.FromMilliseconds(100),
            ProgressInterval = TimeSpan.FromMilliseconds(50),
        });
        Backups.Start();
        Shell = new FakeShell();
        Transport = new MemoryTransport();
        Services = new BridgeServices
        {
            Hub = hub,
            Transfers = queue,
            Settings = Settings,
            Shell = Shell,
            Screenshots = new ScreenshotStore(),
            Version = "9.9.9-test",
            DevFake = fake,
            Backups = Backups,
        };
        Bridge = new BridgeHost(Transport, Services);
    }

    public string Root { get; }
    public AppPaths Paths { get; }
    public FakeDeviceProvider Fake { get; }
    public DeviceHub Hub { get; }
    public SqliteTransferStore Store { get; }
    public TransferQueue Queue { get; }
    public CountingKeepAwake KeepAwake { get; }
    public SettingsStore Settings { get; }
    public BackupService Backups { get; }
    public FakeShell Shell { get; }
    public MemoryTransport Transport { get; }
    public BridgeServices Services { get; }
    public BridgeHost Bridge { get; }

    public string PcFolder(string name)
    {
        var path = Path.Combine(Root, "pc", name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static Task<TestHost> StartAsync(params FakeDeviceSpec[] phones) =>
        StartAsync(null, null, phones);

    public static Task<TestHost> StartAsync(long? bytesPerSecond, TransferQueueOptions? options, params FakeDeviceSpec[] phones) =>
        StartAsync(bytesPerSecond, options, TimeSpan.Zero, phones);

    /// <param name="backupDuration">How long a fake iPhone's backup takes (long enough to pull the cable part-way).</param>
    public static async Task<TestHost> StartAsync(long? bytesPerSecond, TransferQueueOptions? options, TimeSpan backupDuration, params FakeDeviceSpec[] phones)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fakeOptions = new FakeOptions
        {
            ArrivalDelay = TimeSpan.Zero,
            BytesPerSecond = bytesPerSecond,
            RebootDelay = TimeSpan.FromMilliseconds(50),
            IndexDuration = TimeSpan.Zero,
            BackupDuration = backupDuration,
            Devices = phones.Length == 0 ? [TestPhones.Iphone(), TestPhones.Android()] : phones,
        };
        var fake = new FakeDeviceProvider(fakeOptions);
        var hub = new DeviceHub([fake]) { DriverPollInterval = TimeSpan.Zero };
        await hub.StartAsync(CancellationToken.None);
        var store = new SqliteTransferStore(Path.Combine(root, "app", "transfers.db"));
        var awake = new CountingKeepAwake();
        var queue = new TransferQueue(hub, store, TransferHandlers.All(), awake, options ?? FastQueue());
        queue.Start();
        return new TestHost(root, fake, hub, store, queue, awake);
    }

    public static TransferQueueOptions FastQueue() => new()
    {
        WorkersPerDevice = 2,
        RetryDelay = TimeSpan.FromMilliseconds(100),
        ProgressInterval = TimeSpan.FromMilliseconds(100),
    };

    /// <summary>Sends a request through the transport and returns the parsed response.</summary>
    public async Task<JsonObject> CallAsync(string method, object? parameters = null, IReadOnlyList<string>? files = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters is null ? null : JsonSerializer.SerializeToNode(parameters, Core.Serialization.DeviceDeckJson.Options),
        };
        return await Transport.RequestAsync(request.ToJsonString(), id, files ?? []);
    }

    public async Task<JsonNode?> ResultAsync(string method, object? parameters = null)
    {
        var response = await CallAsync(method, parameters);
        Assert.True(response.ContainsKey("result"), $"{method} failed: {response.ToJsonString()}");
        return response["result"];
    }

    public async ValueTask DisposeAsync()
    {
        Bridge.Dispose();
        await Backups.DisposeAsync();
        await Queue.DisposeAsync();
        Store.Dispose();
        await Hub.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class MemoryTransport : IBridgeTransport
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();

    public event EventHandler<BridgeMessage>? MessageReceived;

    public ConcurrentQueue<JsonObject> Events { get; } = new();

    public void Post(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is JsonObject message && message.ContainsKey("id") && message["id"] is JsonValue id && id.TryGetValue<long>(out var key) &&
            _pending.TryRemove(key, out var waiter))
        {
            waiter.TrySetResult(message);
        }
        else if (node is JsonObject notification && !notification.ContainsKey("id"))
        {
            Events.Enqueue(notification);
        }
    }

    public async Task<JsonObject> RequestAsync(string json, long id, IReadOnlyList<string> files)
    {
        var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        MessageReceived?.Invoke(this, new BridgeMessage(json, files));
        return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public IEnumerable<JsonObject> EventsNamed(string method) => Events.Where(e => (string?)e["method"] == method);
}

internal sealed class FakeShell : IShellServices
{
    public string? NextFolder { get; set; } = @"C:\Picked";
    public List<string> Revealed { get; } = [];

    public Task<string?> PickFolderAsync(string? title, CancellationToken ct) => Task.FromResult(NextFolder);

    public void RevealInExplorer(string path) => Revealed.Add(path);
}

internal sealed class CountingKeepAwake : KeepAwakeBase
{
    public int Begins { get; private set; }
    public int Ends { get; private set; }

    protected override void Begin() => Begins++;

    protected override void End() => Ends++;
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(15);
        }
    }

    public static Task ForStateAsync(TransferQueue queue, string jobId, TransferState state, int timeoutMs = 10_000) =>
        UntilAsync(() => queue.Get(jobId)?.State == state, $"job {jobId} to reach {state} (now {queue.Get(jobId)?.State})", timeoutMs);
}
