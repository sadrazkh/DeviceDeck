using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>
/// The Drivers card through the bridge (v0.6). v0.5.3 showed "Android tools · Stopped" while adb ran: the page asked once
/// while the server was still starting, and the hub's poll only compared its own answers with each other, so the first
/// poll ("Ready") became its baseline and was never published. Now the hub remembers what the page was told.
/// </summary>
public sealed class DriversBridgeTests
{
    /// <summary>A provider whose driver status the test sets; it can start, download and say its driver changed.</summary>
    private sealed class ScriptedProvider(string id) : IDeviceProvider, IDriverControl, IDriverDownload
    {
        private volatile DriverStatus _status = new(id, DriverState.Starting, null, "Starting the adb server.") { Reason = DriverReasons.AdbStarting };

        public string Id => id;
        public IReadOnlyList<IDevice> Devices => [];
        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;
        public event EventHandler? DriverStatusChanged;
        public event EventHandler<DriverDownloadStatus>? DownloadChanged;

        public int StatusCalls;
        public int StartCalls;
        public int Cancels;
        public Func<Task>? OnStart { get; set; }
        public DriverDownloadStatus DownloadStatus { get; set; } = new(id, DriverDownloadState.Idle);

        public DriverStatus Status
        {
            get => _status;
            set => _status = value;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref StatusCalls);
            return Task.FromResult(_status);
        }

