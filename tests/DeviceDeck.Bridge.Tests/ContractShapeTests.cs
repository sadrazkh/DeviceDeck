using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Serialization;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The JSON the page sees, checked against docs/bridge-contract.md and the frozen C# shapes.</summary>
public sealed class ContractShapeTests
{
    private static JsonObject ToJson(object value) => JsonSerializer.SerializeToNode(value, value.GetType(), DeviceDeckJson.Options)!.AsObject();

    private static string[] Keys(JsonObject node) => node.Select(p => p.Key).ToArray();

    [Fact]
    public void Device_summary_is_camelCase_with_string_enums_and_capability_names()
    {
        var summary = FakeSampleData.Iphone().Summary with { State = DeviceState.AwaitingTrust };
        var json = ToJson(summary);

        Assert.Equal(["id", "name", "platform", "model", "marketingName", "osVersion", "connection", "state", "mode", "capabilities", "colorHex"], Keys(json));
        Assert.Equal("ios", (string?)json["platform"]);
        Assert.Equal("usb", (string?)json["connection"]);
        Assert.Equal("awaitingTrust", (string?)json["state"]);
        Assert.Equal("full", (string?)json["mode"]);
        var caps = json["capabilities"]!.AsArray().Select(c => (string)c!).ToArray();
        Assert.Equal(["info", "storage", "battery", "photosRead", "photosDelete", "files", "appDocuments"], caps[..7]);
        Assert.DoesNotContain("photosImport", caps);
        Assert.Contains("crashReports", caps);
    }

