using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

/// <summary>
/// App files on an iPhone end to end: the provider over the in-memory usbmux and phone, house_arrest serving VLC's
/// Documents from a fake AFC server, and the real transfer queue and handlers copying in and out of it.
/// </summary>
public sealed class AppFilesTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private const string Vlc = "org.videolan.vlc-ios";

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-appfiles-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly IosDeviceProvider _provider;
    private readonly FakeAfcServer _vlc = new();
    private DeviceHub? _hub;
    private SqliteTransferStore? _store;
    private TransferQueue? _queue;
    private FakePhone? _phone;

    public AppFilesTests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
    }

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_queue is not null)
            await _queue.DisposeAsync();
        _store?.Dispose();
        if (_hub is not null)
            await _hub.DisposeAsync();
        else
            await _provider.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<IosDevice> StartAsync()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var phone = _phone = new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
        phone.Apps.Add(App(Vlc, "VLC", fileSharing: true));
        phone.Apps.Add(App("com.example.docs", "Documents", fileSharing: true));
        phone.Apps.Add(App("com.example.game", "Game", fileSharing: false));
        phone.AppContainers[Vlc] = _vlc
            .AddFile("/Documents/Persepolis.mkv", RandomBytes(300_000, 1))
            .AddFile("/Documents/Subtitles/Persepolis.fa.srt", RandomBytes(2_000, 2))
            .AddFile("/Library/Preferences/org.videolan.vlc-ios.plist", [1, 2, 3]);
        phone.AppContainers["com.example.docs"] = new FakeAfcServer().AddDir("/Documents");
        _mux.Attach(1, phone);

        _hub = new DeviceHub([_provider]) { DriverPollInterval = TimeSpan.Zero };
        await _hub.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            Assert.True(DateTime.UtcNow < deadline, "The fake iPhone never became ready.");
            await Task.Delay(20);
        }

        return _provider.Find(Udid)!;
    }

    private TransferQueue StartQueue()
    {
        _store = new SqliteTransferStore(Path.Combine(_dir, "transfers.db"));
        _queue = new TransferQueue(_hub!, _store, TransferHandlers.All(), options: new TransferQueueOptions { RetryDelay = TimeSpan.FromMilliseconds(100) });
        _queue.Start();
        return _queue;
    }

    [Fact]
    public async Task Lists_only_the_apps_with_file_sharing_each_with_its_documents_root()
    {
        var device = await StartAsync();

        var overview = await device.GetAppFilesAsync(Ct);

        Assert.Equal(AppDataAccess.NotApplicable, overview.PrivateData);
        Assert.Equal([("com.example.docs", "Documents"), (Vlc, "VLC")], overview.Entries.Select(e => (e.Id, e.Name)));
        var vlc = overview.Entries.Single(e => e.Id == Vlc);
        Assert.Equal((AppFilesKind.App, Vlc), (vlc.Kind, vlc.AppId));
        Assert.Equal([new AppFolder("app:" + Vlc, "Documents")], vlc.Folders);
    }

    [Fact]
    public async Task Measures_files_folders_and_bytes_inside_documents_only()
    {
        var device = await StartAsync();

        var usage = await device.MeasureAsync("app:" + Vlc, ["/", "/Subtitles", "/Persepolis.mkv", "/gone"], Ct);

        Assert.Equal(
            [new PathUsage("/", 2, 1, 302_000), new PathUsage("/Subtitles", 1, 0, 2_000), new PathUsage("/Persepolis.mkv", 1, 0, 300_000), new PathUsage("/gone", 0, 0, 0)],
            usage.Items);
        Assert.Equal((2, 1, 302_000L), (usage.Files, usage.Folders, usage.Bytes)); // nested paths count once
    }

    [Fact]
    public async Task Listings_share_one_house_arrest_connection_per_app_and_contents_use_their_own()
    {
        var device = await StartAsync();
        var phone = _phone!;
        int HouseArrests() => phone.Log.Count(l => l == "service:com.apple.mobile.house_arrest");

        for (var i = 0; i < 3; i++)
        {
            await using var fs = await device.OpenFileSystemAsync("app:" + Vlc, Ct);
            await foreach (var _ in fs.ListAsync("/", Ct))
            {
            }
            Assert.NotNull(await fs.StatAsync("/Subtitles", Ct));
        }
        Assert.Equal(1, HouseArrests());

        await using (var fs = await device.OpenFileSystemAsync("app:" + Vlc, Ct))
        {
            using var film = new MemoryStream();
            await fs.ReadAsync("/Persepolis.mkv", film, null, Ct);
            Assert.Equal(300_000, film.Length);
            await fs.WriteAsync("/new.bin", new MemoryStream(new byte[5_000]), 5_000, null, Ct);
            await fs.DeleteAsync("/new.bin", recursive: false, Ct);
        }
        Assert.Equal(2, HouseArrests()); // one more for the contents, closed with that file system

        Assert.Equal(DeviceErrorCodes.NotFound,
            (await Assert.ThrowsAsync<DeviceException>(() => device.OpenFileSystemAsync("app:com.example.game", Ct))).Code);
    }

    [Fact]
    public async Task Round_trip_a_folder_with_a_large_file_through_the_queue_then_delete_it_and_leave_vlc_as_it_was()
    {
        var device = await StartAsync();
        var queue = StartQueue();
        var before = await SnapshotAsync(device);

        // A PC folder: a 60 MB film, a nested folder, an empty folder.
        var pc = Directory.CreateDirectory(Path.Combine(_dir, "pc", "DeviceDeck test")).FullName;
        File.WriteAllBytes(Path.Combine(pc, "big film.mkv"), RandomBytes(60 * 1024 * 1024 + 123, 3));
        Directory.CreateDirectory(Path.Combine(pc, "Notes", "Empty"));
        File.WriteAllBytes(Path.Combine(pc, "Notes", "readme.txt"), "سلام VLC"u8.ToArray());
        File.WriteAllBytes(Path.Combine(pc, "Notes", "data.bin"), RandomBytes(3_000_000, 4));

        var copyIn = await queue.EnqueueAsync(Udid, TransferKind.CopyToDevice, new CopyToDeviceRequest("app:" + Vlc, [pc], "/Subtitles"), ConflictRule.Ask, Ct);
        Assert.Equal("Copying DeviceDeck test into VLC", copyIn.Title);
        await WaitForAsync(queue, copyIn.Id, TransferState.Done);
        Assert.Equal((3, 0), (queue.Get(copyIn.Id)!.DoneItems, queue.Get(copyIn.Id)!.FailedItems));
        Assert.True(_vlc.Exists("/Documents/Subtitles/DeviceDeck test/Notes/Empty"));
        Assert.DoesNotContain(await AllPathsAsync(device), p => p.EndsWith(".ddpart", StringComparison.Ordinal));

        // The same folder again: every file clashes, the question comes once and "apply to all" answers the rest.
        var decisions = 0;
        queue.JobUpdated += (_, job) =>
        {
            if (job.State == TransferState.NeedsDecision)
                Interlocked.Increment(ref decisions);
        };
        var again = await queue.EnqueueAsync(Udid, TransferKind.CopyToDevice, new CopyToDeviceRequest("app:" + Vlc, [pc], "/Subtitles"), ConflictRule.Ask, Ct);
        await WaitForAsync(queue, again.Id, TransferState.NeedsDecision);
        queue.ResolveConflict(again.Id, ConflictChoice.KeepBoth, applyToAll: true);
        await WaitForAsync(queue, again.Id, TransferState.Done);
        Assert.Equal(1, decisions);
        Assert.True(_vlc.Exists("/Documents/Subtitles/DeviceDeck test/big film (1).mkv"));
        Assert.True(_vlc.Exists("/Documents/Subtitles/DeviceDeck test/Notes/readme (1).txt"));

        // Export the folder back and compare byte for byte.
        var exported = Directory.CreateDirectory(Path.Combine(_dir, "pc", "export")).FullName;
        var copyOut = await queue.EnqueueAsync(Udid, TransferKind.CopyToPc, new CopyToPcRequest("app:" + Vlc, ["/Subtitles/DeviceDeck test"], exported), ConflictRule.Ask, Ct);
        await WaitForAsync(queue, copyOut.Id, TransferState.Done);
        var back = Path.Combine(exported, "DeviceDeck test");
        foreach (var file in Directory.EnumerateFiles(pc, "*", SearchOption.AllDirectories))
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(file)), SHA256.HashData(File.ReadAllBytes(Path.Combine(back, Path.GetRelativePath(pc, file)))));
        Assert.True(Directory.Exists(Path.Combine(back, "Notes", "Empty")));

        // The whole app: a folder named after it, not "_".
        var whole = await queue.EnqueueAsync(Udid, TransferKind.CopyToPc, new CopyToPcRequest("app:" + Vlc, ["/"], exported), ConflictRule.Ask, Ct);
        Assert.Equal($"Copying VLC to {exported}", whole.Title);
        await WaitForAsync(queue, whole.Id, TransferState.Done);
        Assert.Equal(300_000, new FileInfo(Path.Combine(exported, "VLC", "Persepolis.mkv")).Length);
        Assert.True(File.Exists(Path.Combine(exported, "VLC", "Subtitles", "DeviceDeck test", "big film.mkv")));

        // Delete what was added: VLC is exactly as it was, and its Library was never reachable.
        await using (var fs = await device.OpenFileSystemAsync("app:" + Vlc, Ct))
            await fs.DeleteAsync("/Subtitles/DeviceDeck test", recursive: true, Ct);
        Assert.Equal(before, await SnapshotAsync(device));
        Assert.True(_vlc.Exists("/Library/Preferences/org.videolan.vlc-ios.plist"));
    }

    private static async Task WaitForAsync(TransferQueue queue, string jobId, TransferState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (queue.Get(jobId)?.State != state)
        {
            var job = queue.Get(jobId);
            Assert.True(job?.State is not (TransferState.Failed or TransferState.Cancelled) || state == job.State, $"Job ended {job?.State}: {job?.Error?.Message}");
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {state}; job is {job?.State}.");
            await Task.Delay(20);
        }
    }

    private static async Task<List<string>> SnapshotAsync(IosDevice device)
    {
        var lines = new List<string>();
        await using var fs = await device.OpenFileSystemAsync("app:" + Vlc, Ct);
        var pending = new Stack<string>(["/"]);
        while (pending.TryPop(out var folder))
        {
            await foreach (var entry in fs.ListAsync(folder, Ct))
            {
                lines.Add($"{entry.Path}|{entry.IsDirectory}|{entry.Size}|{entry.Modified:O}");
                if (entry.IsDirectory)
                    pending.Push(entry.Path);
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static async Task<List<string>> AllPathsAsync(IosDevice device) =>
        (await SnapshotAsync(device)).Select(l => l[..l.IndexOf('|')]).ToList();

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static PlistDict App(string id, string name, bool fileSharing) => new()
    {
        ["CFBundleIdentifier"] = id,
        ["CFBundleDisplayName"] = name,
        ["CFBundleShortVersionString"] = "1.2",
        ["CFBundleVersion"] = "120",
        ["ApplicationType"] = "User",
        ["SignerIdentity"] = "Apple iPhone OS Application Signing",
        ["UIFileSharingEnabled"] = fileSharing,
        ["StaticDiskUsage"] = 1000L,
        ["DynamicDiskUsage"] = 10L,
    };
}