        public async Task StartDriverAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref StartCalls);
            if (OnStart is { } start)
            {
                await start();
            }
        }

        public void SayDriverChanged() => DriverStatusChanged?.Invoke(this, EventArgs.Empty);

        public void SayPhone(DeviceChange change) => DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(change,
            new DeviceSummary("R58N1", "Galaxy", DevicePlatform.Android, "SM-S911B", null, "", ConnectionKind.Usb,
                change == DeviceChange.Removed ? DeviceState.Disconnected : DeviceState.Unauthorized, DeviceMode.Full, DeviceCapability.None)));

        public DriverDownloadStatus StartDownload()
        {
            DownloadStatus = DownloadStatus with { State = DriverDownloadState.Downloading };
            DownloadChanged?.Invoke(this, DownloadStatus);
            return DownloadStatus;
        }

        public void Progress(long received, long total)
        {
            DownloadStatus = DownloadStatus with { ReceivedBytes = received, TotalBytes = total };
            DownloadChanged?.Invoke(this, DownloadStatus);
        }

        public void CancelDownload() => Interlocked.Increment(ref Cancels);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A provider with a fixed driver status and nothing to start (the iPhone row in these tests).</summary>
    private sealed class PlainProvider(string id) : IDeviceProvider
    {
        public string Id => id;
        public IReadOnlyList<IDevice> Devices => [];
        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(id, DriverState.Ready, "18.4.0.4", null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLinks : ILinkOpener
    {
        public List<Uri> Opened { get; } = [];
        public bool StoreMissing { get; set; }

        public void Open(Uri uri)
        {
            if (StoreMissing && uri.Scheme == "ms-windows-store")
            {
                throw new System.ComponentModel.Win32Exception(1155, "No application is associated with the specified file.");
            }
            Opened.Add(uri);
        }
    }

    /// <summary>Hub + bridge over an in-memory transport, with only what drivers.* needs.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        private readonly SqliteTransferStore _store;
        private readonly TransferQueue _queue;
        private int _nextId;

        public Rig(DeviceHub hub, FakeLinks? links = null)
        {
            Directory.CreateDirectory(_root);
            Hub = hub;
            _store = new SqliteTransferStore(Path.Combine(_root, "transfers.db"));
            _queue = new TransferQueue(hub, _store, []);
            Transport = new MemoryTransport();
            Bridge = new BridgeHost(Transport, new BridgeServices
            {
                Hub = hub,
                Transfers = _queue,
                Settings = new SettingsStore(Path.Combine(_root, "settings.json")),
                Shell = new FakeShell(),
                Screenshots = new Images.ScreenshotStore(),
                Links = links,
            });
        }

        public DeviceHub Hub { get; }
        public MemoryTransport Transport { get; }
        public BridgeHost Bridge { get; }

        public async Task<JsonObject> CallAsync(string method, object? parameters = null)
        {
            int id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(parameters, Core.Serialization.DeviceDeckJson.Options),
            };
            return await Transport.RequestAsync(request.ToJsonString(), id, []);
        }

        public async Task<JsonNode> ResultAsync(string method, object? parameters = null)
        {
            var response = await CallAsync(method, parameters);
            Assert.True(response.ContainsKey("result"), $"{method} failed: {response.ToJsonString()}");
            return response["result"]!;
        }

        public IEnumerable<JsonArray> DriversChanged() => Transport.EventsNamed("drivers.changed").Select(e => e["params"]!.AsArray());

        public static string? State(JsonArray drivers, string providerId) =>
            (string?)drivers.Single(d => (string?)d!["providerId"] == providerId)!["state"];

        public async ValueTask DisposeAsync()
        {
            Bridge.Dispose();
            await _queue.DisposeAsync();
            _store.Dispose();
            await Hub.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static readonly DriverStatus AdbReady = new("android", DriverState.Ready, "37.0.1", null) { Source = DriverSources.Bundled };

    [Fact]
    public async Task A_page_that_asked_while_adb_was_starting_is_told_when_it_is_ready_by_the_poll()
    {
        // The v0.5.3 report: the page asks once, early; only the hub's periodic check can correct it.
        var android = new ScriptedProvider("android");
        var hub = new DeviceHub([new PlainProvider("ios"), android]) { DriverPollInterval = TimeSpan.FromMilliseconds(100), CheckDriversOnEvents = false };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);

        var first = (await rig.ResultAsync("drivers.status")).AsArray();
        Assert.Equal("starting", Rig.State(first, "android"));

        android.Status = AdbReady;
        await Wait.UntilAsync(() => rig.DriversChanged().Any(d => Rig.State(d, "android") == "ready"), "drivers.changed with Android ready");
        Assert.Equal("ready", Rig.State(rig.DriversChanged().Last(), "ios"));
    }

    [Fact]
    public async Task A_provider_saying_its_driver_changed_is_checked_and_published_without_any_poll()
    {
        var android = new ScriptedProvider("android");
        var hub = new DeviceHub([android]) { DriverPollInterval = TimeSpan.Zero, DriverCheckDelay = TimeSpan.Zero };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);
        Assert.Equal("starting", Rig.State((await rig.ResultAsync("drivers.status")).AsArray(), "android"));

        android.Status = AdbReady;
        android.SayDriverChanged();
        await Wait.UntilAsync(() => rig.DriversChanged().Any(d => Rig.State(d, "android") == "ready"), "drivers.changed after the provider's signal");
    }

    [Fact]
    public async Task A_phone_coming_or_going_triggers_a_driver_check()
    {
        var android = new ScriptedProvider("android") { Status = AdbReady };
        var hub = new DeviceHub([android]) { DriverPollInterval = TimeSpan.Zero, DriverCheckDelay = TimeSpan.Zero };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);
        await rig.ResultAsync("drivers.status");

        android.Status = AdbReady with { Phones = [new DriverPhoneHint("Galaxy", DriverPhoneProblems.Unauthorized)] };
        android.SayPhone(DeviceChange.Added);
        await Wait.UntilAsync(() => rig.DriversChanged().Any(d => d[0]!["phones"] is JsonArray { Count: 1 }), "drivers.changed with the phone hint");
    }

    [Fact]
    public async Task A_check_that_finds_the_same_answer_tells_nobody()
    {
        var android = new ScriptedProvider("android") { Status = AdbReady with { Phones = [new DriverPhoneHint("Galaxy", DriverPhoneProblems.Offline)] } };
        var hub = new DeviceHub([android]) { DriverPollInterval = TimeSpan.Zero, DriverCheckDelay = TimeSpan.Zero };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);
        await rig.ResultAsync("drivers.status");

        // Equal by value, though the phone list is a new list each time.
        await hub.RefreshDriversAsync(CancellationToken.None);
        await hub.RefreshDriversAsync(CancellationToken.None);
        Assert.Empty(rig.DriversChanged());
    }

    [Fact]
    public async Task Drivers_start_starts_it_answers_the_new_statuses_and_tells_other_windows()
    {
        var android = new ScriptedProvider("android") { Status = new DriverStatus("android", DriverState.Error, null, "won't start") { Reason = DriverReasons.AdbFailed } };
        android.OnStart = () =>
        {
            android.Status = AdbReady;
            return Task.CompletedTask;
        };
        var hub = new DeviceHub([new PlainProvider("ios"), android]) { DriverPollInterval = TimeSpan.Zero, CheckDriversOnEvents = false };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);
        await rig.ResultAsync("drivers.status");

        var after = (await rig.ResultAsync("drivers.start", new { providerId = "android" })).AsArray();
        Assert.Equal("ready", Rig.State(after, "android"));
        Assert.Equal(1, android.StartCalls);
        Assert.Contains(rig.DriversChanged(), d => Rig.State(d, "android") == "ready");

        var unknown = await rig.CallAsync("drivers.start", new { providerId = "mtp" });
        Assert.Equal(-32602, (int)unknown["error"]!["code"]!);
        var cannot = await rig.CallAsync("drivers.start", new { providerId = "ios" });
        Assert.Equal("not_supported", (string?)cannot["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task A_start_that_fails_says_why_in_plain_words()
    {
        var android = new ScriptedProvider("android")
        {
            OnStart = () => throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Windows asked for permission to start Apple Mobile Device Service, and it wasn't given."),
        };
        var hub = new DeviceHub([android]) { DriverPollInterval = TimeSpan.Zero, CheckDriversOnEvents = false };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);
        var refused = await rig.CallAsync("drivers.start", new { providerId = "android" });
        Assert.Equal("permission_denied", (string?)refused["error"]!["data"]!["code"]);
        Assert.Contains("permission", (string?)refused["error"]!["message"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_link_opens_only_named_links_and_the_store_falls_back_to_its_web_page()
    {
        var links = new FakeLinks();
        var hub = new DeviceHub([new PlainProvider("ios")]) { DriverPollInterval = TimeSpan.Zero, CheckDriversOnEvents = false };
        await using var rig = new Rig(hub, links);
        await hub.StartAsync(CancellationToken.None);

        await rig.ResultAsync("drivers.openLink", new { link = "appleDevicesStore" });
        await rig.ResultAsync("drivers.openLink", new { link = "itunes" });
        await rig.ResultAsync("drivers.openLink", new { link = "samsungUsbDriver" });
        Assert.Equal(
            ["ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K", "https://www.apple.com/itunes/", "https://developer.samsung.com/android-usb-driver"],
            links.Opened.Select(u => u.OriginalString));

        links.StoreMissing = true;
        await rig.ResultAsync("drivers.openLink", new { link = "appleDevicesStore" });
        Assert.Equal("https://apps.microsoft.com/detail/9NP83LWLPZ9K", links.Opened[^1].OriginalString);

        var other = await rig.CallAsync("drivers.openLink", new { link = "https://example.com/" });
        Assert.Equal(-32602, (int)other["error"]!["code"]!);
        Assert.Equal(4, links.Opened.Count);
    }

    [Fact]
    public async Task Download_start_status_progress_events_and_cancel()
    {
        var android = new ScriptedProvider("android") { Status = new DriverStatus("android", DriverState.Missing, null, "no adb") { Reason = DriverReasons.AdbMissing } };
        var hub = new DeviceHub([new PlainProvider("ios"), android]) { DriverPollInterval = TimeSpan.Zero, CheckDriversOnEvents = false };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);

        Assert.Equal("idle", (string?)(await rig.ResultAsync("drivers.downloadStatus", new { providerId = "android" }))["state"]);
        Assert.Equal("downloading", (string?)(await rig.ResultAsync("drivers.download", new { providerId = "android" }))["state"]);
        android.Progress(4_000_000, 8_044_989);
        var progress = rig.Transport.EventsNamed("drivers.download").Last()["params"]!;
        Assert.Equal(["providerId", "state", "receivedBytes", "totalBytes", "error", "errorCode", "version"], progress.AsObject().Select(p => p.Key));
        Assert.Equal(4_000_000, (long)progress["receivedBytes"]!);

        await rig.ResultAsync("drivers.cancelDownload", new { providerId = "android" });
        Assert.Equal(1, android.Cancels);

        var cannot = await rig.CallAsync("drivers.download", new { providerId = "ios" });
        Assert.Equal("not_supported", (string?)cannot["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Driver_rows_keep_their_shape_and_add_reason_source_and_phones_only_when_set()
    {
        var android = new ScriptedProvider("android")
        {
            Status = new DriverStatus("android", DriverState.Idle, "37.0.1", "DeviceDeck starts its adb server by itself.")
            {
                Reason = DriverReasons.AdbNotStarted,
                Source = DriverSources.Downloaded,
                Phones = [new DriverPhoneHint("Galaxy S23", DriverPhoneProblems.DriverMissing, "samsung")],
            },
        };
        var hub = new DeviceHub([new PlainProvider("ios"), android]) { DriverPollInterval = TimeSpan.Zero, CheckDriversOnEvents = false };
        await using var rig = new Rig(hub);
        await hub.StartAsync(CancellationToken.None);

        var drivers = (await rig.ResultAsync("drivers.status")).AsArray();
        Assert.Equal(["providerId", "state", "version", "detail"], drivers[0]!.AsObject().Select(p => p.Key));
        var row = drivers[1]!.AsObject();
        Assert.Equal(["providerId", "state", "version", "detail", "reason", "source", "phones"], row.Select(p => p.Key));
        Assert.Equal("idle", (string?)row["state"]);
        Assert.Equal("adbNotStarted", (string?)row["reason"]);
        Assert.Equal("""[{"name":"Galaxy S23","problem":"driverMissing","maker":"samsung"}]""", row["phones"]!.ToJsonString());
    }
}