    [Fact]
    public void Capabilities_round_trip_and_reject_unknown_names()
    {
        var all = Enum.GetValues<DeviceCapability>().Aggregate(DeviceCapability.None, (a, b) => a | b);
        var json = JsonSerializer.Serialize(all, DeviceDeckJson.Options);
        Assert.Equal(24, JsonNode.Parse(json)!.AsArray().Count);
        Assert.Equal(all, JsonSerializer.Deserialize<DeviceCapability>(json, DeviceDeckJson.Options));
        Assert.Equal("[]", JsonSerializer.Serialize(DeviceCapability.None, DeviceDeckJson.Options));
        Assert.Equal("[\"screenMirror\",\"rebootToBootloader\"]", JsonSerializer.Serialize(DeviceCapability.ScreenMirror | DeviceCapability.RebootToBootloader, DeviceDeckJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeviceCapability>("[\"teleport\"]", DeviceDeckJson.Options));
    }

    [Fact]
    public void Enums_are_camelCase_strings()
    {
        Assert.Equal("\"waitingForDevice\"", JsonSerializer.Serialize(TransferState.WaitingForDevice, DeviceDeckJson.Options));
        Assert.Equal("\"needsDecision\"", JsonSerializer.Serialize(TransferState.NeedsDecision, DeviceDeckJson.Options));
        Assert.Equal("\"keepBoth\"", JsonSerializer.Serialize(ConflictChoice.KeepBoth, DeviceDeckJson.Options));
        Assert.Equal("\"copyToPc\"", JsonSerializer.Serialize(TransferKind.CopyToPc, DeviceDeckJson.Options));
        Assert.Equal("\"appDocuments\"", JsonSerializer.Serialize(FileRootKind.AppDocuments, DeviceDeckJson.Options));
        Assert.Equal("\"livePhoto\"", JsonSerializer.Serialize(PhotoKind.LivePhoto, DeviceDeckJson.Options));
        Assert.Equal("\"bootloader\"", JsonSerializer.Serialize(RebootTarget.Bootloader, DeviceDeckJson.Options));
        Assert.Equal(RebootTarget.Recovery, JsonSerializer.Deserialize<RebootTarget>("\"recovery\"", DeviceDeckJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ConflictChoice>("1", DeviceDeckJson.Options));
    }

    [Fact]
    public void Transfer_job_has_exactly_the_contract_fields()
    {
        var job = new TransferJob("j1", "d1", TransferKind.PhotosExport, "Exporting 1,284 photos to D:\\Photos\\2026", TransferState.NeedsDecision,
            1284, 800, 2, 4_000_000_000, 2_500_000_000, 38_000_000, 120, new TransferConflict("IMG_4412.HEIC", 10, 20), null,
            new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(3.5)), null);
        var json = ToJson(job);

        Assert.Equal(["id", "deviceId", "kind", "title", "state", "totalItems", "doneItems", "failedItems", "totalBytes", "doneBytes",
            "bytesPerSecond", "etaSeconds", "conflict", "error", "createdAt", "finishedAt"], Keys(json));
        Assert.Equal("photosExport", (string?)json["kind"]);
        Assert.Equal("needsDecision", (string?)json["state"]);
        Assert.Equal(["itemName", "existingSize", "newSize"], Keys(json["conflict"]!.AsObject()));
        Assert.Null(json["error"]);
        Assert.Null(json["finishedAt"]);
        Assert.Equal("2026-10-20T09:30:00+03:30", (string?)json["createdAt"]);

        var failed = ToJson(job with { State = TransferState.Failed, Conflict = null, Error = new TransferError("device_disconnected", "The phone was disconnected."), BytesPerSecond = null, EtaSeconds = null });
        Assert.Equal(["code", "message"], Keys(failed["error"]!.AsObject()));
        Assert.Null(failed["conflict"]);
        Assert.Null(failed["bytesPerSecond"]);
    }

    [Fact]
    public void Settings_have_exactly_the_contract_fields_and_values()
    {
        var json = ToJson(new AppSettings { PhotoExportFolder = @"D:\Photos", FileCopyFolder = @"D:\Copies" });
        Assert.Equal(["language", "theme", "density", "photoExportFolder", "photoFolderPattern", "convertHeic", "fileCopyFolder", "conflictRule",
            "backupFolder", "backupKeep"], Keys(json));
        Assert.Equal("en", (string?)json["language"]);
        Assert.Equal("system", (string?)json["theme"]);
        Assert.Equal("comfortable", (string?)json["density"]);
        Assert.Equal("yyyy/MM", (string?)json["photoFolderPattern"]);
        Assert.False((bool)json["convertHeic"]!);
        Assert.Equal("ask", (string?)json["conflictRule"]);
        Assert.Equal(3, (int)json["backupKeep"]!);
        Assert.Equal("\"fa\"", JsonSerializer.Serialize(AppLanguage.Fa, DeviceDeckJson.Options));
        Assert.Equal("\"compact\"", JsonSerializer.Serialize(UiDensity.Compact, DeviceDeckJson.Options));
    }

    [Fact]
    public void Models_serialise_dates_as_iso_and_sizes_as_numbers()
    {
        var asset = FakeSampleData.Iphone().Assets[0];
        var json = ToJson(asset);
        Assert.Equal(["id", "fileName", "kind", "takenAt", "size", "width", "height", "durationSeconds", "isFavorite", "inCloudOnly", "livePairId"], Keys(json));
        Assert.True(DateTimeOffset.TryParse((string?)json["takenAt"], out _));
        Assert.Equal(JsonValueKind.Number, json["size"]!.GetValueKind());

        var entry = ToJson(new FileEntry("/Movies/a.mkv", "a.mkv", false, 12, null));
        Assert.Equal(["path", "name", "isDirectory", "size", "modified", "readOnly"], Keys(entry));

        var app = ToJson(FakeSampleData.Iphone().Apps[0].Info);
        Assert.Equal(["id", "name", "version", "appBytes", "dataBytes", "source", "fileSharing", "installedAt"], Keys(app));
        Assert.Equal("store", (string?)app["source"]);

        var page = ToJson(new Page<PhotoAsset>([asset], 1));
        Assert.Equal(["items", "total"], Keys(page));
    }

    [Fact]
    public async Task Methods_answer_in_contract_shapes_end_to_end()
    {
        await using var host = await TestHost.StartAsync();

        var hello = (await host.ResultAsync("app.hello"))!.AsObject();
        Assert.Equal(["version", "isWebView"], Keys(hello));
        Assert.True((bool)hello["isWebView"]!);

        var devices = (await host.ResultAsync("device.list"))!.AsArray();
        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal(JsonValueKind.Array, d!["capabilities"]!.GetValueKind()));

        var drivers = (await host.ResultAsync("drivers.status"))!.AsArray();
        Assert.Equal(["providerId", "state", "version", "detail"], Keys(drivers[0]!.AsObject()));
        Assert.Equal("ready", (string?)drivers[0]!["state"]);

        var storage = (await host.ResultAsync("device.storage", new { deviceId = TestPhones.IphoneId }))!.AsObject();
        Assert.Equal(["key", "label", "totalBytes", "freeBytes", "categories"], Keys(storage["volumes"]![0]!.AsObject()));

        var battery = (await host.ResultAsync("device.battery", new { deviceId = TestPhones.IphoneId }))!.AsObject();
        Assert.Equal(["levelPercent", "isCharging", "healthPercent", "cycleCount", "temperatureCelsius", "designCapacityMah", "fullChargeCapacityMah"], Keys(battery));

        var details = (await host.ResultAsync("device.details", new { deviceId = TestPhones.IphoneId }))!.AsObject();
        Assert.Equal(["key", "label", "value", "sensitive"], Keys(details["rows"]![0]!.AsObject()));

        var albums = (await host.ResultAsync("photos.albums", new { deviceId = TestPhones.IphoneId }))!.AsArray();
        Assert.Equal(["id", "name", "kind", "count"], Keys(albums[0]!.AsObject()));
        Assert.Equal("all", (string?)albums[0]!["kind"]);

        var list = (await host.ResultAsync("photos.list", new { deviceId = TestPhones.IphoneId, offset = 1, limit = 2 }))!.AsObject();
        Assert.Equal(4, (int)list["total"]!);
        Assert.Equal(2, list["items"]!.AsArray().Count);

        var favourites = (await host.ResultAsync("photos.list", new { deviceId = TestPhones.IphoneId, albumId = "favorites", offset = 0, limit = 10 }))!.AsObject();
        Assert.Equal(1, (int)favourites["total"]!);

        var roots = (await host.ResultAsync("files.roots", new { deviceId = TestPhones.IphoneId }))!.AsArray();
        Assert.Equal(["key", "label", "kind", "appId"], Keys(roots[0]!.AsObject()));

        var files = (await host.ResultAsync("files.list", new { deviceId = TestPhones.IphoneId, root = "app:org.videolan.vlc-ios", path = "/" }))!.AsArray();
        Assert.Equal(["/Locked", "/Movie.mkv", "/Subtitles"], files.Select(f => (string)f!["path"]!).Order(StringComparer.Ordinal));

        var apps = (await host.ResultAsync("apps.list", new { deviceId = TestPhones.AndroidId, includeSystem = false }))!.AsArray();
        Assert.Single(apps);

        var settings = (await host.ResultAsync("settings.get"))!.AsObject();
        Assert.Equal(10, settings.Count);

        var shot = (await host.ResultAsync("tools.screenshot", new { deviceId = TestPhones.IphoneId }))!.AsObject();
        Assert.StartsWith("https://device.local/shot/test-iphone/", (string?)shot["url"]);

        var none = (await host.ResultAsync("apps.uninstall", new { deviceId = TestPhones.IphoneId, appId = "org.videolan.vlc-ios" }))!.AsObject();
        Assert.Empty(none);

        var picked = (await host.ResultAsync("shell.pickFolder", new { title = "Where to?" }))!.AsObject();
        Assert.Equal(@"C:\Picked", (string?)picked["path"]);
        host.Shell.NextFolder = null;
        var cancelled = (await host.ResultAsync("shell.pickFolder"))!.AsObject();
        Assert.True(cancelled.ContainsKey("path"));
        Assert.Null(cancelled["path"]);
    }

