using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.LocalSend;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests.LocalSend;

/// <summary>An iPhone whose port 53317 leads to a <see cref="FakeLocalSendReceiver"/> while LocalSend is "open" on it.</summary>
internal sealed class LocalSendTestPhone : IDevice, IDevicePortTunnel
{
    private int _connects;

    public LocalSendTestPhone(string id = "ls-iphone", string name = "Test iPhone", DevicePlatform platform = DevicePlatform.Ios)
    {
        Summary = new DeviceSummary(id, name, platform, "iPhone17,3", name, "27.0", ConnectionKind.Usb, DeviceState.Ready, DeviceMode.Full,
            DeviceCapability.Info | DeviceCapability.PhotosRead | DeviceCapability.AppDocuments);
    }

    public DeviceSummary Summary { get; }

    /// <summary>LocalSend open in front on the phone; null = closed (usbmux answers "connection refused").</summary>
    public FakeLocalSendReceiver? Receiver { get; set; }

    public bool Plugged { get; set; } = true;

    public int Connects => Volatile.Read(ref _connects);

    public async Task<Stream> ConnectPortAsync(int port, CancellationToken ct)
    {
        Interlocked.Increment(ref _connects);
        if (!Plugged)
        {
            throw new DeviceException(DeviceErrorCodes.Disconnected, "The iPhone was disconnected.");
        }

        if (port != LocalSendProtocol.DefaultPort || Receiver is not { } receiver)
        {
            throw new DeviceException(IDevicePortTunnel.PortClosedCode, $"Nothing on the iPhone listens on port {port}.");
        }

        return await receiver.ConnectAsync(ct);
    }

    public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FileRoot>>([]);
    public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.PathNotFound, "No such root.");
    public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => Task.FromResult(new DeviceDetails([]));
    public IPhotoLibrary Photos { get; } = new NoPhotos();
    public IAppManager Apps => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public IDeviceActions Actions => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
}

/// <summary>The test phone shows no photos (the bridge looks at the library when a phone arrives).</summary>
internal sealed class NoPhotos : IPhotoLibrary
{
    public async IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }

    public Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    public Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
    public Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct) => throw new DeviceException(DeviceErrorCodes.NotSupported, "n/a");
}

internal sealed class LocalSendTestProvider(LocalSendTestPhone phone) : IDeviceProvider
{
    public string Id => "localsend-test";

    public IReadOnlyList<IDevice> Devices => phone.Plugged ? [phone] : [];

    public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    public Task StartAsync(CancellationToken ct)
    {
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Added, phone.Summary));
        return Task.CompletedTask;
    }

    public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "1", null));

    public void Unplug()
    {
        phone.Plugged = false;
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Removed, phone.Summary with { State = DeviceState.Disconnected }));
    }

    public void PlugBack()
    {
        phone.Plugged = true;
        DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Added, phone.Summary));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Hub, queue, LocalSend service and bridge around one <see cref="LocalSendTestPhone"/>, in a temp folder.</summary>
internal sealed class LocalSendTestHost : IAsyncDisposable
{
    public static readonly LocalSendTimeouts FastTimeouts = new()
    {
        Connect = TimeSpan.FromSeconds(3),
        Short = TimeSpan.FromSeconds(3),
        Accept = TimeSpan.FromSeconds(20),
        Stall = TimeSpan.FromSeconds(3),
        Finish = TimeSpan.FromSeconds(10),
    };

    private int _id;

