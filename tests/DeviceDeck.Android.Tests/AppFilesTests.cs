using System.Security.Cryptography;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.AppFiles;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Android.Tests;

/// <summary>App folders on Android: listing them, Android/data where adb may read it, measuring, and copies through the queue.</summary>
public sealed class AppFilesTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-android-appfiles-").FullName;

    private static async Task<(AndroidDevice Device, FakeAdbChannel Adb, FakeSharedStorage Storage)> PhoneAsync(bool dataReadable = true)
    {
        FakeSharedStorage? storage = null;
        var (device, adb) = await DeviceTests.ReadyAsync("android-14-google-pixel-7", fake => storage = new FakeSharedStorage(fake));
        storage!
            .AddFile("/sdcard/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Documents/Invoice.pdf", Bytes(5_000, 1))
            .AddDir("/sdcard/Android/media/com.spotify.music")
            .AddFile("/sdcard/Android/data/org.videolan.vlc/files/Movies/Clip.mp4", Bytes(70_000, 2))
            .AddDir("/sdcard/Android/data/com.whatsapp/cache")
            .AddFile("/sdcard/Documents/CV.pdf", Bytes(1_000, 3))
            .AddDir("/sdcard/Download");
        if (!dataReadable)
            storage.Denied.Add("/sdcard/Android/data");
        return (device, adb, storage);
    }

    [Fact]
    public async Task Lists_app_folders_with_android_data_where_adb_may_read_it_then_documents_and_download()
    {
        var (device, adb, _) = await PhoneAsync();

        var overview = await device.GetAppFilesAsync(None);

        Assert.Equal(AppDataAccess.Allowed, overview.PrivateData);
        Assert.Equal(["com.spotify.music", "com.whatsapp", "org.videolan.vlc", "shared:Documents", "shared:Download"], overview.Entries.Select(e => e.Id));
        var whatsapp = overview.Entries.Single(e => e.Id == "com.whatsapp");
        Assert.Equal((AppFilesKind.App, "com.whatsapp", "com.whatsapp"), (whatsapp.Kind, whatsapp.Name, whatsapp.AppId));
        Assert.Equal([new AppFolder("appmedia:com.whatsapp", "Media"), new AppFolder("appdata:com.whatsapp", "Data", Empty: true)], whatsapp.Folders);
        Assert.Equal([new AppFolder("appmedia:com.spotify.music", "Media", Empty: true)], overview.Entries.Single(e => e.Id == "com.spotify.music").Folders);
        var download = overview.Entries.Single(e => e.Id == "shared:Download");
        Assert.Equal((AppFilesKind.SharedFolder, "Download", null), (download.Kind, download.Name, download.AppId));
        Assert.True(download.Folders.Single().Empty);
        Assert.Single(adb.Commands, c => c == AndroidDevice.AppFilesScript);
        Assert.Empty(adb.Unmatched);
    }

    [Fact]
    public async Task A_phone_that_hides_android_data_lists_media_folders_only_and_says_so()
    {
        var (device, _, _) = await PhoneAsync(dataReadable: false);

        var overview = await device.GetAppFilesAsync(None);

        Assert.Equal(AppDataAccess.Blocked, overview.PrivateData);
        Assert.DoesNotContain(overview.Entries, e => e.Id == "org.videolan.vlc");
        Assert.Equal(["appmedia:com.whatsapp"], overview.Entries.Single(e => e.Id == "com.whatsapp").Folders.Select(f => f.RootKey));

        await using var data = await device.OpenFileSystemAsync("appdata:org.videolan.vlc", None);
        Assert.Equal(DeviceErrorCodes.PermissionDenied,
            (await Assert.ThrowsAsync<DeviceException>(() => data.ListAsync("/", None).ToListAsync().AsTask())).Code);
    }

    [Theory]
    [InlineData("allowed\n", AppDataAccess.Blocked)] // listable but empty: the phone is hiding it
    [InlineData("blocked\n", AppDataAccess.Blocked)]
    [InlineData("", AppDataAccess.Blocked)]
    [InlineData("allowed\n1|com.x.y\n", AppDataAccess.Allowed)]
    public void Android_data_access_comes_from_the_probe(string data, AppDataAccess expected)
    {
        var overview = AndroidDevice.ParseAppFiles(new Dictionary<string, string> { ["media"] = "1|com.a.b\n0|not a package\n", ["data"] = data, ["shared"] = "1|Download\n1|Elsewhere\n" });

        Assert.Equal(expected, overview.PrivateData);
        Assert.Equal(expected == AppDataAccess.Allowed ? ["com.a.b", "com.x.y", "shared:Download"] : ["com.a.b", "shared:Download"], overview.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task App_folder_roots_open_the_right_folder_and_refuse_anything_else()
    {
        var (device, _, _) = await PhoneAsync();

        await using (var media = await device.OpenFileSystemAsync("appmedia:com.whatsapp", None))
            Assert.Equal(["WhatsApp"], (await media.ListAsync("/", None).ToListAsync()).Select(e => e.Name));
        await using (var download = await device.OpenFileSystemAsync("shared:Download", None))
            Assert.Empty(await download.ListAsync("/", None).ToListAsync());

        foreach (var bad in new[] { "appmedia:../../data", "appdata:com.x/../y", "shared:DCIM", "appmedia:", "app:com.x" })
            await Assert.ThrowsAsync<DeviceException>(() => device.OpenFileSystemAsync(bad, None));
        Assert.False(AppFilesRootKeys.TryParse("appmedia:com.x y", out _));
    }

    [Fact]
    public async Task Measures_in_one_shell_round_trip()
    {
        var (device, adb, _) = await PhoneAsync();
        adb.Commands.Clear();

        var usage = await device.MeasureAsync("appdata:org.videolan.vlc", ["/", "/files/Movies/Clip.mp4", "/gone"], None);

        Assert.Equal([new PathUsage("/", 1, 2, 70_000), new PathUsage("/files/Movies/Clip.mp4", 1, 0, 70_000), new PathUsage("/gone", 0, 0, 0)], usage.Items);
        Assert.Equal((1, 2, 70_000L), (usage.Files, usage.Folders, usage.Bytes)); // nested paths count once
        Assert.Single(adb.Commands);
    }

    [Fact]
    public void Usage_lines_are_summed_under_the_right_path()
    {
        var output = "41f9|3488|/sdcard/a\n81b0|10|/sdcard/a/x\n41f9|3488|/sdcard/a/sub\n81b0|5|/sdcard/a/sub/y|z\n81b0|7|/sdcard/ab\nA1ff|9|/sdcard/a/link\ngarbage\n";

        var usage = AndroidDevice.SumUsage(["/a", "/ab"], ["/sdcard/a", "/sdcard/ab"], output);

        Assert.Equal([new PathUsage("/a", 2, 1, 15), new PathUsage("/ab", 1, 0, 7)], usage.Items);
    }

    [Fact]
    public async Task Round_trip_a_folder_through_the_queue_into_an_app_folder_and_back()
    {
        var (device, adb, storage) = await PhoneAsync();
        var before = storage.Snapshot("/sdcard/Android/data/org.videolan.vlc");
        await using var hub = new DeviceHub([new OneDeviceProvider(device)]) { DriverPollInterval = TimeSpan.Zero };
        await hub.StartAsync(None);
        using var store = new SqliteTransferStore(Path.Combine(_dir, "transfers.db"));
        await using var queue = new TransferQueue(hub, store, TransferHandlers.All(), options: new TransferQueueOptions { RetryDelay = TimeSpan.FromMilliseconds(100) });
        queue.Start();

        var pc = Directory.CreateDirectory(Path.Combine(_dir, "pc", "Season 1")).FullName;
        File.WriteAllBytes(Path.Combine(pc, "E01.mkv"), Bytes(55 * 1024 * 1024, 4));
        File.WriteAllBytes(Path.Combine(pc, "E01.srt"), Bytes(3_000, 5));
        Directory.CreateDirectory(Path.Combine(pc, "Extras"));

        var copyIn = await queue.EnqueueAsync(device.Summary.Id, TransferKind.CopyToDevice, new CopyToDeviceRequest("appdata:org.videolan.vlc", [pc], "/files/Movies"), ConflictRule.Ask, None);
        Assert.Equal("Copying Season 1 into org.videolan.vlc", copyIn.Title); // one folder: no "(Data)"
        await WaitForAsync(queue, copyIn.Id, TransferState.Done);
        Assert.True(storage.Exists("/sdcard/Android/data/org.videolan.vlc/files/Movies/Season 1/Extras"));
        Assert.Equal(55 * 1024 * 1024, adb.Files["/sdcard/Android/data/org.videolan.vlc/files/Movies/Season 1/E01.mkv"].Length);

        var again = await queue.EnqueueAsync(device.Summary.Id, TransferKind.CopyToDevice, new CopyToDeviceRequest("appdata:org.videolan.vlc", [pc], "/files/Movies"), ConflictRule.Ask, None);
        await WaitForAsync(queue, again.Id, TransferState.NeedsDecision);
        queue.ResolveConflict(again.Id, ConflictChoice.Skip, applyToAll: true);
        await WaitForAsync(queue, again.Id, TransferState.Done);
        Assert.Equal(2, queue.Get(again.Id)!.DoneItems);

        var exported = Directory.CreateDirectory(Path.Combine(_dir, "pc", "export")).FullName;
        var whole = await queue.EnqueueAsync(device.Summary.Id, TransferKind.CopyToPc, new CopyToPcRequest("appdata:org.videolan.vlc", ["/"], exported), ConflictRule.Ask, None);
        await WaitForAsync(queue, whole.Id, TransferState.Done);
        var back = Path.Combine(exported, "org.videolan.vlc", "files", "Movies", "Season 1");
        foreach (var file in Directory.EnumerateFiles(pc))
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(file)), SHA256.HashData(File.ReadAllBytes(Path.Combine(back, Path.GetFileName(file)))));
        Assert.True(Directory.Exists(Path.Combine(back, "Extras")));

        await using (var fs = await device.OpenFileSystemAsync("appdata:org.videolan.vlc", None))
            await fs.DeleteAsync("/files/Movies/Season 1", recursive: true, None);
        Assert.Equal(before, storage.Snapshot("/sdcard/Android/data/org.videolan.vlc"));
    }

    private static async Task WaitForAsync(TransferQueue queue, string jobId, TransferState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (queue.Get(jobId)?.State != state)
        {
            var job = queue.Get(jobId);
            Assert.False(job?.State is TransferState.Failed or TransferState.Cancelled, $"Job ended {job?.State}: {job?.Error?.Message}");
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {state}; job is {job?.State}.");
            await Task.Delay(20);
        }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A provider holding one ready device, so the hub and the queue can drive it.</summary>
    private sealed class OneDeviceProvider(AndroidDevice device) : IDeviceProvider
    {
        public string Id => "android";

        public IReadOnlyList<IDevice> Devices => [device];

        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged { add { } remove { } }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "test", null));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
