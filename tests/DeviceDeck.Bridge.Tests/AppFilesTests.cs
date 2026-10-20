using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>appFiles.* over the bridge, and copies in and out of app folders, with the fake phones.</summary>
public sealed class AppFilesTests
{
    private const string Vlc = "app:org.videolan.vlc-ios";

    private static string[] Keys(JsonNode? node) => node!.AsObject().Select(p => p.Key).ToArray();

    [Fact]
    public async Task List_on_an_iphone_has_the_contract_shape()
    {
        await using var host = await TestHost.StartAsync(TestPhones.Iphone());

        var result = await host.ResultAsync("appFiles.list", new { deviceId = TestPhones.IphoneId });

        Assert.Equal(["entries", "privateData"], Keys(result));
        Assert.Equal("notApplicable", (string?)result!["privateData"]);
        var vlc = result["entries"]!.AsArray().Single()!;
        Assert.Equal(["id", "name", "kind", "appId", "folders"], Keys(vlc));
        Assert.Equal(("org.videolan.vlc-ios", "VLC", "app", "org.videolan.vlc-ios"), ((string?)vlc["id"], (string?)vlc["name"], (string?)vlc["kind"], (string?)vlc["appId"]));
        var folder = vlc["folders"]!.AsArray().Single()!;
        Assert.Equal(["rootKey", "label", "empty"], Keys(folder));
        Assert.Equal((Vlc, "Documents", false), ((string?)folder["rootKey"], (string?)folder["label"], (bool)folder["empty"]!));
    }

