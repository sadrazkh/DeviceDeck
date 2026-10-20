using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Cleanup;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>Storage analysis and the confirmed clean-up actions, against fake phones with known findings.</summary>
public sealed class CleanupTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<CleanupReport> AnalyzeAsync(TestHost host, string deviceId)
    {
        host.Cleanup.Analyze(deviceId);
        await Wait.UntilAsync(() => host.Cleanup.GetStatus(deviceId).State is not CleanupState.Running, "the analysis to finish");
        var status = host.Cleanup.GetStatus(deviceId);
        Assert.True(status.State == CleanupState.Done, $"analysis ended {status.State}: {status.Error?.Message}");
        return status.Report!;
    }

    private static CleanupItem Item(PhotoAsset a) => new(a.Id, a.Size);

    [Fact]
    public async Task Android_analysis_finds_big_apps_media_caches_app_files_and_exact_copies()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var report = await AnalyzeAsync(host, CleanupPhones.AndroidId);

        Assert.Equal(9_000_000_000, report.FreeBytes);

        // Apps: user apps only (Chrome is a system app), biggest first.
        Assert.Equal(["com.whatsapp", "com.example.game"], report.Apps!.Largest.Select(a => a.Id));
        Assert.Equal(4_100_000 + 3_001_000, report.Apps.TotalBytes);
        Assert.True(report.Apps.CanUninstall);
        Assert.True(report.Apps.CanCopyFirst);

        // Media: videos and photos apart, each biggest first.
        Assert.Equal(["1", "2", "3", "4"], report.Media!.LargestVideos.Select(a => a.Id));
        Assert.Equal("11", report.Media.LargestPhotos[0].Id);
        Assert.Equal(4, report.Media.VideoCount);
        Assert.Equal(10_000_000, report.Media.VideoBytes);
        Assert.True(report.Media.CanDelete);

        // Caches: as the phone counted them (the fake keeps a quarter of each app's data).
        Assert.Equal(1_000_000 + 250 + 2_250_000, report.Caches!.TotalBytes);
        Assert.Equal("com.android.chrome", report.Caches.Apps[0].AppId);
        Assert.Equal("WhatsApp", report.Caches.Apps.Single(a => a.AppId == "com.whatsapp").Name);

        // App files: only from the app folders and downloads, at least 100 kB, biggest first; DCIM is media, not app files.
        Assert.Equal(["/Android/media/com.whatsapp/WhatsApp/Media/big-video.mp4", "/Download/setup.apk", "/Documents/report.pdf"],
            report.AppFiles!.Files.Select(f => f.Path));
        Assert.Equal("com.whatsapp", report.AppFiles.Files[0].AppId);
        Assert.Equal("WhatsApp", report.AppFiles.AppNames["com.whatsapp"]);
        Assert.Equal("Download", report.AppFiles.Files[1].Folder);
        Assert.True(report.AppFiles.CanDelete);
        Assert.Contains(report.Notes, n => n.Section == CleanupSection.AppFiles && n.Message.Contains("Android/data"));

        // Duplicates: three copies of IMG_1, two of VID_dup, two small screenshots; IMG_2 has IMG_1's size but not its bytes.
        var groups = report.Duplicates!.Groups;
        Assert.Equal(3, groups.Count);
        Assert.Equal(["2", "3"], groups[0].Assets.Select(a => a.Id).Order());
        Assert.Equal(["5", "6", "7"], groups[1].Assets.Select(a => a.Id).Order());
        Assert.Equal(["10", "9"], groups[2].Assets.Select(a => a.Id).Order());
        Assert.DoesNotContain(groups, g => g.Assets.Any(a => a.Id == "8"));
        Assert.Equal(2_000_000 + 2 * 300_000 + 800, report.Duplicates.ReclaimableBytes);
        Assert.True(report.Duplicates.CanDelete);
    }

    [Fact]
    public async Task iPhone_analysis_walks_shared_app_folders_leaves_icloud_only_out_and_never_offers_media_delete()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Iphone());
        var report = await AnalyzeAsync(host, CleanupPhones.IphoneId);

        Assert.False(report.Media!.CanDelete);
        Assert.Equal(1, report.Media.CloudOnlyCount);
        Assert.DoesNotContain(report.Media.LargestPhotos, a => a.InCloudOnly);
        Assert.Null(report.Caches); // an iPhone has no app-cache trim
        Assert.Equal(["/Downloads/archive.zip", "/Movie.mkv"], report.AppFiles!.Files.Select(f => f.Path));
        Assert.Equal(["app:com.readdle.Documents", "app:org.videolan.vlc-ios"], report.AppFiles.Files.Select(f => f.Root));
        Assert.Equal("VLC", report.AppFiles.AppNames["org.videolan.vlc-ios"]);
        Assert.False(report.Duplicates!.CanDelete);
        Assert.Equal(2_000_000, Assert.Single(report.Duplicates.Groups).ReclaimableBytes);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public async Task Progress_and_the_finished_report_reach_the_page_as_cleanup_updated()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var started = await host.ResultAsync("cleanup.analyze", new { deviceId = CleanupPhones.AndroidId });
        Assert.Equal("running", (string?)started!["state"]);
        await Wait.UntilAsync(() => host.Transport.EventsNamed("cleanup.updated").Any(e => (string?)e["params"]!["state"] == "done"), "cleanup.updated done");

        var updates = host.Transport.EventsNamed("cleanup.updated").Select(e => e["params"]!.AsObject()).ToList();
        Assert.Contains(updates, u => (string?)u["state"] == "running" && (string?)u["progress"]!["section"] == "duplicates");
        var status = (await host.ResultAsync("cleanup.status", new { deviceId = CleanupPhones.AndroidId }))!.AsObject();
        Assert.Equal(["deviceId", "state", "progress", "report", "error", "finishedAt"], status.Select(p => p.Key));
        var report = status["report"]!.AsObject();
        Assert.Equal(["deviceId", "platform", "startedAt", "totalBytes", "freeBytes", "apps", "media", "caches", "appFiles", "duplicates", "notes"], report.Select(p => p.Key));
        var group = report["duplicates"]!["groups"]![0]!.AsObject();
        Assert.Equal(["hash", "size", "assets", "reclaimableBytes"], group.Select(p => p.Key));
        Assert.Equal("appFiles", (string?)report["notes"]![0]!["section"]);
    }

    [Fact]
    public async Task Nothing_can_be_deleted_before_an_analysis_or_outside_it()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Cleanup.DeleteMediaAsync(id, [new("1", 5_000_000)], 5_000_000, null, None));

        await AnalyzeAsync(host, id);
        // The total must be what the page showed.
        await Assert.ThrowsAsync<ArgumentException>(() => host.Cleanup.DeleteMediaAsync(id, [new("1", 5_000_000)], 4_999_999, null, None));
        // Listed, but not at that size.
        await Assert.ThrowsAsync<ArgumentException>(() => host.Cleanup.DeleteMediaAsync(id, [new("8", 1)], 1, null, None));
        // Chrome is a system app: never listed.
        await Assert.ThrowsAsync<ArgumentException>(() => host.Cleanup.UninstallAppsAsync(id, [new("com.android.chrome", 18_000_000)], 18_000_000, null, None));
        // DCIM isn't an app folder.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Cleanup.DeleteFilesAsync(id, [new("internal", "/DCIM/Camera/20260901_120000.jpg", 900_000)], 900_000, null, None));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Cleanup.DeleteMediaAsync(id, [new("2", 2_000_000), new("2", 2_000_000)], 4_000_000, null, None));

        var library = await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None);
        Assert.Equal(11, library.Total);
    }

    [Fact]
    public async Task Deleting_extra_copies_removes_exactly_those_and_updates_the_report()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        var report = await AnalyzeAsync(host, id);
        var extras = report.Duplicates!.Groups.SelectMany(g => g.Assets.Skip(1)).ToList();

        var result = await host.Cleanup.DeleteMediaAsync(id, extras.Select(Item).ToList(), extras.Sum(a => a.Size), null, None);

        Assert.Equal(4, result.Deleted);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(2_000_000 + 600_000 + 800, result.FreedBytes);
        Assert.False(result.CopyIncomplete);
        var left = (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Items.Select(a => a.Id).ToHashSet();
        Assert.Equal(7, left.Count);
        Assert.All(report.Duplicates.Groups, g => Assert.Contains(g.Assets[0].Id, left)); // the oldest copy of each stays

        var after = host.Cleanup.GetStatus(id).Report!;
        Assert.Empty(after.Duplicates!.Groups);
        Assert.Equal(0, after.Duplicates.ReclaimableBytes);
        Assert.DoesNotContain(after.Media!.LargestVideos, a => extras.Any(e => e.Id == a.Id));
        Assert.Equal(9_000_000_000 + result.FreedBytes, after.FreeBytes);
    }

    [Fact]
    public async Task An_item_that_changed_since_the_analysis_is_skipped_not_deleted()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        var report = await AnalyzeAsync(host, id);
        var big = report.Media!.LargestVideos[0];
        var other = report.Media.LargestVideos[3];

        // Gone from the phone meanwhile (deleted in the gallery app, say).
        await host.Hub.GetDevice(id).Photos.DeleteAsync([other.Id], None);
        var result = await host.Cleanup.DeleteMediaAsync(id, [Item(big), Item(other)], big.Size + other.Size, null, None);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(big.Size, result.FreedBytes);

        // A file that grew since the analysis stays.
        var file = report.AppFiles!.Files.Single(f => f.Path == "/Download/setup.apk");
        await using (var fs = await host.Hub.GetDevice(id).OpenFileSystemAsync("internal", None))
        {
            await fs.WriteAsync(file.Path, new MemoryStream(new byte[260_000]), 260_000, null, None);
        }

        var files = await host.Cleanup.DeleteFilesAsync(id, [new(file.Root, file.Path, file.Size)], file.Size, null, None);
        Assert.Equal(0, files.Deleted);
        Assert.Equal(1, files.Skipped);
        await using var check = await host.Hub.GetDevice(id).OpenFileSystemAsync("internal", None);
        Assert.Equal(260_000, (await check.StatAsync(file.Path, None))!.Size);
    }

    [Fact]
    public async Task Copy_to_PC_first_copies_then_deletes_and_deletes_nothing_when_the_copy_fails()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        var report = await AnalyzeAsync(host, id);
        var video = report.Media!.LargestVideos[0];

        // A file where the folder should be: the copy can't land, so the video must stay.
        var blocked = Path.Combine(host.PcFolder("blocked"), "not-a-folder");
        await File.WriteAllTextAsync(blocked, "x");
        var failed = await host.Cleanup.DeleteMediaAsync(id, [Item(video)], video.Size, blocked, None);
        Assert.True(failed.CopyIncomplete);
        Assert.Equal(0, failed.Deleted);
        Assert.Single(failed.CopyJobIds);
        Assert.Contains(video.Id, (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Items.Select(a => a.Id));

        var folder = host.PcFolder("backup");
        var done = await host.Cleanup.DeleteMediaAsync(id, [Item(video)], video.Size, folder, None);
        Assert.False(done.CopyIncomplete);
        Assert.Equal(1, done.Deleted);
        Assert.Equal(TransferState.Done, host.Queue.Get(done.CopyJobIds[0])!.State);
        var copied = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        Assert.Equal(video.Size, new FileInfo(Assert.Single(copied)).Length);
        Assert.DoesNotContain(video.Id, (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Items.Select(a => a.Id));

        // App files the same way: copied, then deleted.
        var apk = report.AppFiles!.Files.Single(f => f.Path == "/Download/setup.apk");
        var files = await host.Cleanup.DeleteFilesAsync(id, [new(apk.Root, apk.Path, apk.Size)], apk.Size, host.PcFolder("files"), None);
        Assert.Equal(1, files.Deleted);
        Assert.True(File.Exists(Path.Combine(host.PcFolder("files"), "setup.apk")));
        await using var fs = await host.Hub.GetDevice(id).OpenFileSystemAsync("internal", None);
        Assert.Null(await fs.StatAsync(apk.Path, None));
    }

    [Fact]
    public async Task An_iPhone_photo_is_never_deleted_from_the_PC()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Iphone());
        var id = CleanupPhones.IphoneId;
        var report = await AnalyzeAsync(host, id);
        var copy = report.Duplicates!.Groups[0].Assets[1];

        var error = await Assert.ThrowsAsync<DeviceException>(() => host.Cleanup.DeleteMediaAsync(id, [Item(copy)], copy.Size, host.PcFolder("x"), None));
        Assert.Equal(DeviceErrorCodes.NotSupported, error.Code);
        Assert.Contains("Photos app", error.Message);
        Assert.Equal(4, (await host.Hub.GetDevice(id).Photos.GetAssetsAsync(null, 0, 100, None)).Total);
        Assert.Empty(host.Queue.List()); // and nothing was copied either

        // Files in an app's own folder can go.
        var movie = report.AppFiles!.Files.Single(f => f.Name == "Movie.mkv");
        var result = await host.Cleanup.DeleteFilesAsync(id, [new(movie.Root, movie.Path, movie.Size)], movie.Size, null, None);
        Assert.Equal(1, result.Deleted);
        Assert.DoesNotContain(host.Cleanup.GetStatus(id).Report!.AppFiles!.Files, f => f.Name == "Movie.mkv");

        // Apps can't be saved off an iPhone first.
        var vlc = report.Apps!.Largest.Single(a => a.Id == "org.videolan.vlc-ios");
        var copyApp = await Assert.ThrowsAsync<DeviceException>(() =>
            host.Cleanup.UninstallAppsAsync(id, [new(vlc.Id, vlc.AppBytes + vlc.DataBytes)], vlc.AppBytes + vlc.DataBytes, host.PcFolder("apps"), None));
        Assert.Equal(DeviceErrorCodes.NotSupported, copyApp.Code);
        Assert.Contains((await host.Hub.GetDevice(id).Apps.ListAsync(false, None)), a => a.Id == vlc.Id);
    }

    [Fact]
    public async Task Uninstall_with_the_APK_saved_first_and_cache_trim_on_Android()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        var report = await AnalyzeAsync(host, id);
        var game = report.Apps!.Largest.Single(a => a.Id == "com.example.game");
        var size = game.AppBytes + game.DataBytes;

        var folder = host.PcFolder("apks");
        var result = await host.Cleanup.UninstallAppsAsync(id, [new(game.Id, size)], size, folder, None);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(size, result.FreedBytes);
        Assert.NotEmpty(Directory.GetFiles(folder));
        Assert.DoesNotContain(await host.Hub.GetDevice(id).Apps.ListAsync(true, None), a => a.Id == game.Id);
        var after = host.Cleanup.GetStatus(id).Report!;
        Assert.DoesNotContain(after.Apps!.Largest, a => a.Id == game.Id);
        Assert.DoesNotContain(after.Caches!.Apps, a => a.AppId == game.Id);

        var trim = await host.Cleanup.TrimCachesAsync(id, None);
        Assert.Equal(1_000_000 + 2_250_000, trim.FreedBytes); // the game's cache went with the game
        Assert.Equal(trim.FreedBytes, host.Cleanup.GetStatus(id).Report!.Caches!.TrimmedBytes);
        var caches = await ((IAppCacheCleaner)host.Hub.GetDevice(id)).GetCachesAsync(None);
        Assert.Equal(0, caches.TotalBytes);
    }

    [Fact]
    public async Task Cache_trim_is_refused_on_an_iPhone()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Iphone());
        await AnalyzeAsync(host, CleanupPhones.IphoneId);
        var error = await Assert.ThrowsAsync<DeviceException>(() => host.Cleanup.TrimCachesAsync(CleanupPhones.IphoneId, None));
        Assert.Equal(DeviceErrorCodes.NotSupported, error.Code);
    }

    [Fact]
    public async Task A_disconnect_drops_the_report_so_nothing_stale_can_be_deleted()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        var report = await AnalyzeAsync(host, id);
        host.Fake.Disconnect(id);
        await Wait.UntilAsync(() => host.Cleanup.GetStatus(id).State == CleanupState.Idle, "the report to be dropped");
        host.Fake.Reconnect(id);
        await Wait.UntilAsync(() => host.Hub.ListDevices().Any(d => d.Id == id && d.State == DeviceState.Ready), "the phone to return");

        var video = report.Media!.LargestVideos[0];
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Cleanup.DeleteMediaAsync(id, [Item(video)], video.Size, null, None));
        Assert.Null(host.Cleanup.GetStatus(id).Report);
    }

    [Fact]
    public async Task Bridge_methods_check_their_params()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var bad = await host.CallAsync("cleanup.deleteMedia", new { deviceId = CleanupPhones.AndroidId, items = new[] { new { id = "1", size = 1 } }, expectedBytes = 1, copyFirstTo = "relative\\folder" });
        Assert.Equal(-32602, (int)bad["error"]!["code"]!);

        var notYet = await host.CallAsync("cleanup.trimCaches", new { deviceId = CleanupPhones.AndroidId });
        Assert.Contains("Analyse the phone first", (string?)notYet["error"]!["message"]);

        var unknown = await host.CallAsync("cleanup.analyze", new { deviceId = "nope" });
        Assert.Equal("device_not_found", (string?)unknown["error"]!["data"]!["code"]);

        var idle = (await host.ResultAsync("cleanup.status", new { deviceId = CleanupPhones.AndroidId }))!.AsObject();
        Assert.Equal("idle", (string?)idle["state"]);
        Assert.Null(idle["report"]);
    }
}