    private LocalSendTestHost(string root, LocalSendTestPhone phone, LocalSendFinderOptions? finder)
    {
        Root = root;
        Phone = phone;
        Provider = new LocalSendTestProvider(phone);
        Hub = new DeviceHub([Provider]) { DriverPollInterval = TimeSpan.Zero };
        Store = new SqliteTransferStore(Path.Combine(root, "app", "transfers.db"));
        Service = new LocalSendService(Hub, Store.LoadItems, new LocalSendIdentity("DeviceDeck (TEST-PC)", "DD-TEST-FINGERPRINT"),
            finder ?? new LocalSendFinderOptions { Multicast = false, SweepAddresses = () => [], ProbeTimeouts = FastTimeouts }, FastTimeouts);
        Queue = new TransferQueue(Hub, Store, [.. TransferHandlers.All(), Service.Handler], new CountingKeepAwake(), TestHost.FastQueue());
        Queue.Start();
        Service.Attach(Queue);
        Transport = new MemoryTransport();
        Bridge = new BridgeHost(Transport, new BridgeServices
        {
            Hub = Hub,
            Transfers = Queue,
            Settings = new SettingsStore(Path.Combine(root, "app", "settings.json")),
            Shell = new FakeShell(),
            Screenshots = new ScreenshotStore(),
            LocalSend = Service,
        });
    }

    public string Root { get; }
    public LocalSendTestPhone Phone { get; }
    public LocalSendTestProvider Provider { get; }
    public DeviceHub Hub { get; }
    public SqliteTransferStore Store { get; }
    public LocalSendService Service { get; }
    public TransferQueue Queue { get; }
    public MemoryTransport Transport { get; }
    public BridgeHost Bridge { get; }

    public static async Task<LocalSendTestHost> StartAsync(LocalSendTestPhone? phone = null, LocalSendFinderOptions? finder = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", "localsend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pc"));
        var host = new LocalSendTestHost(root, phone ?? new LocalSendTestPhone(), finder);
        await host.Hub.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => host.Hub.TryGetDevice(host.Phone.Summary.Id, out _), "the test phone");
        return host;
    }

    /// <summary>A file on "this PC" with recognisable bytes.</summary>
    public string PcFile(string name, int length = 50_000)
    {
        var path = Path.Combine(Root, "pc", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Content(name, length));
        return path;
    }

    public static byte[] Content(string name, int length)
    {
        var seed = System.Text.Encoding.UTF8.GetBytes(name);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed[i % seed.Length] + i * 7);
        }

        return bytes;
    }

    public Task<JsonObject> CallAsync(string method, object parameters, IReadOnlyList<string>? files = null)
    {
        var id = Interlocked.Increment(ref _id);
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(parameters, Core.Serialization.DeviceDeckJson.Options),
        };
        return Transport.RequestAsync(request.ToJsonString(), id, files ?? []);
    }

    public async Task<JsonNode> ResultAsync(string method, object parameters, IReadOnlyList<string>? files = null)
    {
        var response = await CallAsync(method, parameters, files);
        Assert.True(response.ContainsKey("result"), $"{method} failed: {response.ToJsonString()}");
        return response["result"]!;
    }

    public async Task<string> ErrorCodeAsync(string method, object parameters)
    {
        var response = await CallAsync(method, parameters);
        Assert.True(response.ContainsKey("error"), $"{method} should have failed: {response.ToJsonString()}");
        return (string)response["error"]!["data"]!["code"]!;
    }

    /// <summary>The usb target as discovery found it.</summary>
    public async Task<JsonNode> FindUsbTargetAsync()
    {
        var found = await ResultAsync("localsend.discover", new { deviceId = Phone.Summary.Id });
        Assert.Equal("found", (string?)found["usb"]);
        var receiver = found["usbReceiver"]!;
        return new JsonObject
        {
            ["route"] = "usb",
            ["port"] = (int)receiver["port"]!,
            ["protocol"] = (string)receiver["protocol"]!,
            ["fingerprint"] = (string)receiver["fingerprint"]!,
            ["alias"] = (string)receiver["alias"]!,
        };
    }

    public async Task<string> SendAsync(JsonNode target, params string[] files)
    {
        var started = await ResultAsync("localsend.send", new { deviceId = Phone.Summary.Id, target, files });
        return (string)started["jobId"]!;
    }

    public async Task<JsonNode> StatusAsync(string jobId) => await ResultAsync("localsend.job", new { jobId });

    public async ValueTask DisposeAsync()
    {
        Bridge.Dispose();
        await Queue.DisposeAsync();
        await Service.DisposeAsync();
        Store.Dispose();
        await Hub.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