    [Fact]
    public async Task List_on_the_sample_android_has_app_folders_and_shared_folders_and_says_android_data_is_closed()
    {
        await using var host = await TestHost.StartAsync(FakeSampleData.Android());

        var result = await host.ResultAsync("appFiles.list", new { deviceId = FakeSampleData.AndroidId });

        Assert.Equal("blocked", (string?)result!["privateData"]);
        var entries = result["entries"]!.AsArray().Select(e => e!).ToList();
        Assert.Equal(["com.spotify.music", "org.telegram.messenger", "com.whatsapp", "shared:Documents", "shared:Download"], entries.Select(e => (string?)e["id"]));
        Assert.Equal(["Spotify", "Telegram", "WhatsApp"], entries.Take(3).Select(e => (string?)e["name"]));
        Assert.True((bool)entries[0]["folders"]![0]!["empty"]!);
        Assert.Equal(("sharedFolder", null), ((string?)entries[3]["kind"], (string?)entries[3]["appId"]));
        Assert.Equal("appmedia:com.whatsapp", (string?)entries[2]["folders"]![0]!["rootKey"]);

        // Each folder browses like any root; Android/data stays closed on this phone.
        var whatsapp = await host.ResultAsync("files.list", new { deviceId = FakeSampleData.AndroidId, root = "appmedia:com.whatsapp", path = "/" });
        Assert.Equal("WhatsApp", (string?)whatsapp!.AsArray().Single()!["name"]);
        var data = await host.CallAsync("files.list", new { deviceId = FakeSampleData.AndroidId, root = "appdata:com.whatsapp", path = "/" });
        Assert.Equal("permission_denied", (string?)data["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Measure_counts_everything_under_each_path()
    {
        await using var host = await TestHost.StartAsync(TestPhones.Iphone());

        var result = await host.ResultAsync("appFiles.measure", new { deviceId = TestPhones.IphoneId, root = Vlc, paths = new[] { "/", "/Subtitles", "/nope" } });

        Assert.Equal(["items", "files", "folders", "bytes"], Keys(result));
        Assert.Equal((2, 2, 302_000L), ((int)result!["files"]!, (int)result["folders"]!, (long)result["bytes"]!));
        Assert.Equal(["path", "files", "folders", "bytes"], Keys(result["items"]![0]));
        Assert.Equal([(2, 2, 302_000L), (1, 0, 2_000L), (0, 0, 0L)],
            result["items"]!.AsArray().Select(i => ((int)i!["files"]!, (int)i["folders"]!, (long)i["bytes"]!)));
    }

    [Fact]
    public async Task Bad_requests_and_phones_without_app_files_are_refused()
    {
        var plain = TestPhones.Android(id: "plain");
        plain = new FakeDeviceSpec
        {
            Summary = plain.Summary with { Capabilities = plain.Summary.Capabilities & ~Core.Abstractions.DeviceCapability.AppDocuments },
            Storage = plain.Storage, Battery = plain.Battery, Details = plain.Details, Assets = plain.Assets, Albums = plain.Albums, Roots = plain.Roots, Apps = plain.Apps,
        };
        await using var host = await TestHost.StartAsync(TestPhones.Iphone(), plain);

        var noPaths = await host.CallAsync("appFiles.measure", new { deviceId = TestPhones.IphoneId, root = Vlc, paths = Array.Empty<string>() });
        Assert.Equal(-32602, (int)noPaths["error"]!["code"]!);
        var noRoot = await host.CallAsync("appFiles.measure", new { deviceId = TestPhones.IphoneId, root = "", paths = new[] { "/" } });
        Assert.Equal(-32602, (int)noRoot["error"]!["code"]!);
        var notSharing = await host.CallAsync("appFiles.list", new { deviceId = "plain" });
        Assert.Equal("not_supported", (string?)notSharing["error"]!["data"]!["code"]);
        var gone = await host.CallAsync("appFiles.list", new { deviceId = "nobody" });
        Assert.Equal("device_not_found", (string?)gone["error"]!["data"]!["code"]);
    }

    [Fact]
    public async Task Copy_a_folder_into_an_android_shared_folder_and_export_a_whole_app_folder_named_after_the_app()
    {
        await using var host = await TestHost.StartAsync(TestPhones.Iphone(), FakeSampleData.Android());
        var pc = host.PcFolder("Lecture notes");
        File.WriteAllBytes(Path.Combine(pc, "week 1.pdf"), new byte[12_345]);
        Directory.CreateDirectory(Path.Combine(pc, "Week 2", "Empty"));
        File.WriteAllBytes(Path.Combine(pc, "Week 2", "slides.pdf"), new byte[2_000]);

        var copyIn = await host.ResultAsync("files.copyToDevice", new { deviceId = FakeSampleData.AndroidId, root = "shared:Download", files = new[] { pc }, path = "/" });
        var inId = (string)copyIn!["jobId"]!;
        Assert.Equal("Copying Lecture notes into Download", host.Queue.Get(inId)!.Title);
        await Wait.ForStateAsync(host.Queue, inId, TransferState.Done);
        var listed = await host.ResultAsync("files.list", new { deviceId = FakeSampleData.AndroidId, root = "shared:Download", path = "/Lecture notes/Week 2" });
        Assert.Equal(["Empty", "slides.pdf"], listed!.AsArray().Select(e => (string?)e!["name"]).Order());
        var usage = await host.ResultAsync("appFiles.measure", new { deviceId = FakeSampleData.AndroidId, root = "shared:Download", paths = new[] { "/Lecture notes" } });
        Assert.Equal((2, 2, 14_345L), ((int)usage!["files"]!, (int)usage["folders"]!, (long)usage["bytes"]!));

        var destination = host.PcFolder("export");
        var copyOut = await host.ResultAsync("files.copyToPc", new { deviceId = TestPhones.IphoneId, root = Vlc, paths = new[] { "/" }, destination });
        var outId = (string)copyOut!["jobId"]!;
        Assert.Equal($"Copying VLC to {destination}", host.Queue.Get(outId)!.Title);
        await Wait.ForStateAsync(host.Queue, outId, TransferState.Done);
        Assert.Equal(300_000, new FileInfo(Path.Combine(destination, "VLC", "Movie.mkv")).Length);
        Assert.Equal(2_000, new FileInfo(Path.Combine(destination, "VLC", "Subtitles", "Movie.srt")).Length);
        Assert.True(Directory.Exists(Path.Combine(destination, "VLC", "Locked")));
    }
}