/// <summary>The duplicate search itself, with a phone whose file contents the test controls.</summary>
public sealed class DuplicateFinderTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>A fake phone whose bytes come from a table: id → (opening bytes, rest). Records what each pass asked for.</summary>
    private sealed class ContentPhone(IDevice inner, Dictionary<string, (string Head, string Tail)> content) : IDevice, IMediaHashSource
    {
        public List<(IReadOnlyList<string> Ids, long? Prefix)> Calls { get; } = [];

        public DeviceSummary Summary => inner.Summary;
        public IPhotoLibrary Photos => inner.Photos;
        public IAppManager Apps => inner.Apps;
        public IDeviceActions Actions => inner.Actions;
        public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => inner.GetStorageAsync(ct);
        public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => inner.GetBatteryAsync(ct);
        public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => inner.GetDetailsAsync(ct);
        public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => inner.GetFileRootsAsync(ct);
        public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => inner.OpenFileSystemAsync(rootKey, ct);

        public Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct)
        {
            Calls.Add((assetIds.ToList(), prefixBytes));
            IReadOnlyDictionary<string, string> result = assetIds
                .Where(content.ContainsKey)
                .ToDictionary(id => id, id => prefixBytes is null ? content[id].Head + content[id].Tail : content[id].Head);
            return Task.FromResult(result);
        }
    }

    /// <summary>The same phone without hashing of its own: the search must read originals.</summary>
    private sealed class PlainPhone(IDevice inner, CountingLibrary photos) : IDevice
    {
        public DeviceSummary Summary => inner.Summary;
        public IPhotoLibrary Photos => photos;
        public IAppManager Apps => inner.Apps;
        public IDeviceActions Actions => inner.Actions;
        public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => inner.GetStorageAsync(ct);
        public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => inner.GetBatteryAsync(ct);
        public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => inner.GetDetailsAsync(ct);
        public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => inner.GetFileRootsAsync(ct);
        public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => inner.OpenFileSystemAsync(rootKey, ct);
    }

    private sealed class CountingLibrary(IPhotoLibrary inner) : IPhotoLibrary
    {
        public List<string> Reads { get; } = [];

        public IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync(CancellationToken ct) => inner.GetAlbumsAsync(ct);
        public Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct) => inner.GetAssetsAsync(albumId, offset, limit, ct);
        public Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct) => inner.GetThumbnailAsync(assetId, size, ct);

        public Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            Reads.Add(assetId);
            return inner.ReadOriginalAsync(assetId, destination, progress, ct);
        }

        public Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct) => inner.DeleteAsync(assetIds, ct);
        public Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct) => inner.ImportAsync(fileName, source, length, progress, ct);
    }

    private static async Task<(FakeDeviceProvider Provider, IDevice Device)> PhoneAsync()
    {
        var provider = new FakeDeviceProvider(FakeOptions.ForTests(CleanupPhones.Android()));
        await provider.StartAsync(None);
        return (provider, provider.Devices.Single());
    }

    [Fact]
    public async Task Openings_are_compared_first_and_only_matching_openings_are_read_whole()
    {
        var (provider, inner) = await PhoneAsync();
        await using var _ = provider;
        // 5, 6, 7 and 8 are all 300 kB. 8 opens differently; 7 opens like 5 and 6 but ends differently.
        var phone = new ContentPhone(inner, new()
        {
            ["5"] = ("A", "1"), ["6"] = ("A", "1"), ["7"] = ("A", "2"), ["8"] = ("B", "1"),
            ["2"] = ("V", "x"), ["3"] = ("V", "x"), ["9"] = ("S", ""), ["10"] = ("S", ""),
        });
        var assets = (await inner.Photos.GetAssetsAsync(null, 0, 100, None)).Items;

        var result = await DuplicateFinderAccess.FindAsync(phone, assets, prefixBytes: 1024);

        Assert.Equal(2, phone.Calls.Count);
        Assert.Equal(1024, phone.Calls[0].Prefix);
        Assert.DoesNotContain("1", phone.Calls[0].Ids);  // a size of its own: never hashed
        Assert.DoesNotContain("11", phone.Calls[0].Ids);
        Assert.Null(phone.Calls[1].Prefix);
        Assert.Equal(["2", "3", "5", "6", "7"], phone.Calls[1].Ids.Order()); // 8 dropped after its opening; 9 and 10 fit in the prefix
        Assert.Equal([["10", "9"], ["2", "3"], ["5", "6"]], result.Groups.Select(g => g.Assets.Select(a => a.Id).Order().ToArray()).OrderBy(g => g[0]));
    }

    [Fact]
    public async Task Without_hashing_on_the_phone_only_same_size_originals_are_read()
    {
        var (provider, inner) = await PhoneAsync();
        await using var _ = provider;
        var library = new CountingLibrary(inner.Photos);
        var phone = new PlainPhone(inner, library);
        var assets = (await inner.Photos.GetAssetsAsync(null, 0, 100, None)).Items;

        var result = await DuplicateFinderAccess.FindAsync(phone, assets, prefixBytes: 1024);

        Assert.Equal(["10", "2", "3", "5", "6", "7", "8", "9"], library.Reads.Order());
        // The fake's originals differ by id, so read byte for byte nothing is a copy.
        Assert.Empty(result.Groups);
        Assert.Equal(8, result.HashedCount);
    }
}

