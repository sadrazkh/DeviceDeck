using System.Collections.Concurrent;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Cleanup;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.PhotoDelete;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Bridge.Tests;

/// <summary>
/// photos.deleteSupport, and the confirmed delete as a transfer job (v0.4.1): photos.deleteItems and cleanup.deleteMedia
/// start a photosDelete job; photos.deleteJob gives its results. The job's stages, cancelling before and during the
/// deletes, a phone that goes away, and a restart in the middle (never a delete that starts by itself).
/// </summary>
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

    private static async Task<System.Text.Json.Nodes.JsonObject> FinishedJobAsync(TestHost host, string jobId)
    {
        await Wait.UntilAsync(() => host.Queue.Get(jobId) is { } j && DeleteJobs.IsFinished(j.State), $"delete job {jobId}");
        return (await host.ResultAsync("photos.deleteJob", new { jobId }))!.AsObject();
    }

    [Fact]
    public async Task Deleting_items_starts_a_job_that_deletes_exactly_those_and_answers_per_item()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;

        var started = (await host.ResultAsync("photos.deleteItems", new
        {
            deviceId = id,
            items = new[] { new { id = "5", size = 300_000 }, new { id = "9", size = 800 } },
            expectedBytes = 300_800,
        }))!.AsObject();
        var jobId = (string)started["jobId"]!;
        Assert.Equal(["jobId", "skipped"], started.Select(p => p.Key));
        Assert.Equal("photosDelete", (string?)(await host.ResultAsync("transfers.list"))!.AsArray().Single(j => (string?)j!["id"] == jobId)!["kind"]);

        var report = await FinishedJobAsync(host, jobId);
        Assert.Equal(["jobId", "deviceId", "source", "state", "stage", "items", "deleted", "freedBytes", "goesTo", "copyJobIds", "copyIncomplete", "skipped", "error"],
            report.Select(p => p.Key));
        Assert.Equal(("done", "photos"), ((string?)report["state"], (string?)report["source"]));
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

        // The drawer saw it read, then delete; a finished job has no stage.
        var updates = host.Transport.EventsNamed("transfer.updated").Select(e => e["params"]!).Where(p => (string?)p["id"] == jobId).ToList();
        var stages = updates.Select(u => (string?)u["stage"]?["name"]).Where(n => n is not null).Distinct().ToList();
        Assert.Equal(["reading", "deleting"], stages);
        Assert.Null(updates[^1]["stage"]);
        Assert.Equal((2, 2), ((int)updates[^1]["doneItems"]!, (int)updates[^1]["totalItems"]!));
    }

    [Fact]
    public async Task One_item_that_changed_means_nothing_is_deleted_and_the_job_says_so()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;

        var started = (await host.ResultAsync("photos.deleteItems", new
        {
            deviceId = id,
            items = new[] { new { id = "5", size = 300_000 }, new { id = "11", size = 1 } },
            expectedBytes = 300_001,
        }))!.AsObject();
        var report = await FinishedJobAsync(host, (string)started["jobId"]!);

        Assert.Equal(0, (int)report["deleted"]!);
        Assert.Equal("failed", (string?)report["state"]);
        Assert.Contains("Nothing was deleted", (string?)report["error"]!["message"]);
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

        var started = await host.ResultAsync("photos.deleteItems", new { deviceId = id, items = new[] { new { id = "1", size = 5_000_000 } }, expectedBytes = 5_000_000, copyFirstTo = blocked });
        var failed = await FinishedJobAsync(host, (string)started!["jobId"]!);
        Assert.True((bool)failed["copyIncomplete"]!);
        Assert.Equal("failed", (string?)failed["state"]);
        Assert.Equal("notTried", (string?)failed["items"]![0]!["outcome"]);
        Assert.Equal(11, (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Total);

        var folder = host.PcFolder("before-delete");
        var again = await host.ResultAsync("photos.deleteItems", new { deviceId = id, items = new[] { new { id = "1", size = 5_000_000 } }, expectedBytes = 5_000_000, copyFirstTo = folder });
        var jobId = (string)again!["jobId"]!;
        var done = await FinishedJobAsync(host, jobId);
        Assert.Equal(1, (int)done["deleted"]!);
        Assert.Single(done["copyJobIds"]!.AsArray());
        Assert.Equal(5_000_000, new FileInfo(Assert.Single(Directory.GetFiles(folder, "*", SearchOption.AllDirectories))).Length);
        var stages = host.Transport.EventsNamed("transfer.updated").Select(e => e["params"]!).Where(p => (string?)p["id"] == jobId)
            .Select(u => (string?)u["stage"]?["name"]).Where(n => n is not null).Distinct().ToList();
        Assert.Equal(["copying", "reading", "deleting"], stages);
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
        Assert.Empty(host.Queue.List()); // no job for a refused delete

        var notADelete = await host.CallAsync("photos.deleteJob", new { jobId = "nope" });
        Assert.Equal(-32602, (int)notADelete["error"]!["code"]!);
    }

    [Fact]
    public async Task Preparing_a_delete_always_answers_at_once()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android(), CleanupPhones.Iphone());

        foreach (var deviceId in new[] { CleanupPhones.AndroidId, CleanupPhones.IphoneId, "no-such-phone" })
        {
            var answer = await host.ResultAsync("photos.deletePrepare", new { deviceId, reason = "Photos: selection" });
            Assert.Empty(answer!.AsObject());
        }

        Assert.Empty(host.Queue.List()); // a read ahead is no transfer
    }

    [Fact]
    public async Task Which_items_the_PC_cant_delete_is_asked_per_phone()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android(), CleanupPhones.Iphone());

        var android = (await host.ResultAsync("photos.deleteReach", new { deviceId = CleanupPhones.AndroidId }))!.AsObject();
        Assert.Equal(["deviceId", "state", "done", "total", "notListed", "checkedAt"], android.Select(p => p.Key));
        Assert.Equal("ready", (string?)android["state"]);
        Assert.Empty(android["notListed"]!.AsArray()); // MediaStore deletes whatever it lists

        var iphone = (await host.ResultAsync("photos.deleteReach", new { deviceId = CleanupPhones.IphoneId }))!.AsObject();
        Assert.Equal("unsupported", (string?)iphone["state"]); // this one can't delete from the PC at all
    }

    [Fact]
    public async Task A_delete_job_cant_be_paused_resumed_or_retried()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var started = await host.ResultAsync("photos.deleteItems", new { deviceId = CleanupPhones.AndroidId, items = new[] { new { id = "11", size = 1 } }, expectedBytes = 1 });
        var jobId = (string)started!["jobId"]!;
        await FinishedJobAsync(host, jobId); // failed: nothing matched

        foreach (var method in new[] { "transfers.pause", "transfers.resume", "transfers.retry" })
        {
            var refused = await host.CallAsync(method, new { jobId });
            Assert.True(refused.ContainsKey("error"), method);
        }

        Assert.Equal(TransferState.Failed, host.Queue.Get(jobId)!.State);
    }

    // ---------- An iPhone whose camera connection allows deleting (a scripted deleter) ----------

    private const string IphoneId = "delete-iphone";

    /// <summary>The clean-up iPhone plus a Live Photo and twelve more photos (IMG_0101…IMG_0112).</summary>
    private static FakeDeviceSpec DeletableIphone()
    {
        var spec = CleanupPhones.Iphone();
        var extra = Enumerable.Range(101, 12).Select(i => new PhotoAsset($"DCIM/100APPLE/IMG_{i:0000}.HEIC", $"IMG_{i:0000}.HEIC", PhotoKind.Photo, T.AddMinutes(-i),
            100_000 + i, 4032, 3024, null, false, false, null));
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
                .. extra,
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

    private static List<PhotoDeleteItem> Twelve() => Enumerable.Range(101, 12).Select(i => new PhotoDeleteItem($"DCIM/100APPLE/IMG_{i:0000}.HEIC", 100_000 + i)).ToList();

    /// <summary>
    /// The fake iPhone with an <see cref="IPhotoDeleter"/> that reads, deletes in batches of five through the library and
    /// checks, telling the job each stage; tests can hold it while it reads, or before a batch goes.
    /// </summary>
    private sealed class DeletingIphone(IDevice inner) : IDevice, IPhotoDeleter, IMediaHashSource, IPhotoDeletePrefetch
    {
        /// <summary>Each read ahead asked for (v0.4.2), with its reason.</summary>
        public ConcurrentQueue<string> Prefetches { get; } = new();

        public void PrefetchForDelete(string reason) => Prefetches.Enqueue(reason);

        /// <summary>What the phone says Windows lists nowhere (v0.4.2).</summary>
        public PhotoDeleteReach? Reach { get; set; }

        public Task<PhotoDeleteReach> GetDeleteReachAsync(CancellationToken ct) =>
            Task.FromResult(Reach ?? new PhotoDeleteReach(Summary.Id, PhotoDeleteReachState.Checking, 0, 0, [], null));

        /// <summary>v0.6, set: hashing (Clean-up's last section, duplicates) waits for it.</summary>
        public TaskCompletionSource? HoldHashing { get; set; }

        public async Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct)
        {
            if (HoldHashing is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            return await ((IMediaHashSource)inner).HashAssetsAsync(assetIds, prefixBytes, progress, ct);
        }

        public ConcurrentQueue<IReadOnlyList<PhotoDeleteItem>> Calls { get; } = new();

        /// <summary>Set: the read waits for it (a cancel stops the wait: nothing deleted).</summary>
        public TaskCompletionSource? HoldReading { get; set; }

        /// <summary>Called once a batch is announced and before it goes (batch number from 0).</summary>
        public Func<int, CancellationToken, Task>? BeforeBatch { get; set; }

        /// <summary>v0.6, set: reading the library waits for it (an iPhone still reading Photos.sqlite).</summary>
        public TaskCompletionSource? HoldLibrary { get; set; }

        public DeviceSummary Summary => inner.Summary;
        public IPhotoLibrary Photos => HoldLibrary is { } hold ? new HeldLibrary(inner.Photos, hold) : inner.Photos;
        public IAppManager Apps => inner.Apps;
        public IDeviceActions Actions => inner.Actions;
        public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => inner.GetStorageAsync(ct);
        public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => inner.GetBatteryAsync(ct);
        public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => inner.GetDetailsAsync(ct);
        public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => inner.GetFileRootsAsync(ct);
        public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => inner.OpenFileSystemAsync(rootKey, ct);

        public Task<PhotoDeleteSupport> GetDeleteSupportAsync(bool recheck, CancellationToken ct) =>
            Task.FromResult(new PhotoDeleteSupport(true, PhotoDeleteBlock.None, "fake", PhotoDeleteDestination.Permanent, false, T, ICloudPhotos: true));

        public async Task<IReadOnlyList<PhotoDeleteResult>> DeleteConfirmedAsync(IReadOnlyList<PhotoDeleteItem> items, IPhotoDeleteObserver? observer, CancellationToken ct)
        {
            Calls.Enqueue(items);
            observer ??= NoPhotoDeleteObserver.Instance;
            observer.Stage(PhotoDeleteStage.Reading, 0, 100);
            if (HoldReading is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            observer.Stage(PhotoDeleteStage.Reading, 100, 100);
            var results = new PhotoDeleteResult?[items.Count];
            observer.Stage(PhotoDeleteStage.Deleting, 0, items.Count);
            for (var start = 0; start < items.Count; start += 5)
            {
                if (start > 0 && ct.IsCancellationRequested)
                {
                    break;
                }

                var batch = Enumerable.Range(start, Math.Min(5, items.Count - start)).ToList();
                observer.Deleting(batch);
                if (BeforeBatch is { } before)
                {
                    await before(start / 5, ct);
                }

                await inner.Photos.DeleteAsync(batch.Select(i => items[i].Id).ToList(), CancellationToken.None);
                foreach (var i in batch)
                {
                    results[i] = new PhotoDeleteResult(items[i].Id, Path.GetFileName(items[i].Id), items[i].Size, PhotoDeleteOutcome.Deleted, null, null,
                        items[i].Id.EndsWith("0005.HEIC", StringComparison.Ordinal) ? ["IMG_0005.MOV"] : [], PhotoDeleteDestination.RecentlyDeleted);
                    observer.Result(i, results[i]!);
                }

                observer.Stage(PhotoDeleteStage.Deleting, start + batch.Count, items.Count);
            }

            var deleted = results.Count(r => r is not null);
            observer.Stage(PhotoDeleteStage.Checking, 0, deleted);
            observer.Stage(PhotoDeleteStage.Checking, deleted, deleted);
            return results.Select((r, i) => r ?? new PhotoDeleteResult(items[i].Id, Path.GetFileName(items[i].Id), items[i].Size, PhotoDeleteOutcome.NotTried, null,
                PhotoDeletes.Cancelled, [], PhotoDeleteDestination.Unknown)).ToList();
        }
    }

    /// <summary>One phone that can be unplugged and plugged back in.</summary>
    private sealed class OnePhoneProvider(IDevice device) : IDeviceProvider
    {
        private bool _gone;

        public string Id => "delete";
        public IReadOnlyList<IDevice> Devices => _gone ? [] : [device];
        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "1", null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Unplug()
        {
            _gone = true;
            DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Removed, device.Summary with { State = DeviceState.Disconnected }));
        }

        public void PlugIn()
        {
            _gone = false;
            DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Added, device.Summary));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        private FakeDeviceProvider _fake = null!;
        private SqliteTransferStore _store = null!;

        public DeletingIphone Phone { get; private set; } = null!;
        public OnePhoneProvider Provider { get; private set; } = null!;
        public Core.Hub.DeviceHub Hub { get; private set; } = null!;
        public TransferQueue Queue { get; private set; } = null!;
        public SettingsStore Settings { get; private set; } = null!;
        public CleanupService Cleanup { get; private set; } = null!;
        public PhotoDeleteService Deletes { get; private set; } = null!;
        public ConcurrentQueue<TransferJob> Updates { get; } = new();

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, "pc", name)).FullName;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig._fake = new FakeDeviceProvider(FakeOptions.ForTests(DeletableIphone()));
            await rig._fake.StartAsync(None);
            rig.Phone = new DeletingIphone(rig._fake.Devices.Single());
            rig.Provider = new OnePhoneProvider(rig.Phone);
            rig.Hub = new Core.Hub.DeviceHub([rig.Provider]) { DriverPollInterval = TimeSpan.Zero };
            await rig.Hub.StartAsync(None);
            rig.Settings = new SettingsStore(Path.Combine(rig._root, "settings.json"));
            rig.OpenQueue();
            return rig;
        }

        private void OpenQueue()
        {
            _store = new SqliteTransferStore(Path.Combine(_root, "transfers.db"));
            Queue = new TransferQueue(Hub, _store, Core.Transfers.Handlers.TransferHandlers.All(), new CountingKeepAwake(), TestHost.FastQueue());
            Queue.JobUpdated += (_, job) => Updates.Enqueue(job);
            Queue.Start();
            Cleanup = new CleanupService(Hub, Queue, Settings, TestHost.CleanupOptions);
            Deletes = new PhotoDeleteService(Hub, Queue, Settings);
        }

        /// <summary>DeviceDeck closes (the queue stops as it does at shutdown) and opens again on the same database.</summary>
        public async Task RestartAsync()
        {
            await Cleanup.DisposeAsync();
            await Queue.DisposeAsync();
            _store.Dispose();
            OpenQueue();
        }

        public Task<PhotoDeleteJobStatus> FinishedAsync(PhotoDeleteStarted started) => DeleteJobs.FinishedAsync(Hub, Queue, Settings, started);

        public Task WaitForStageAsync(string jobId, string stage) =>
            Wait.UntilAsync(() => Queue.Get(jobId)?.Stage?.Name == stage, $"job {jobId} to reach {stage} (now {Queue.Get(jobId)?.State} {Queue.Get(jobId)?.Stage})");

        public async ValueTask DisposeAsync()
        {
            Phone.HoldReading?.TrySetCanceled();
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

        var report = await rig.FinishedAsync(await rig.Deletes.StartAsync(IphoneId, [new("DCIM/100APPLE/IMG_0005.HEIC", 3_000_000)], 3_000_000, folder,
            PhotoDeleteSource.Photos, 0, None));

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

        var result = await rig.FinishedAsync(await rig.Cleanup.DeleteMediaAsync(IphoneId, [new(copy.Id, copy.Size)], copy.Size, null, None));

        Assert.Equal(1, result.Deleted);
        Assert.Equal(PhotoDeleteSource.Cleanup, result.Source);
        Assert.Equal(PhotoDeleteDestination.RecentlyDeleted, result.GoesTo);
        Assert.Equal(copy.Id, Assert.Single(result.Items).Id);
        Assert.True(rig.Phone.Calls.TryPeek(out var call));
        Assert.Equal(copy.Id, Assert.Single(call).Id);
        Assert.Empty(rig.Cleanup.GetStatus(IphoneId).Report!.Duplicates!.Groups);
    }

    private sealed class HeldLibrary(IPhotoLibrary inner, TaskCompletionSource hold) : IPhotoLibrary
    {
        public IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync(CancellationToken ct) => inner.GetAlbumsAsync(ct);

        public async Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct)
        {
            await hold.Task.WaitAsync(ct);
            return await inner.GetAssetsAsync(albumId, offset, limit, ct);
        }

        public Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct) => inner.GetThumbnailAsync(assetId, size, ct);
        public Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct) => inner.ReadOriginalAsync(assetId, destination, progress, ct);
        public Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct) => inner.DeleteAsync(assetIds, ct);
        public Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct) => inner.ImportAsync(fileName, source, length, progress, ct);
    }

    [Fact]
    public async Task Starting_a_delete_answers_at_once_while_the_iPhones_library_is_still_being_read()
    {
        await using var rig = await Rig.StartAsync();
        rig.Phone.HoldLibrary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // v0.6: on the owner's iPhone the dialog stayed open 12 s because the job read the library before it existed.
        var started = await rig.Deletes.StartAsync(IphoneId, [new("DCIM/100APPLE/IMG_0005.HEIC", 3_000_000, "IMG_0005.HEIC")], 3_000_000, null,
            PhotoDeleteSource.Photos, 0, None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("IMG_0005.HEIC", rig.Queue.Details(started.JobId!)!.Value.Items.Single().Name);

        rig.Phone.HoldLibrary.SetResult();
        var report = await rig.FinishedAsync(started);
        Assert.Equal(1, report.Deleted);
        Assert.Equal("IMG_0005.HEIC", report.Items.Single().FileName);
    }

    [Fact]
    public async Task Clean_up_answers_at_once_and_its_job_reads_the_library_to_leave_alone_what_changed()
    {
        await using var rig = await Rig.StartAsync();
        rig.Cleanup.Analyze(IphoneId);
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(IphoneId).State is not CleanupState.Running, "the analysis");
        var copy = rig.Cleanup.GetStatus(IphoneId).Report!.Duplicates!.Groups.Single().Assets[1];
        rig.Phone.HoldLibrary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = await rig.Cleanup.DeleteMediaAsync(IphoneId, [new(copy.Id, copy.Size)], copy.Size, null, None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, started.Skipped);
        await rig.WaitForStageAsync(started.JobId!, PhotoDeleteHandler.Reading);
        Assert.Empty(rig.Phone.Calls);

        rig.Phone.HoldLibrary.SetResult();
        var result = await rig.FinishedAsync(started);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(copy.FileName, result.Items.Single().FileName);
    }

    [Fact]
    public async Task Clean_ups_job_deletes_what_is_unchanged_and_says_which_item_changed()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        host.Cleanup.Analyze(id);
        await Wait.UntilAsync(() => host.Cleanup.GetStatus(id).State is not CleanupState.Running, "the analysis");
        var videos = host.Cleanup.GetStatus(id).Report!.Media!.LargestVideos;
        var (big, other) = (videos[0], videos[3]);
        await host.Hub.GetDevice(id).Photos.DeleteAsync([other.Id], None);

        var started = await host.Cleanup.DeleteMediaAsync(id, [new(big.Id, big.Size), new(other.Id, other.Size)], big.Size + other.Size, null, None);
        await Wait.UntilAsync(() => host.Queue.Get(started.JobId!) is { } j && DeleteJobs.IsFinished(j.State), "the job");
        var job = host.Queue.Get(started.JobId!)!;
        Assert.Equal((TransferState.Done, 1, 0, 1), (job.State, job.DoneItems, job.FailedItems, job.SkippedItems));
        Assert.Equal(big.Size, job.DoneBytes);

        var report = (await host.ResultAsync("photos.deleteJob", new { jobId = started.JobId }))!.AsObject();
        Assert.Equal(1, (int)report["skipped"]!);
        var changed = report["items"]!.AsArray().Single(i => (string?)i!["id"] == other.Id)!;
        Assert.Equal(("mismatch", "changed"), ((string?)changed["outcome"], (string?)changed["code"]));
        Assert.Equal(other.FileName, (string?)changed["fileName"]);
    }

    [Fact]
    public async Task A_delete_that_ends_while_an_analysis_runs_is_taken_out_of_that_analysis_report()
    {
        await using var rig = await Rig.StartAsync();
        rig.Phone.HoldHashing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Cleanup.Analyze(IphoneId);
        // Duplicates come last: the app files are listed by then.
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(IphoneId).Progress?.Section == CleanupSection.Duplicates, "the duplicates section");

        // v0.6: a delete runs on after its dialog closed (here one from App files), so it can end in the middle of an analysis.
        var job = await rig.Queue.EnqueueAsync(IphoneId, TransferKind.FilesDelete,
            new Core.Deletes.FilesDeleteRequest([new("app:org.videolan.vlc-ios", "/Movie.mkv", 300_000)], 300_000), ConflictRule.KeepBoth, None);
        await Wait.UntilAsync(() => rig.Queue.Get(job.Id)!.State == TransferState.Done, "the delete");
        Assert.Equal(CleanupState.Running, rig.Cleanup.GetStatus(IphoneId).State);

        rig.Phone.HoldHashing.SetResult();
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(IphoneId).State is not CleanupState.Running, "the analysis");
        var files = rig.Cleanup.GetStatus(IphoneId).Report!.AppFiles!.Files;
        Assert.DoesNotContain(files, f => f.Name == "Movie.mkv");
        Assert.Contains(files, f => f.Name == "archive.zip");
    }

    [Fact]
    public async Task Clean_ups_analysis_has_the_iPhone_read_its_camera_roll_ahead()
    {
        await using var rig = await Rig.StartAsync();
        rig.Cleanup.Analyze(IphoneId);
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(IphoneId).State is not CleanupState.Running, "the analysis");

        await Wait.UntilAsync(() => rig.Phone.Prefetches.Contains("Clean-up's analysis finished"), "a read ahead");
        Assert.Single(rig.Phone.Prefetches);
    }

    [Fact]
    public async Task A_delete_from_photos_also_leaves_clean_ups_report()
    {
        await using var rig = await Rig.StartAsync();
        rig.Cleanup.Analyze(IphoneId);
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(IphoneId).State is not CleanupState.Running, "the analysis");
        var copy = rig.Cleanup.GetStatus(IphoneId).Report!.Duplicates!.Groups.Single().Assets[1];

        await rig.FinishedAsync(await rig.Deletes.StartAsync(IphoneId, [new(copy.Id, copy.Size)], copy.Size, null, PhotoDeleteSource.Photos, 0, None));

        Assert.Empty(rig.Cleanup.GetStatus(IphoneId).Report!.Duplicates!.Groups);
    }

    [Fact]
    public async Task The_job_goes_through_its_stages_with_progress_and_saves_each_item()
    {
        await using var rig = await Rig.StartAsync();
        rig.Phone.HoldReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Phone.BeforeBatch = (n, _) => n == 1 ? heldBatch.Task : Task.CompletedTask;

        var started = await rig.Deletes.StartAsync(IphoneId, Twelve(), Twelve().Sum(i => i.Size), null, PhotoDeleteSource.Photos, 0, None);
        var id = started.JobId!;
        await rig.WaitForStageAsync(id, PhotoDeleteHandler.Reading);
        Assert.Equal((TransferState.Running, 0, 100), (rig.Queue.Get(id)!.State, rig.Queue.Get(id)!.Stage!.Done, rig.Queue.Get(id)!.Stage!.Total));
        Assert.Equal("Deleting 12 photos and videos from Clean iPhone", rig.Queue.Get(id)!.Title);

        rig.Phone.HoldReading.SetResult();
        await Wait.UntilAsync(() => rig.Queue.Get(id)!.DoneItems == 5, "the first batch");
        var mid = rig.Deletes.Status(id);
        Assert.Equal(5, mid.Deleted);
        Assert.Equal(PhotoDeleteHandler.Deleting, mid.Stage!.Name);
        Assert.Equal(Enumerable.Repeat(PhotoDeleteOutcome.NotTried, 7), mid.Items.Skip(5).Select(r => r.Outcome));

        heldBatch.SetResult();
        var done = await rig.FinishedAsync(started);
        Assert.Equal((TransferState.Done, 12), (done.State, done.Deleted));
        Assert.Null(rig.Queue.Get(id)!.Stage);
        var stages = rig.Updates.Where(u => u.Id == id && u.Stage is not null).Select(u => u.Stage!.Name).Distinct().ToList();
        Assert.Equal([PhotoDeleteHandler.Reading, PhotoDeleteHandler.Deleting, PhotoDeleteHandler.Checking], stages);
        var deleting = rig.Updates.Where(u => u.Id == id && u.Stage?.Name == PhotoDeleteHandler.Deleting).Select(u => u.Stage!.Done).ToList();
        Assert.Equal(deleting.Order(), deleting); // never backwards
    }

    [Fact]
    public async Task Cancelling_while_it_reads_deletes_nothing()
    {
        await using var rig = await Rig.StartAsync();
        rig.Phone.HoldReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = await rig.Deletes.StartAsync(IphoneId, Twelve(), Twelve().Sum(i => i.Size), null, PhotoDeleteSource.Photos, 0, None);
        await rig.WaitForStageAsync(started.JobId!, PhotoDeleteHandler.Reading);

        rig.Queue.Cancel(started.JobId!);
        var report = await rig.FinishedAsync(started);

        Assert.Equal((TransferState.Cancelled, 0), (report.State, report.Deleted));
        Assert.All(report.Items, r => Assert.Equal((PhotoDeleteOutcome.NotTried, PhotoDeleteHandler.StoppedNote), (r.Outcome, r.Message)));
        Assert.Equal(17, (await rig.Phone.Photos.GetAssetsAsync(null, 0, 100, None)).Total);
    }

    [Fact]
    public async Task Cancelling_while_it_deletes_stops_after_the_batch_and_says_which_went()
    {
        await using var rig = await Rig.StartAsync();
        var started = await rig.Deletes.StartAsync(IphoneId, Twelve(), Twelve().Sum(i => i.Size), null, PhotoDeleteSource.Photos, 0, None);
        var id = started.JobId!;
        rig.Phone.BeforeBatch = (n, _) =>
        {
            if (n == 0)
            {
                rig.Queue.Cancel(id); // pressed while the first batch is on its way
            }

            return Task.CompletedTask;
        };

        var report = await rig.FinishedAsync(started);

        Assert.Equal((TransferState.Cancelled, 5), (report.State, report.Deleted));
        Assert.Equal(Enumerable.Repeat(PhotoDeleteOutcome.Deleted, 5), report.Items.Take(5).Select(r => r.Outcome));
        Assert.All(report.Items.Skip(5), r => Assert.Equal(PhotoDeleteOutcome.NotTried, r.Outcome));
        Assert.Equal(12, (await rig.Phone.Photos.GetAssetsAsync(null, 0, 100, None)).Total);
        var job = rig.Queue.Get(id)!;
        Assert.Equal((5, 0, 12), (job.DoneItems, job.FailedItems, job.TotalItems));
    }

    [Fact]
    public async Task A_phone_that_goes_away_fails_the_job_and_it_never_runs_again()
    {
        await using var rig = await Rig.StartAsync();
        rig.Phone.HoldReading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = await rig.Deletes.StartAsync(IphoneId, Twelve().Take(6).ToList(), Twelve().Take(6).Sum(i => i.Size), null, PhotoDeleteSource.Photos, 0, None);
        var queued = await rig.Deletes.StartAsync(IphoneId, Twelve().Skip(6).ToList(), Twelve().Skip(6).Sum(i => i.Size), null, PhotoDeleteSource.Photos, 0, None);
        await rig.WaitForStageAsync(running.JobId!, PhotoDeleteHandler.Reading);
        Assert.Equal(TransferState.Queued, rig.Queue.Get(queued.JobId!)!.State); // deletes take turns on one phone
        Assert.Equal(6, rig.Queue.Get(queued.JobId!)!.TotalItems); // planned when queued: the drawer shows how many

        rig.Provider.Unplug();
        var first = await rig.FinishedAsync(running);
        var second = await rig.FinishedAsync(queued);
        rig.Phone.HoldReading = null;
        rig.Provider.PlugIn();
        await Task.Delay(300);

        Assert.Equal((TransferState.Failed, DeviceErrorCodes.Disconnected), (first.State, first.Error?.Code));
        Assert.Equal((TransferState.Failed, DeviceErrorCodes.Disconnected), (second.State, second.Error?.Code));
        Assert.Equal(TransferState.Failed, rig.Queue.Get(running.JobId!)!.State);
        Assert.Equal(TransferState.Failed, rig.Queue.Get(queued.JobId!)!.State);
        Assert.Single(rig.Phone.Calls); // the queued one never started, then or after
        Assert.Equal(17, (await rig.Phone.Photos.GetAssetsAsync(null, 0, 100, None)).Total);
    }

    [Fact]
    public async Task DeviceDeck_closing_in_the_middle_ends_the_delete_stopped_and_it_never_runs_again()
    {
        await using var rig = await Rig.StartAsync();
        // The second batch is on its way when DeviceDeck closes: it never comes back from the phone.
        rig.Phone.BeforeBatch = (n, ct) => n == 1 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask;
        var started = await rig.Deletes.StartAsync(IphoneId, Twelve(), Twelve().Sum(i => i.Size), null, PhotoDeleteSource.Photos, 0, None);
        var id = started.JobId!;
        await Wait.UntilAsync(() => rig.Queue.Get(id)!.DoneItems == 5, "the first batch");

        await rig.RestartAsync();
        await Task.Delay(300); // a queue that would start it would have by now

        var job = rig.Queue.Get(id)!;
        Assert.Equal(TransferState.Cancelled, job.State);
        Assert.Equal("interrupted", job.Error?.Code);
        Assert.Contains("5 deleted, 2 not tried, 5 may be gone", job.Error!.Message);
        Assert.Single(rig.Phone.Calls); // never run again
        var report = rig.Deletes.Status(id);
        Assert.Equal(5, report.Deleted);
        Assert.Equal(Enumerable.Repeat(PhotoDeleteOutcome.Failed, 5), report.Items.Skip(5).Take(5).Select(r => r.Outcome));
        Assert.All(report.Items.Skip(10), r => Assert.Equal(PhotoDeleteOutcome.NotTried, r.Outcome));
        Assert.Throws<InvalidOperationException>(() => rig.Queue.Retry(id));
    }

    [Fact]
    public async Task A_delete_left_running_by_a_crash_ends_stopped_on_the_next_start()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "transfers.db");
        var request = System.Text.Json.JsonSerializer.Serialize(new PhotosDeleteRequest(Twelve(), Twelve().Sum(i => i.Size), null, "yyyy/MM"),
            Core.Serialization.DeviceDeckJson.Options);
        using (var store = new SqliteTransferStore(db))
        {
            // Killed while the second batch was on its way: nothing settled it.
            var job = new StoredJob
            {
                Id = "crashed", Seq = 1, DeviceId = IphoneId, Kind = TransferKind.PhotosDelete, Title = "Deleting 12 photos and videos from Clean iPhone",
                State = TransferState.Running, RequestJson = request, Rule = ConflictRule.KeepBoth, Planned = true, CreatedAt = T,
            };
            job.Items = Twelve().Select((it, i) => new TransferItem
            {
                Index = i, Source = it.Id, Destination = "", Name = Path.GetFileName(it.Id), Size = it.Size,
                State = i < 5 ? TransferItemState.Done : i < 10 ? TransferItemState.Running : TransferItemState.Queued,
            }).ToList();
            store.UpsertJob(job);
            store.ReplaceItems(job.Id, job.Items);
            var never = new StoredJob
            {
                Id = "queued", Seq = 2, DeviceId = IphoneId, Kind = TransferKind.PhotosDelete, Title = "Deleting 2", State = TransferState.Queued,
                RequestJson = request, Rule = ConflictRule.KeepBoth, CreatedAt = T,
            };
            store.UpsertJob(never);
        }

        await using var fake = new FakeDeviceProvider(FakeOptions.ForTests(DeletableIphone()));
        await fake.StartAsync(None);
        var phone = new DeletingIphone(fake.Devices.Single());
        await using var hub = new Core.Hub.DeviceHub([new OnePhoneProvider(phone)]) { DriverPollInterval = TimeSpan.Zero };
        await hub.StartAsync(None);
        using (var store = new SqliteTransferStore(db))
        {
            await using var queue = new TransferQueue(hub, store, Core.Transfers.Handlers.TransferHandlers.All(), new CountingKeepAwake(), TestHost.FastQueue());
            queue.Start();
            await Task.Delay(300);

            var crashed = queue.Get("crashed")!;
            Assert.Equal(TransferState.Cancelled, crashed.State);
            Assert.Contains("5 deleted, 2 not tried, 5 may be gone", crashed.Error!.Message);
            Assert.Equal((5, 5), (crashed.DoneItems, crashed.FailedItems));
            var status = new PhotoDeleteService(hub, queue, new SettingsStore(Path.Combine(root, "settings.json"))).Status("crashed");
            Assert.All(status.Items.Skip(5).Take(5), r => Assert.Equal(("interrupted", PhotoDeleteOutcome.Failed), (r.Code, r.Outcome)));

            var queued = queue.Get("queued")!;
            Assert.Equal(TransferState.Cancelled, queued.State);
            Assert.Contains("before anything was deleted", queued.Error!.Message);
            Assert.Empty(phone.Calls);
        }

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
