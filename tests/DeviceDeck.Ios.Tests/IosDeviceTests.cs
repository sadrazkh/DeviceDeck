using System.Collections.Concurrent;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Tests.Fakes;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Ios.Tests;

/// <summary>The provider and devices end to end, against the in-memory usbmux and phone.</summary>
public sealed class IosDeviceTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly ConcurrentQueue<DeviceChangedEventArgs> _events = new();
    private readonly IosDeviceProvider _provider;

    public IosDeviceTests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
        _provider.DeviceChanged += (_, e) => _events.Enqueue(e);
    }

    private FakePhone PairedPhone()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        return new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
    }

    private async Task<IosDevice> AttachAsync(FakePhone phone, DeviceState until = DeviceState.Ready, int id = 1)
    {
        _mux.Attach(id, phone);
        await _provider.StartAsync(Ct);
        await WaitUntil(() => _provider.Find(Udid)?.Summary.State == until, $"state {until}");
        return _provider.Find(Udid)!;
    }

    private async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                var device = _provider.Find(Udid);
                Assert.Fail($"Timed out waiting for {what}; state {device?.Summary.State}, last error: {device?.LastError?.Message ?? _provider.LastListenError?.Message}");
            }
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Paired_phone_is_added_then_ready()
    {
        var device = await AttachAsync(PairedPhone());

        var s = device.Summary;
        Assert.Equal((Udid, "Test iPhone", "iPhone 16", "18.0", ConnectionKind.Usb), (s.Id, s.Name, s.MarketingName, s.OsVersion, s.Connection));
        Assert.Equal(LockdownMapping.ReadyCapabilities, s.Capabilities);
        Assert.Single(_provider.Devices);

        var events = _events.ToList();
        Assert.Equal(DeviceChange.Added, events[0].Change);
        Assert.Equal(DeviceState.Connecting, events[0].Summary.State);
        Assert.Equal(DeviceState.Ready, events[^1].Summary.State);
        Assert.Equal(DeviceChange.Changed, events[^1].Change);
    }

    [Fact]
    public async Task Phones_already_plugged_in_are_listed_when_StartAsync_returns()
    {
        _mux.ListenReplayDelay = TimeSpan.FromSeconds(1.5);
        _mux.Attach(1, PairedPhone());

        await _provider.StartAsync(Ct);

        Assert.Single(_provider.Devices);
        await WaitUntil(() => _provider.Find(Udid)?.Summary.State == DeviceState.Ready, "ready");
        await Task.Delay(1700); // the late replay must not add it twice
        Assert.Single(_events, e => e.Change == DeviceChange.Added);
        Assert.Single(_provider.Devices);
    }

    [Fact]
    public async Task Unpaired_phone_waits_for_trust_then_pairs()
    {
        var phone = new FakePhone(Udid);
        phone.PairReplies.Enqueue("PairingDialogResponsePending");
        phone.PairReplies.Enqueue("PairingDialogResponsePending");
        phone.PairReplies.Enqueue(null); // the user taps Trust

        await AttachAsync(phone, DeviceState.Ready);

        Assert.Contains(_events, e => e.Summary.State == DeviceState.AwaitingTrust);
        Assert.Empty(phone.PairReplies);
        var sent = phone.AcceptedPairRecord!;
        Assert.NotNull(sent.GetData("DeviceCertificate"));
        Assert.Null(sent.GetData("HostPrivateKey")); // keys never leave the PC
        Assert.Equal(_mux.Buid, sent.GetString("SystemBUID"));

        var stored = PlistSerializer.ParseDict(_mux.PairRecords[Udid]);
        Assert.Equal(sent.GetString("HostID"), stored.GetString("HostID"));
        Assert.NotNull(stored.GetData("HostPrivateKey"));
        Assert.Equal(new byte[] { 0xE5, 0xC0 }, stored.GetData("EscrowBag"));
    }

    [Fact]
    public async Task Denied_trust_stays_awaiting_and_operations_say_trust_required()
    {
        var phone = new FakePhone(Udid);
        phone.PairReplies.Enqueue("UserDeniedPairing");

        var device = await AttachAsync(phone, DeviceState.AwaitingTrust);
        await Task.Delay(200);

        Assert.Equal(DeviceState.AwaitingTrust, device.Summary.State);
        Assert.Equal(DeviceCapability.None, device.Summary.Capabilities);
        Assert.Equal(DeviceErrorCodes.TrustRequired, (await Assert.ThrowsAsync<DeviceException>(() => device.GetStorageAsync(Ct))).Code);
    }

    [Fact]
    public async Task Locked_phone_becomes_ready_when_unlocked()
    {
        var phone = PairedPhone();
        phone.StartServiceError = "PasswordProtected";

        var device = await AttachAsync(phone, DeviceState.Locked);
        Assert.Equal(DeviceCapability.Info, device.Summary.Capabilities);

        phone.StartServiceError = null;
        await WaitUntil(() => device.Summary.State == DeviceState.Ready, "unlock");
    }

    [Fact]
    public async Task Pairing_a_locked_phone_waits_for_unlock()
    {
        var phone = new FakePhone(Udid);
        phone.PairReplies.Enqueue("PasswordProtected");
        phone.PairReplies.Enqueue(null);

        await AttachAsync(phone, DeviceState.Ready);
        Assert.Contains(_events, e => e.Summary.State == DeviceState.Locked);
    }

    [Fact]
    public async Task A_phone_that_forgot_this_pc_is_paired_again()
    {
        var phone = PairedPhone();
        phone.ExpectedHostId = "HOST-THE-PHONE-KNOWS-NOW";

        await AttachAsync(phone, DeviceState.Ready);
        Assert.NotNull(phone.AcceptedPairRecord);
        Assert.NotEqual(TestPairing.HostId, phone.AcceptedPairRecord!.GetString("HostID"));
    }

    [Fact]
    public async Task Detach_removes_the_device()
    {
        var device = await AttachAsync(PairedPhone());

        _mux.Detach(1);
        await WaitUntil(() => _events.Any(e => e.Change == DeviceChange.Removed), "removal");

        Assert.Empty(_provider.Devices);
        var removed = Assert.Single(_events, e => e.Change == DeviceChange.Removed);
        Assert.Equal(DeviceState.Disconnected, removed.Summary.State);
        Assert.Equal(DeviceErrorCodes.Disconnected, (await Assert.ThrowsAsync<DeviceException>(() => device.GetDetailsAsync(Ct))).Code);
    }

    [Fact]
    public async Task Usb_and_wifi_links_are_one_device()
    {
        var phone = PairedPhone();
        var device = await AttachAsync(phone);
        _mux.Attach(2, new FakePhone(Udid) { Network = true, ExpectedHostId = TestPairing.HostId });
        await WaitUntil(() => device.HasLink(2), "second link");
        Assert.Single(_provider.Devices);

        _mux.Detach(1);
        await WaitUntil(() => device.Summary.Connection == ConnectionKind.Wifi, "Wi-Fi only");
        Assert.Single(_provider.Devices);

        _mux.Detach(2);
        await WaitUntil(() => _provider.Devices.Count == 0, "removal");
    }

    [Fact]
    public async Task Service_restart_removes_and_re_adds_devices()
    {
        await AttachAsync(PairedPhone());

        _mux.DropListeners();
        await WaitUntil(() => _events.Any(e => e.Change == DeviceChange.Removed), "removal");
        await WaitUntil(() => _events.Count(e => e.Change == DeviceChange.Added) == 2 && _provider.Find(Udid)?.Summary.State == DeviceState.Ready, "re-add");
    }

    [Fact]
    public async Task Storage_battery_and_details()
    {
        var phone = PairedPhone();
        phone.Domains["com.apple.disk_usage"] = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "disk_usage.xml"));
        phone.Domains["com.apple.mobile.battery"] = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "battery_domain.xml"));
        phone.PowerSource = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "ioreg_powersource.xml"));
        var device = await AttachAsync(phone);

        var storage = await device.GetStorageAsync(Ct);
        Assert.Equal(256_000_000_000, storage.Volumes[0].TotalBytes);

        var battery = await device.GetBatteryAsync(Ct);
        Assert.Equal((76, 412, 92, 30.5), (battery.LevelPercent, battery.CycleCount, battery.HealthPercent, battery.TemperatureCelsius));

        var details = await device.GetDetailsAsync(Ct);
        Assert.Contains(details.Rows, r => r.Key == "imei" && r.Sensitive);
    }

    [Fact]
    public async Task Storage_on_ios27_asks_for_app_usage_by_name_and_splits_videos_from_dcim()
    {
        var phone = PairedPhone();
        // As on iOS 27: the main dump has neither app nor camera usage; they come by name, the camera roll
        // from the ".factory" domain.
        var dump = PlistSerializer.ParseDict(Fixture.Bytes("Plist", "disk_usage_ios27.xml"));
        dump.Remove("CameraUsage");
        dump.Remove("PhotoUsage");
        phone.Domains["com.apple.disk_usage"] = dump;
        phone.ByNameOnly["com.apple.disk_usage"] = new PlistDict { ["MobileApplicationUsage"] = 36_800_000_000L };
        phone.ByNameOnly["com.apple.disk_usage.factory"] = new PlistDict { ["CameraUsage"] = 26_000_000_000L };
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0001.HEIC", new byte[100]);
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0001.MOV", new byte[50]);   // Live Photo motion: counts as photo
        phone.Media.AddFile("/DCIM/101APPLE/IMG_0102.MOV", new byte[4000]); // a video
        phone.Media.AddFile("/DCIM/101APPLE/IMG_0103.MP4", new byte[1000]); // a video
        var device = await AttachAsync(phone);

        var c = (await device.GetStorageAsync(Ct)).Volumes[0].Categories.ToDictionary(x => x.Key, x => x.Bytes);
        Assert.Equal(36_800_000_000, c["apps"]);
        Assert.Equal(5000, c["videos"]);
        Assert.Equal(26_000_000_000 - 5000, c["photos"]);
        Assert.DoesNotContain(phone.Log, l => l == "instproxy:Browse"); // no slow size scan
        Assert.DoesNotContain(phone.Log, l => l == "lockdown:GetValue com.apple.disk_usage.factory *"); // no slow full dump

        // The video sizes are cached: a second call does not stat the camera roll again.
        var stats = phone.Media.Operations.Count(o => o == Protocol.Afc.AfcOperation.GetFileInfo);
        await device.GetStorageAsync(Ct);
        Assert.Equal(stats, phone.Media.Operations.Count(o => o == Protocol.Afc.AfcOperation.GetFileInfo));
    }

    [Fact]
    public async Task File_roots_media_and_app_documents()
    {
        var phone = PairedPhone();
        phone.Apps.Add(App("com.example.docs", "Docs", fileSharing: true));
        phone.Apps.Add(App("com.example.game", "Game", fileSharing: false));
        phone.AppContainers["com.example.docs"] = new FakeAfcServer().AddFile("/Documents/report.pdf", [1, 2, 3]).AddFile("/Library/secret", [9]);
        phone.Media.AddFile("/Downloads/a.txt", "hello"u8.ToArray());
        var device = await AttachAsync(phone);

        var roots = await device.GetFileRootsAsync(Ct);
        Assert.Equal(["media", "app:com.example.docs"], roots.Select(r => r.Key));
        Assert.Equal((FileRootKind.AppDocuments, "Docs", "com.example.docs"), (roots[1].Kind, roots[1].Label, roots[1].AppId));

        await using (var docs = await device.OpenFileSystemAsync("app:com.example.docs", Ct))
        {
            var entries = await ToListAsync(docs.ListAsync("/", Ct));
            var entry = Assert.Single(entries);
            Assert.Equal(("/report.pdf", "report.pdf", false, 3L), (entry.Path, entry.Name, entry.IsDirectory, entry.Size));
        }

        await using (var media = await device.OpenFileSystemAsync("media", Ct))
        {
            using var read = new MemoryStream();
            await media.ReadAsync("/Downloads/a.txt", read, null, Ct);
            Assert.Equal("hello"u8.ToArray(), read.ToArray());

            await media.CreateDirectoryAsync("/Downloads/New", Ct);
            await media.WriteAsync("/Downloads/New/b.bin", new MemoryStream([5, 6, 7]), 3, null, Ct);
            Assert.Equal(3, (await media.StatAsync("/Downloads/New/b.bin", Ct))!.Size);
            await media.RenameAsync("/Downloads/New", "/Downloads/Renamed", Ct);
            Assert.Null(await media.StatAsync("/Downloads/New", Ct));
            await media.DeleteAsync("/Downloads/Renamed", recursive: true, Ct);
            Assert.Null(await media.StatAsync("/Downloads/Renamed", Ct));

            Assert.Equal(DeviceErrorCodes.PermissionDenied,
                (await Assert.ThrowsAsync<DeviceException>(() => media.StatAsync("/Downloads/../../etc", Ct))).Code);
            Assert.Equal(DeviceErrorCodes.PathNotFound,
                (await Assert.ThrowsAsync<DeviceException>(() => media.ReadAsync("/nope", Stream.Null, null, Ct))).Code);
        }

        Assert.Equal(DeviceErrorCodes.NotFound,
            (await Assert.ThrowsAsync<DeviceException>(() => device.OpenFileSystemAsync("app:com.example.game", Ct))).Code);
        Assert.Equal(DeviceErrorCodes.PathNotFound,
            (await Assert.ThrowsAsync<DeviceException>(() => device.OpenFileSystemAsync("sd:1", Ct))).Code);
    }

    [Fact]
    public async Task Apps_list_icon_install_and_uninstall()
    {
        var phone = PairedPhone();
        phone.Apps.Add(App("com.example.docs", "Docs", fileSharing: true, staticBytes: 50_000_000, dynamicBytes: 7_000_000));
        phone.Apps.Add(App("com.example.dev", "Dev Build", signer: "Apple Development: Someone (ABCDE12345)"));
        phone.Apps.Add(App("com.apple.mobilesafari", "Safari", type: "System"));
        phone.Icons["com.example.docs"] = [0x89, 0x50, 0x4E, 0x47];
        var device = await AttachAsync(phone);

        var user = await device.Apps.ListAsync(includeSystem: false, Ct);
        Assert.Equal(["Dev Build", "Docs"], user.Select(a => a.Name));
        var docs = user.Single(a => a.Id == "com.example.docs");
        Assert.Equal((AppSource.Store, true, 50_000_000L, 7_000_000L, "1.2"), (docs.Source, docs.FileSharing, docs.AppBytes, docs.DataBytes, docs.Version));
        Assert.Equal(AppSource.Sideloaded, user.Single(a => a.Id == "com.example.dev").Source);
        Assert.Contains(await device.Apps.ListAsync(includeSystem: true, Ct), a => a.Source == AppSource.System);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, await device.Apps.GetIconAsync("com.example.docs", Ct));
        Assert.Null(await device.Apps.GetIconAsync("com.example.none", Ct));

        var ipa = Path.Combine(_dir, "App.ipa");
        await File.WriteAllBytesAsync(ipa, new byte[300_000], Ct);
        var stages = new List<InstallProgress>();
        await device.Apps.InstallAsync([ipa], new SyncProgress<InstallProgress>(stages.Add), Ct);
        Assert.Equal("upload", stages[0].Stage);
        Assert.Contains(stages, s => s.Stage == "upload" && s.Percent == 100);
        Assert.Contains(stages, s => s.Stage == "installingApplication" && s.Percent == 60);
        Assert.Equal("complete", stages[^1].Stage);
        var installed = phone.Log.Single(l => l.StartsWith("install:", StringComparison.Ordinal));
        Assert.StartsWith("install:PublicStaging/devicedeck-", installed);
        Assert.False(phone.Media.Exists("/" + installed["install:".Length..]), "the staged .ipa is cleaned up");

        phone.InstallError = "ApplicationVerificationFailed";
        Assert.Equal(DeviceErrorCodes.InstallFailed,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Apps.InstallAsync([ipa], null, Ct))).Code);
        Assert.Equal(DeviceErrorCodes.InstallFailed,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Apps.InstallAsync([ipa, ipa], null, Ct))).Code);

        await device.Apps.UninstallAsync("com.example.dev", Ct);
        Assert.DoesNotContain(phone.Apps, a => a.GetString("CFBundleIdentifier") == "com.example.dev");
        Assert.Equal(DeviceErrorCodes.NotFound,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Apps.UninstallAsync("com.example.dev", Ct))).Code);
        Assert.Equal(DeviceErrorCodes.NotSupported,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Apps.ExportAsync("com.example.docs", _dir, Ct))).Code);
    }

    [Fact]
    public async Task Photos_from_photos_sqlite_with_thumbnails_and_originals()
    {
        var phone = PairedPhone();
        var db = PhotosTests.CreateDatabase("ios17.sql", Path.Combine(_dir, "Photos.sqlite"));
        SqliteConnection.ClearAllPools();
        phone.Media.AddFile("/PhotoData/Photos.sqlite", await File.ReadAllBytesAsync(db, Ct));
        foreach (var f in PhotosTests.DcimFiles)
            phone.Media.AddFile("/" + f, System.Text.Encoding.UTF8.GetBytes(f));
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0001.AAE", [0]);
        phone.Media.AddFile("/PhotoData/Thumbnails/V2/DCIM/100APPLE/IMG_0001.JPG/5003.JPG", PhotosTests.TinyJpeg(120, 90));
        phone.Media.AddFile("/PhotoData/Thumbnails/V2/DCIM/100APPLE/IMG_0001.JPG/5005.JPG", PhotosTests.TinyJpeg(480, 360));
        var device = await AttachAsync(phone);

        var albums = await ToListAsync(device.Photos.GetAlbumsAsync(Ct));
        Assert.Contains(albums, a => a.Id == "album:ALBUM-TRIP" && a.Count == 2);

        var page = await device.Photos.GetAssetsAsync(null, 0, 50, Ct);
        Assert.Equal(6, page.Total);
        var loose = page.Items.Single(a => a.Id == "DCIM/100APPLE/IMG_0007.JPG");
        Assert.Equal("DCIM/100APPLE/IMG_0007.JPG".Length, loose.Size);   // filled by a stat
        Assert.NotNull(loose.TakenAt);

        Assert.Equal((480, 360), PhotosTestsJpeg(await device.Photos.GetThumbnailAsync("DCIM/100APPLE/IMG_0001.JPG", 256, Ct)));
        Assert.Equal((120, 90), PhotosTestsJpeg(await device.Photos.GetThumbnailAsync("DCIM/100APPLE/IMG_0001.JPG", 100, Ct)));
        Assert.Null(await device.Photos.GetThumbnailAsync("DCIM/100APPLE/IMG_0004.PNG", 256, Ct));

        using var original = new MemoryStream();
        await device.Photos.ReadOriginalAsync("DCIM/100APPLE/IMG_0002.MOV", original, null, Ct);
        Assert.Equal("DCIM/100APPLE/IMG_0002.MOV", System.Text.Encoding.UTF8.GetString(original.ToArray()));

        Assert.Equal(DeviceErrorCodes.NotFound,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Photos.ReadOriginalAsync("../secret", Stream.Null, null, Ct))).Code);
        Assert.Equal(DeviceErrorCodes.NotSupported,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Photos.DeleteAsync(["DCIM/100APPLE/IMG_0001.JPG"], Ct))).Code);
        Assert.Equal(DeviceErrorCodes.NotSupported,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Photos.ImportAsync("x.jpg", Stream.Null, 0, null, Ct))).Code);
    }

    [Fact]
    public async Task Thumbnails_and_icons_share_one_connection_and_survive_a_dropped_one()
    {
        var phone = PairedPhone();
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0001.JPG", [1]);
        phone.Media.AddFile("/PhotoData/Thumbnails/V2/DCIM/100APPLE/IMG_0001.JPG/5005.JPG", PhotosTests.TinyJpeg(480, 360));
        phone.Icons["com.example.docs"] = [0x89, 0x50, 0x4E, 0x47];
        var device = await AttachAsync(phone);
        int Connections(string service) => phone.Log.Count(l => l == "service:" + service);
        var afcBefore = Connections("com.apple.afc"); // the handshake's lock probe connects once

        var thumbs = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => device.Photos.GetThumbnailAsync("DCIM/100APPLE/IMG_0001.JPG", 256, Ct)));
        Assert.All(thumbs, t => Assert.NotNull(t));
        Assert.Equal(afcBefore + 1, Connections("com.apple.afc"));

        var icons = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => device.Apps.GetIconAsync("com.example.docs", Ct)));
        Assert.All(icons, i => Assert.Equal(4, i!.Length));
        Assert.Equal(1, Connections("com.apple.springboardservices"));

        // afcd hangs up mid-way: the next call reconnects once and still succeeds.
        phone.Media.HangUpAfterOperations = phone.Media.Operations.Count + 1;
        Assert.NotNull(await device.Photos.GetThumbnailAsync("DCIM/100APPLE/IMG_0001.JPG", 256, Ct));
        Assert.Equal(afcBefore + 2, Connections("com.apple.afc"));
    }

    [Fact]
    public async Task Photos_sqlite_copy_reuses_the_main_file_and_survives_a_dropped_connection()
    {
        var phone = PairedPhone();
        var live = Path.Combine(_dir, "live.sqlite");
        await using var writer = new SqliteConnection($"Data Source={live};Pooling=False");
        writer.Open();
        await using (var cmd = writer.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;" + Fixture.Text("Photos", "ios17.sql");
            cmd.ExecuteNonQuery();
        }
        var mainBytes = ReadShared(live);
        var walBytes = ReadShared(live + "-wal");
        var t0 = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        phone.Media.AddFile("/PhotoData/Photos.sqlite", mainBytes, t0);
        phone.Media.AddFile("/PhotoData/Photos.sqlite-wal", walBytes, t0);
        foreach (var f in PhotosTests.DcimFiles)
            phone.Media.AddFile("/" + f, [1]);
        // The first copy loses its connection half-way and is retried on a fresh one.
        phone.Media.HangUpWhileReading = "/PhotoData/Photos.sqlite";
        var device = await AttachAsync(phone);
        var library = (Device.Photos.IosPhotoLibrary)device.Photos;

        var first = await library.GetIndexAsync(refresh: true, Ct);
        Assert.Equal(Device.Photos.PhotoIndexMode.Database, first.Mode);
        Assert.Contains("WAL", first.Note);
        Assert.Contains("attempt 2", first.Note);
        Assert.Equal(6, first.Page(null, 0, 100).Total);

        // Nothing changed on the phone: the cached copy is reused.
        var second = await library.GetIndexAsync(refresh: true, Ct);
        Assert.StartsWith("reused the cached copy", second.Note);

        // Only the WAL changed (Photos appended to it): only the WAL is copied again.
        phone.Media.AddFile("/PhotoData/Photos.sqlite-wal", walBytes, t0.AddMinutes(1));
        var third = await library.GetIndexAsync(refresh: true, Ct);
        Assert.Matches(@"^copied [0-9.]+ KB WAL in", third.Note);
        Assert.Equal(6, third.Page(null, 0, 100).Total);
    }

    private static byte[] ReadShared(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var m = new MemoryStream();
        f.CopyTo(m);
        return m.ToArray();
    }

    [Fact]
    public async Task Photos_fall_back_to_dcim_when_the_database_is_unreadable()
    {
        var phone = PairedPhone();
        phone.Media.AddFile("/PhotoData/Photos.sqlite", [1, 2, 3]);
        phone.Media.Forbidden.Add("/PhotoData/Photos.sqlite");
        foreach (var f in PhotosTests.DcimFiles)
            phone.Media.AddFile("/" + f, [1]);
        var device = await AttachAsync(phone);

        var albums = await ToListAsync(device.Photos.GetAlbumsAsync(Ct));
        Assert.Contains(albums, a => a.Id == "dcim:100APPLE");
        Assert.Equal(7, (await device.Photos.GetAssetsAsync("all", 0, 100, Ct)).Total);
    }

    [Fact]
    public async Task Restart_and_shutdown_through_diagnostics_relay()
    {
        var phone = PairedPhone();
        var device = await AttachAsync(phone);

        await device.Actions.RebootAsync(RebootTarget.System, Ct);
        await device.Actions.ShutdownAsync(Ct);
        Assert.Contains("diagnostics:Restart", phone.Log);
        Assert.Contains("diagnostics:Shutdown", phone.Log);

        Assert.Equal(DeviceErrorCodes.NotSupported,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Actions.RebootAsync(RebootTarget.Recovery, Ct))).Code);
        Assert.Equal(DeviceErrorCodes.NotSupported,
            (await Assert.ThrowsAsync<DeviceException>(() => device.Actions.TakeScreenshotAsync(Ct))).Code);
    }

    [Fact]
    public async Task Driver_status_is_ready_when_usbmux_answers()
    {
        var status = await _provider.GetDriverStatusAsync(Ct);
        Assert.Equal(("ios", DriverState.Ready), (status.ProviderId, status.State));
    }

    private static (int, int)? PhotosTestsJpeg(byte[]? bytes) => bytes is null ? null : Device.Photos.JpegInfo.Size(bytes);

    private static PlistDict App(string id, string name, bool fileSharing = false, string type = "User",
        string signer = "Apple iPhone OS Application Signing", long staticBytes = 1000, long dynamicBytes = 10) => new()
    {
        ["CFBundleIdentifier"] = id,
        ["CFBundleDisplayName"] = name,
        ["CFBundleShortVersionString"] = "1.2",
        ["CFBundleVersion"] = "120",
        ["ApplicationType"] = type,
        ["SignerIdentity"] = signer,
        ["UIFileSharingEnabled"] = fileSharing,
        ["StaticDiskUsage"] = staticBytes,
        ["DynamicDiskUsage"] = dynamicBytes,
    };

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> items)
    {
        var list = new List<T>();
        await foreach (var item in items)
            list.Add(item);
        return list;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