/// <summary>The internal finder, reached through the analyzer's own entry so the tests use what the app uses.</summary>
internal static class DuplicateFinderAccess
{
    public static Task<DuplicateFinder.Result> FindAsync(IDevice device, IReadOnlyList<PhotoAsset> assets, long prefixBytes) =>
        DuplicateFinder.FindAsync(device, assets, prefixBytes, 500, null, CancellationToken.None);
}

/// <summary>The service around a phone that blocks in the duplicate pass, to see cancel and the "wait for the analysis" rule.</summary>
public sealed class CleanupServiceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Wraps the fake phone; its hashing waits until the test lets it go (or the analysis is cancelled).</summary>
    private sealed class SlowHashPhone(IDevice inner) : IDevice, IMediaHashSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DeviceSummary Summary => inner.Summary;
        public IPhotoLibrary Photos => inner.Photos;
        public IAppManager Apps => inner.Apps;
        public IDeviceActions Actions => inner.Actions;
        public Task<StorageInfo> GetStorageAsync(CancellationToken ct) => inner.GetStorageAsync(ct);
        public Task<BatteryInfo> GetBatteryAsync(CancellationToken ct) => inner.GetBatteryAsync(ct);
        public Task<DeviceDetails> GetDetailsAsync(CancellationToken ct) => inner.GetDetailsAsync(ct);
        public Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct) => inner.GetFileRootsAsync(ct);
        public Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct) => inner.OpenFileSystemAsync(rootKey, ct);

        public async Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await ((IMediaHashSource)inner).HashAssetsAsync(assetIds, prefixBytes, progress, ct);
        }
    }

    private sealed class OnePhoneProvider(IDevice device) : IDeviceProvider
    {
        public string Id => "slow";
        public IReadOnlyList<IDevice> Devices => [device];
        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "1", null));
        public void Gone() => DeviceChanged?.Invoke(this, new DeviceChangedEventArgs(DeviceChange.Removed, device.Summary));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        private FakeDeviceProvider _fake = null!;
        private SqliteTransferStore _store = null!;

        public SlowHashPhone Phone { get; private set; } = null!;
        public OnePhoneProvider Provider { get; private set; } = null!;
        public Core.Hub.DeviceHub Hub { get; private set; } = null!;
        public TransferQueue Queue { get; private set; } = null!;
        public CleanupService Cleanup { get; private set; } = null!;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            rig._fake = new FakeDeviceProvider(FakeOptions.ForTests(CleanupPhones.Android()));
            await rig._fake.StartAsync(None);
            rig.Phone = new SlowHashPhone(rig._fake.Devices.Single());
            rig.Provider = new OnePhoneProvider(rig.Phone);
            rig.Hub = new Core.Hub.DeviceHub([rig.Provider]) { DriverPollInterval = TimeSpan.Zero };
            await rig.Hub.StartAsync(None);
            rig._store = new SqliteTransferStore(Path.Combine(rig._root, "transfers.db"));
            rig.Queue = new TransferQueue(rig.Hub, rig._store, Core.Transfers.Handlers.TransferHandlers.All(), new CountingKeepAwake(), TestHost.FastQueue());
            rig.Queue.Start();
            rig.Cleanup = new CleanupService(rig.Hub, rig.Queue, new Core.Settings.SettingsStore(Path.Combine(rig._root, "settings.json")), TestHost.CleanupOptions);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            Phone.Release.TrySetResult();
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
    public async Task Cancel_stops_the_analysis_and_keeps_what_it_found_so_far()
    {
        await using var rig = await Rig.StartAsync();
        var id = CleanupPhones.AndroidId;
        rig.Cleanup.Analyze(id);
        await rig.Phone.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CleanupState.Running, rig.Cleanup.GetStatus(id).State);
        Assert.Equal(CleanupSection.Duplicates, rig.Cleanup.GetStatus(id).Progress!.Section);

        // A second analyze while one runs returns the running one.
        Assert.Equal(CleanupState.Running, rig.Cleanup.Analyze(id).State);

        // Nothing is deleted while the analysis still runs.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Cleanup.DeleteMediaAsync(id, [new("1", 5_000_000)], 5_000_000, null, None));

        rig.Cleanup.Cancel(id);
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(id).State == CleanupState.Cancelled, "the analysis to be cancelled");
        var status = rig.Cleanup.GetStatus(id);
        Assert.NotNull(status.Report!.Media);
        Assert.Null(status.Report.Duplicates);
        Assert.NotNull(status.FinishedAt);
    }

    [Fact]
    public async Task A_phone_that_goes_away_mid_analysis_leaves_no_report()
    {
        await using var rig = await Rig.StartAsync();
        var id = CleanupPhones.AndroidId;
        var updates = new List<CleanupStatus>();
        rig.Cleanup.Updated += (_, s) =>
        {
            lock (updates)
            {
                updates.Add(s);
            }
        };
        rig.Cleanup.Analyze(id);
        await rig.Phone.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        rig.Provider.Gone();
        await Wait.UntilAsync(() => rig.Cleanup.GetStatus(id).State == CleanupState.Idle, "the entry to be reset");
        rig.Phone.Release.TrySetResult();
        await Task.Delay(100);
        Assert.Equal(CleanupState.Idle, rig.Cleanup.GetStatus(id).State);
        Assert.Null(rig.Cleanup.GetStatus(id).Report);
        lock (updates)
        {
            Assert.Equal(CleanupState.Idle, updates[^1].State);
        }
    }
}
