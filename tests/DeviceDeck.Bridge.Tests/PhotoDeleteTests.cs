using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Cleanup;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.PhotoDelete;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Bridge.Tests;

/// <summary>photos.deleteSupport and photos.deleteItems (round 4), and Clean-up deleting an iPhone's photos item by item.</summary>
public sealed class PhotoDeleteTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset T = new(2026, 10, 21, 19, 10, 0, TimeSpan.FromHours(3.5));

    // ---------- The bridge, with the fake phones ----------

    [Fact]
    public async Task Support_says_whether_and_why_not()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android(), CleanupPhones.Iphone());

        var android = (await host.ResultAsync("photos.deleteSupport", new { deviceId = CleanupPhones.AndroidId }))!.AsObject();
        Assert.True((bool)android["canDelete"]!);
        Assert.Equal("none", (string?)android["reason"]);
        Assert.Equal("permanent", (string?)android["goesTo"]);
        Assert.False((bool)android["observed"]!);

        var iphone = (await host.ResultAsync("photos.deleteSupport", new { deviceId = CleanupPhones.IphoneId, recheck = true }))!.AsObject();
        Assert.False((bool)iphone["canDelete"]!);
        Assert.Equal("unsupported", (string?)iphone["reason"]);
        Assert.Equal(["canDelete", "reason", "detail", "goesTo", "observed", "checkedAt", "iCloudPhotos"], iphone.Select(p => p.Key));
    }

    [Fact]
    public async Task Deleting_items_deletes_exactly_those_and_answers_per_item()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;

        var report = (await host.ResultAsync("photos.deleteItems", new
        {
            deviceId = id,
            items = new[] { new { id = "5", size = 300_000 }, new { id = "9", size = 800 } },
            expectedBytes = 300_800,
        }))!.AsObject();

        Assert.Equal(2, (int)report["deleted"]!);
        Assert.Equal(300_800, (long)report["freedBytes"]!);
        Assert.Equal("permanent", (string?)report["goesTo"]);
        Assert.False((bool)report["copyIncomplete"]!);
        var items = report["items"]!.AsArray();
        Assert.Equal(["5", "9"], items.Select(i => (string?)i!["id"]));
        Assert.All(items, i => Assert.Equal("deleted", (string?)i!["outcome"]));
        Assert.Equal(["IMG_1.jpg", "Screenshot_1.png"], items.Select(i => (string?)i!["fileName"]));
        var left = (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Items.Select(a => a.Id).ToList();
        Assert.Equal(9, left.Count);
        Assert.DoesNotContain("5", left);
        Assert.Contains("6", left); // its copy stays
    }

    [Fact]
    public async Task One_item_that_changed_means_nothing_is_deleted()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;

        var report = (await host.ResultAsync("photos.deleteItems", new
        {
            deviceId = id,
            items = new[] { new { id = "5", size = 300_000 }, new { id = "11", size = 1 } },
            expectedBytes = 300_001,
        }))!.AsObject();

        Assert.Equal(0, (int)report["deleted"]!);
        Assert.Equal(["notTried", "mismatch"], report["items"]!.AsArray().Select(i => (string?)i!["outcome"]));
        Assert.Equal(11, (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Total);
    }

    [Fact]
    public async Task Copy_to_the_PC_first_and_nothing_goes_when_the_copy_fails()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        var blocked = Path.Combine(host.PcFolder("blocked"), "not-a-folder");
        await System.IO.File.WriteAllTextAsync(blocked, "x");

        var failed = (await host.ResultAsync("photos.deleteItems", new { deviceId = id, items = new[] { new { id = "1", size = 5_000_000 } }, expectedBytes = 5_000_000, copyFirstTo = blocked }))!.AsObject();
        Assert.True((bool)failed["copyIncomplete"]!);
        Assert.Equal("notTried", (string?)failed["items"]![0]!["outcome"]);
        Assert.Equal(11, (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Total);

        var folder = host.PcFolder("before-delete");
        var done = (await host.ResultAsync("photos.deleteItems", new { deviceId = id, items = new[] { new { id = "1", size = 5_000_000 } }, expectedBytes = 5_000_000, copyFirstTo = folder }))!.AsObject();
        Assert.Equal(1, (int)done["deleted"]!);
        Assert.Equal(5_000_000, new FileInfo(Assert.Single(Directory.GetFiles(folder, "*", SearchOption.AllDirectories))).Length);
    }

    [Fact]
    public async Task The_methods_check_their_params_and_the_capability()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android(), CleanupPhones.Iphone());

        var relative = await host.CallAsync("photos.deleteItems", new { deviceId = CleanupPhones.AndroidId, items = new[] { new { id = "1", size = 1 } }, expectedBytes = 1, copyFirstTo = "relative\\folder" });
        Assert.Equal(-32602, (int)relative["error"]!["code"]!);

        var total = await host.CallAsync("photos.deleteItems", new { deviceId = CleanupPhones.AndroidId, items = new[] { new { id = "1", size = 1 } }, expectedBytes = 2 });
        Assert.Equal(-32602, (int)total["error"]!["code"]!);

        var twice = await host.CallAsync("photos.deleteItems", new { deviceId = CleanupPhones.AndroidId, items = new[] { new { id = "1", size = 1 }, new { id = "1", size = 1 } }, expectedBytes = 2 });
        Assert.Equal(-32602, (int)twice["error"]!["code"]!);

        var iphone = await host.CallAsync("photos.deleteItems", new { deviceId = CleanupPhones.IphoneId, items = new[] { new { id = "DCIM/100APPLE/IMG_0002.HEIC", size = 2_000_000 } }, expectedBytes = 2_000_000 });
        Assert.Equal("not_supported", (string?)iphone["error"]!["data"]!["code"]);
        Assert.Equal(4, (await host.Hub.GetDevice(CleanupPhones.IphoneId).Photos.GetAssetsAsync(null, 0, 100, None)).Total);
    }

    // ---------- An iPhone whose camera connection allows deleting ----------

    private const string IphoneId = "delete-iphone";

    private static FakeDeviceSpec DeletableIphone()
    {
        var spec = CleanupPhones.Iphone();
        return new FakeDeviceSpec
        {
            Summary = spec.Summary with { Id = IphoneId, Capabilities = spec.Summary.Capabilities | DeviceCapability.PhotosDelete },
            Storage = spec.Storage,
            Battery = spec.Battery,
            Details = spec.Details,
            Assets =
            [
                .. spec.Assets,
                new PhotoAsset("DCIM/100APPLE/IMG_0005.HEIC", "IMG_0005.HEIC", PhotoKind.LivePhoto, T, 3_000_000, 4032, 3024, null, false, false, "DCIM/100APPLE/IMG_0005.MOV"),
            ],
            Albums = spec.Albums,
            Roots =
            [
                .. spec.Roots,
                new FakeRoot(new FileRoot("media", "Media", FileRootKind.Media), Dir("", Dir("DCIM", Dir("100APPLE", FakeFileNode.File("IMG_0005.MOV", 1_500_000, T))))),
            ],
            Apps = spec.Apps,
        };
    }

    /// <summary>The fake iPhone with an <see cref="IPhotoDeleter"/> that deletes through the library and says the photos went to Recently Deleted.</summary>
    private sealed class DeletingIphone(IDevice inner) : IDevice, IPhotoDeleter, IMediaHashSource
    {
        public Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct) =>
            ((IMediaHashSource)inner).HashAssetsAsync(assetIds, prefixBytes, progress, ct);

        public List<IReadOnlyList<PhotoDeleteItem>> Calls { get; } = [];

        public DeviceSummary Summary => inner.Summary;
        public IPhotoLibrary Photos => inner.Photos;
        public IAppManager Apps => inner.Apps;
        public IDeviceActions Actions => inner.Actions;
        public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => inner.GetStorageAsync(ct);
        public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => inner.GetBatteryAsync(ct);
        public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => inner.GetDetailsAsync(ct);
        public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => inner.GetFileRootsAsync(ct);
        public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => inner.OpenFileSystemAsync(rootKey, ct);

        public Task<PhotoDeleteSupport> GetDeleteSupportAsync(bool recheck, CancellationToken ct) =>
            Task.FromResult(new PhotoDeleteSupport(true, PhotoDeleteBlock.None, "fake", PhotoDeleteDestination.Permanent, false, T, ICloudPhotos: true));

        public async Task<IReadOnlyList<PhotoDeleteResult>> DeleteConfirmedAsync(IReadOnlyList<PhotoDeleteItem> items, CancellationToken ct)
        {
            Calls.Add(items);
            await inner.Photos.DeleteAsync(items.Select(i => i.Id).ToList(), ct);
            return items.Select(i => new PhotoDeleteResult(i.Id, Path.GetFileName(i.Id), i.Size, PhotoDeleteOutcome.Deleted, null, null,
                i.Id.EndsWith("0005.HEIC", StringComparison.Ordinal) ? ["IMG_0005.MOV"] : [], PhotoDeleteDestination.RecentlyDeleted)).ToList();
        }
    }

    private sealed class OnePhoneProvider(IDevice device) : IDeviceProvider
    {
        public string Id => "delete";
        public IReadOnlyList<IDevice> Devices => [device];
        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged { add { } remove { } }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "1", null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        private FakeDeviceProvider _fake = null!;
        private SqliteTransferStore _store = null!;

        public DeletingIphone Phone { get; private set; } = null!;
        public Core.Hub.DeviceHub Hub { get; private set; } = null!;
        public TransferQueue Queue { get; private set; } = null!;
        public SettingsStore Settings { get; private set; } = null!;
        public CleanupService Cleanup { get; private set; } = null!;
        public PhotoDeleteService Deletes { get; private set; } = null!;

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, "pc", name)).FullName;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig._fake = new FakeDeviceProvider(FakeOptions.ForTests(DeletableIphone()));
            await rig._fake.StartAsync(None);
            rig.Phone = new DeletingIphone(rig._fake.Devices.Single());
            rig.Hub = new Core.Hub.DeviceHub([new OnePhoneProvider(rig.Phone)]) { DriverPollInterval = TimeSpan.Zero };
            await rig.Hub.StartAsync(None);
            rig._store = new SqliteTransferStore(Path.Combine(rig._root, "transfers.db"));
            rig.Queue = new TransferQueue(rig.Hub, rig._store, Core.Transfers.Handlers.TransferHandlers.All(), new CountingKeepAwake(), TestHost.FastQueue());
            rig.Queue.Start();
            rig.Settings = new SettingsStore(Path.Combine(rig._root, "settings.json"));
            rig.Cleanup = new CleanupService(rig.Hub, rig.Queue, rig.Settings, TestHost.CleanupOptions);
            rig.Deletes = new PhotoDeleteService(rig.Hub, rig.Queue, rig.Settings);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Cleanup.DisposeAsync();
            await Queue.DisposeAsync();
            _store.Dispose();
            await Hub.DisposeAsync();
            await _fake.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task An_iPhone_goes_through_its_own_deleter_and_a_live_photos_video_is_copied_beside_it_first()
    {
        await using var rig = await Rig.StartAsync();
        var folder = rig.Folder("copies");

        var report = await rig.Deletes.DeleteAsync(IphoneId, [new("DCIM/100APPLE/IMG_0005.HEIC", 3_000_000)], 3_000_000, folder, None);

        Assert.Equal(1, report.Deleted);
        Assert.Equal(PhotoDeleteDestination.RecentlyDeleted, report.GoesTo);
        Assert.Equal(["IMG_0005.MOV"], report.Items.Single().Also);
        Assert.Single(rig.Phone.Calls);
        Assert.Equal(2, report.CopyJobIds.Count); // the still through photos.export, its video from the media root
        Assert.Equal([TransferKind.PhotosExport, TransferKind.CopyToPc], report.CopyJobIds.Select(j => rig.Queue.Get(j)!.Kind));
        var copied = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["IMG_0005.HEIC", "IMG_0005.MOV"], copied);
        Assert.Equal(
            Path.GetDirectoryName(Directory.GetFiles(folder, "IMG_0005.HEIC", SearchOption.AllDirectories).Single()),
            Path.GetDirectoryName(Directory.GetFiles(folder, "IMG_0005.MOV", SearchOption.AllDirectories).Single()));
    }

    [Fact]
    public async Task Clean_up_deletes_an_iPhones_duplicates_through_its_deleter_with_results_per_item()
    {
        await using var rig = await Rig.StartAsync();
        rig.Cleanup.Analyze(IphoneId);
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(IphoneId).State is not CleanupState.Running, "the analysis");
        var report = rig.Cleanup.GetStatus(IphoneId).Report!;
        Assert.True(report.Media!.CanDelete);
        Assert.True(report.Duplicates!.CanDelete);
        var copy = report.Duplicates.Groups.Single().Assets[1];

        var result = await rig.Cleanup.DeleteMediaAsync(IphoneId, [new(copy.Id, copy.Size)], copy.Size, null, None);

        Assert.Equal(1, result.Deleted);
        Assert.Equal(PhotoDeleteDestination.RecentlyDeleted, result.GoesTo);
        Assert.Equal(copy.Id, Assert.Single(result.Items!).Id);
        Assert.Equal(copy.Id, Assert.Single(Assert.Single(rig.Phone.Calls)).Id);
        Assert.Empty(rig.Cleanup.GetStatus(IphoneId).Report!.Duplicates!.Groups);
    }
}