    [Fact]
    public async Task File_methods_change_the_phone()
    {
        await using var host = await TestHost.StartAsync();
        const string root = "app:org.videolan.vlc-ios";
        var made = (await host.ResultAsync("files.mkdir", new { deviceId = TestPhones.IphoneId, root, path = "/New folder" }))!.AsObject();
        Assert.True((bool)made["isDirectory"]!);

        var renamed = (await host.ResultAsync("files.rename", new { deviceId = TestPhones.IphoneId, root, from = "/New folder", to = "Films" }))!.AsObject();
        Assert.Equal("/Films", (string?)renamed["path"]);

        var deleted = (await host.ResultAsync("files.delete", new { deviceId = TestPhones.IphoneId, root, paths = new[] { "/Films", "/Movie.mkv" }, recursive = false }))!.AsObject();
        Assert.Equal(2, (int)deleted["deleted"]!);

        var locked = await host.CallAsync("files.mkdir", new { deviceId = TestPhones.IphoneId, root, path = "/Locked/x" });
        Assert.Equal("permission_denied", (string?)locked["error"]!["data"]!["code"]);

        var missing = await host.CallAsync("files.list", new { deviceId = TestPhones.IphoneId, root, path = "/nope" });
        Assert.Equal("path_not_found", (string?)missing["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Device_errors_reach_the_page_with_their_codes()
    {
        await using var host = await TestHost.StartAsync();

        var unknown = await host.CallAsync("device.storage", new { deviceId = "nobody" });
        Assert.Equal(-32000, (int)unknown["error"]!["code"]!);
        Assert.Equal("device_not_found", (string?)unknown["error"]!["data"]!["code"]);

        host.Fake.SetState(TestPhones.IphoneId, DeviceState.AwaitingTrust);
        var trust = await host.CallAsync("device.battery", new { deviceId = TestPhones.IphoneId });
        Assert.Equal("trust_required", (string?)trust["error"]!["data"]!["code"]);

        host.Fake.SetState(TestPhones.AndroidId, DeviceState.Unauthorized);
        var unauthorized = await host.CallAsync("apps.list", new { deviceId = TestPhones.AndroidId });
        Assert.Equal("unauthorized", (string?)unauthorized["error"]!["data"]!["code"]);

        host.Fake.SetState(TestPhones.IphoneId, DeviceState.Ready);
        var export = await host.CallAsync("apps.export", new { deviceId = TestPhones.IphoneId, appId = "org.videolan.vlc-ios", destination = host.PcFolder("apps") });
        Assert.Contains((string?)export["error"]!["data"]!["code"], new[] { "not_supported" });

        var badPage = await host.CallAsync("photos.list", new { deviceId = TestPhones.IphoneId, offset = 0, limit = 0 });
        Assert.Equal(-32602, (int)badPage["error"]!["code"]!);

        var noJob = await host.CallAsync("transfers.pause", new { jobId = "nope" });
        Assert.Equal(-32602, (int)noJob["error"]!["code"]!);
    }

    [Fact]
    public async Task Settings_set_merges_a_partial_object_and_persists_it()
    {
        await using var host = await TestHost.StartAsync();
        var updated = (await host.ResultAsync("settings.set", new { language = "fa", conflictRule = "keepBoth" }))!.AsObject();
        Assert.Equal("fa", (string?)updated["language"]);
        Assert.Equal("keepBoth", (string?)updated["conflictRule"]);
        Assert.Equal("system", (string?)updated["theme"]);

        var reloaded = new SettingsStore(host.Paths.SettingsFile).Current;
        Assert.Equal(AppLanguage.Fa, reloaded.Language);
        Assert.Equal(ConflictRule.KeepBoth, reloaded.ConflictRule);

        foreach (var bad in new object[] { new { language = "de" }, new { photoFolderPattern = "yyyy" }, new { colour = "red" }, new { convertHeic = "yes" } })
        {
            var reply = await host.CallAsync("settings.set", bad);
            Assert.Equal(-32602, (int)reply["error"]!["code"]!);
        }

        Assert.Equal(AppLanguage.Fa, host.Settings.Current.Language);
    }

    [Fact]
    public async Task Events_reach_the_page()
    {
        await using var host = await TestHost.StartAsync();
        host.Fake.Disconnect(TestPhones.AndroidId);
        host.Fake.Reconnect(TestPhones.AndroidId);
        host.Fake.SetState(TestPhones.AndroidId, DeviceState.Unauthorized);

        await Wait.UntilAsync(() => host.Transport.EventsNamed("device.changed").Any(), "device.changed");
        var removed = host.Transport.EventsNamed("device.removed").Single();
        Assert.Equal(["deviceId"], Keys(removed["params"]!.AsObject()));
        Assert.Equal(TestPhones.AndroidId, (string?)removed["params"]!["deviceId"]);
        Assert.Equal("ready", (string?)host.Transport.EventsNamed("device.added").Single()["params"]!["state"]);
        Assert.Equal("unauthorized", (string?)host.Transport.EventsNamed("device.changed").Single()["params"]!["state"]);
    }
}
