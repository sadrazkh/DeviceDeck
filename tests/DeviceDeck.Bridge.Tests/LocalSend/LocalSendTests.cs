using System.Net;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.LocalSend;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests.LocalSend;

public sealed class LocalSendTests
{
    private static CancellationToken None => CancellationToken.None;

    private static async Task<(FakeLocalSendReceiver Receiver, LocalSendTestHost Host)> StartAsync(bool https = true)
    {
        var receiver = new FakeLocalSendReceiver(https);
        var host = await LocalSendTestHost.StartAsync(new LocalSendTestPhone { Receiver = receiver });
        return (receiver, host);
    }

    private static List<string?> Phases(LocalSendTestHost host, string jobId) =>
        host.Transport.EventsNamed("localsend.updated").Where(e => (string?)e["params"]!["jobId"] == jobId).Select(e => (string?)e["params"]!["phase"]).ToList();

    [Fact]
    public async Task Over_the_cable_a_photo_and_a_Live_Photo_go_in_one_session_and_arrive_whole()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;

        var target = await host.FindUsbTargetAsync();
        Assert.Equal(receiver.Fingerprint, (string?)target["fingerprint"]);
        Assert.Equal("Clever Mango", (string?)target["alias"]);
        var jpg = host.PcFile("IMG_0001.JPG");
        var heic = host.PcFile("IMG_0002.HEIC");
        var mov = host.PcFile("IMG_0002.MOV", 300_000);

        var jobId = await host.SendAsync(target, jpg, heic, mov);
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);

        var job = host.Queue.Get(jobId)!;
        Assert.Equal(TransferKind.LocalSend, job.Kind);
        Assert.Equal("Sending 3 items to Test iPhone with LocalSend", job.Title);
        // One Accept for the whole batch: a single /prepare-upload with every file, HEIC and the Live Photo's video as they are.
        var prepare = Assert.Single(receiver.Prepares);
        Assert.Equal(["image/jpeg", "image/heic", "video/quicktime"], prepare.Files.OrderBy(f => f.Id).Select(f => f.FileType));
        Assert.All(prepare.Files, f => Assert.Equal(64, f.Sha256!.Length));
        Assert.Equal(LocalSendTestHost.Content("IMG_0001.JPG", 50_000), receiver.Received["IMG_0001.JPG"]);
        Assert.Equal(LocalSendTestHost.Content("IMG_0002.HEIC", 50_000), receiver.Received["IMG_0002.HEIC"]);
        Assert.Equal(LocalSendTestHost.Content("IMG_0002.MOV", 300_000), receiver.Received["IMG_0002.MOV"]);
        Assert.Null(receiver.ActiveSessionId);
        Assert.Empty(receiver.Cancels);

        var status = await host.StatusAsync(jobId);
        Assert.Equal("done", (string?)status["phase"]);
        Assert.Equal("Clever Mango", (string?)status["receiverAlias"]);
        Assert.All(status["files"]!.AsArray(), f => Assert.Equal("done", (string?)f!["state"]));
        await Wait.UntilAsync(() => Phases(host, jobId).Contains("done"), "the done event");
        Assert.Equal(["connecting", "waitingForAccept", "sending", "done"], Phases(host, jobId).Distinct());
    }

    [Fact]
    public async Task The_send_waits_for_the_Accept_tap_and_says_so()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.AcceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("a.jpg"));

        await Wait.UntilAsync(() => Phases(host, jobId).Contains("waitingForAccept"), "waiting for Accept");
        Assert.Equal("waitingForAccept", (string?)(await host.StatusAsync(jobId))["phase"]);
        Assert.Equal(TransferState.Running, host.Queue.Get(jobId)!.State);
        Assert.Empty(receiver.Received);

        receiver.AcceptGate.SetResult();
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.True(receiver.Received.ContainsKey("a.jpg"));
    }

    [Fact]
    public async Task A_PIN_is_asked_for_once_and_the_retry_with_it_sends_everything()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.Pin = "424242";
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("a.jpg"), host.PcFile("b.jpg"), host.PcFile("c.jpg"));

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Failed);
        Assert.Equal(LocalSendErrors.PinRequired, host.Queue.Get(jobId)!.Error!.Code);
        Assert.Single(receiver.Prepares);
        var status = await host.StatusAsync(jobId);
        Assert.Equal("failed", (string?)status["phase"]);
        Assert.Equal(LocalSendErrors.PinRequired, (string?)status["error"]!["code"]);
        Assert.All(status["files"]!.AsArray(), f => Assert.Equal(LocalSendErrors.PinRequired, (string?)f!["error"]!["code"]));

        await host.ResultAsync("localsend.retry", new { jobId, pin = "1111" });
        await Wait.UntilAsync(() => receiver.Prepares.Count == 2 && host.Queue.Get(jobId)!.State == TransferState.Failed, "the wrong PIN");
        Assert.Equal(LocalSendErrors.PinWrong, host.Queue.Get(jobId)!.Error!.Code);
        Assert.True((bool)(await host.StatusAsync(jobId))["pinGiven"]!);

        await host.ResultAsync("localsend.retry", new { jobId, pin = " 424242 " });
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Equal(3, receiver.Prepares.Count);
        Assert.Equal("424242", receiver.Prepares.Last().Pin);
        Assert.Equal(["a.jpg", "b.jpg", "c.jpg"], receiver.Received.Keys.Order());
    }

    [Theory]
    [InlineData(403, LocalSendErrors.Rejected)]
    [InlineData(409, LocalSendErrors.Busy)]
    [InlineData(429, LocalSendErrors.Busy)]
    [InlineData(500, LocalSendErrors.Failed)]
    public async Task Declined_busy_or_broken_ends_the_send_with_its_code_and_a_retry_asks_again(int status, string code)
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.PrepareStatus = status;
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("a.jpg"), host.PcFile("b.jpg"));

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Failed);
        Assert.Equal(code, host.Queue.Get(jobId)!.Error!.Code);
        Assert.Equal(2, host.Queue.Get(jobId)!.FailedItems);
        Assert.Single(receiver.Prepares);

        receiver.PrepareStatus = null;
        await host.ResultAsync("localsend.retry", new { jobId });
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Equal(2, receiver.Received.Count);
    }

    [Fact]
    public async Task Nothing_to_send_204_counts_as_skipped()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.PrepareStatus = 204;
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("a.jpg"), host.PcFile("b.jpg"));

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.All((await host.StatusAsync(jobId))["files"]!.AsArray(), f => Assert.Equal("skipped", (string?)f!["state"]));
        Assert.Empty(receiver.Received);
    }

    [Fact]
    public async Task Cancelling_in_the_middle_of_a_file_tells_the_phone()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.BytesPerSecond = 256 * 1024;
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("big.mov", 4_000_000), host.PcFile("b.jpg"));

        await Wait.UntilAsync(() => receiver.UploadingBytes > 0, "the upload to start");
        var session = receiver.ActiveSessionId;
        Assert.NotNull(session);
        await host.ResultAsync("transfers.cancel", new { jobId });

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Cancelled);
        await Wait.UntilAsync(() => receiver.Cancels.Contains(session), "/cancel on the phone");
        Assert.Null(receiver.ActiveSessionId);
        Assert.Equal("cancelled", (string?)(await host.StatusAsync(jobId))["phase"]);
    }

    [Fact]
    public async Task A_connection_dropped_mid_file_fails_that_file_only_and_the_retry_sends_just_it()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.DropOnce["b.jpg"] = true;
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("a.jpg"), host.PcFile("b.jpg", 200_000), host.PcFile("c.jpg"));

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Equal(1, host.Queue.Get(jobId)!.FailedItems);
        var files = (await host.StatusAsync(jobId))["files"]!.AsArray();
        Assert.Equal(["done", "failed", "done"], files.Select(f => (string?)f!["state"]));
        Assert.Equal(LocalSendErrors.ConnectionLost, (string?)files[1]!["error"]!["code"]);
        Assert.Equal(["a.jpg", "c.jpg"], receiver.Received.Keys.Order());

        await host.ResultAsync("localsend.retry", new { jobId });
        await Wait.UntilAsync(() => host.Queue.Get(jobId) is { State: TransferState.Done, FailedItems: 0 }, "the retry");
        Assert.Equal(["b.jpg"], receiver.Prepares.Last().Files.Select(f => f.FileName));
        Assert.Equal(LocalSendTestHost.Content("b.jpg", 200_000), receiver.Received["b.jpg"]);
    }

    [Fact]
    public async Task A_checksum_mismatch_fails_that_file_with_its_code()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.CorruptOnce["b.jpg"] = true;
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("a.jpg"), host.PcFile("b.jpg"));

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        var files = (await host.StatusAsync(jobId))["files"]!.AsArray();
        Assert.Equal(LocalSendErrors.Checksum, (string?)files[1]!["error"]!["code"]);
        Assert.Equal(["a.jpg"], receiver.Received.Keys);
    }

    [Fact]
    public async Task LocalSend_closed_on_the_phone_is_told_apart_from_LocalSend_in_the_background()
    {
        await using var receiver = new FakeLocalSendReceiver();
        var phone = new LocalSendTestPhone();
        await using var host = await LocalSendTestHost.StartAsync(phone);

        var closed = await host.ResultAsync("localsend.discover", new { deviceId = phone.Summary.Id });
        Assert.Equal("notListening", (string?)closed["usb"]);
        Assert.Null(closed["usbReceiver"]);

        receiver.Silent = true;
        phone.Receiver = receiver;
        var silent = await host.ResultAsync("localsend.discover", new { deviceId = phone.Summary.Id });
        Assert.Equal("notAnswering", (string?)silent["usb"]);

        phone.Receiver = null;
        var target = new JsonObject { ["route"] = "usb", ["port"] = 53317, ["protocol"] = "https", ["fingerprint"] = receiver.Fingerprint };
        var jobId = await host.SendAsync(target, host.PcFile("a.jpg"));
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Failed);
        Assert.Equal(LocalSendErrors.NotListening, host.Queue.Get(jobId)!.Error!.Code);
    }

    [Fact]
    public async Task A_different_certificate_than_the_one_found_fails_the_send()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        var target = await host.FindUsbTargetAsync();
        await using var impostor = new FakeLocalSendReceiver(certificate: FakeLocalSendReceiver.CreateCertificate("Someone else"));
        host.Phone.Receiver = impostor;

        var jobId = await host.SendAsync(target, host.PcFile("a.jpg"));
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Failed);
        Assert.Equal(LocalSendErrors.Fingerprint, host.Queue.Get(jobId)!.Error!.Code);
        Assert.Empty(impostor.Prepares);
    }

    [Fact]
    public async Task Unplugged_mid_send_it_waits_and_after_plugging_back_cancels_the_old_session_and_finishes()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.BytesPerSecond = 256 * 1024;
        var jobId = await host.SendAsync(await host.FindUsbTargetAsync(), host.PcFile("big.mov", 2_000_000), host.PcFile("b.jpg"));
        await Wait.UntilAsync(() => receiver.UploadingBytes > 0, "the upload to start");
        var first = receiver.ActiveSessionId;

        host.Provider.Unplug();
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.WaitingForDevice);
        receiver.BytesPerSecond = null;
        host.Provider.PlugBack();

        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.Contains(first, receiver.Cancels);
        Assert.Equal(2, receiver.Prepares.Count);
        Assert.Equal(["b.jpg", "big.mov"], receiver.Received.Keys.Order());
    }

    [Fact]
    public async Task Two_sends_to_one_phone_take_turns_instead_of_failing_as_busy()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        receiver.AcceptGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = await host.FindUsbTargetAsync();
        var one = await host.SendAsync(target, host.PcFile("a.jpg"));
        await Wait.UntilAsync(() => receiver.Prepares.Count == 1, "the first send to ask");
        var two = await host.SendAsync(target, host.PcFile("b.jpg"));
        await Wait.UntilAsync(() => host.Queue.Get(two)!.State == TransferState.Running, "the second send to start");
        await Task.Delay(300);
        Assert.Single(receiver.Prepares);

        receiver.AcceptGate.SetResult();
        await Wait.ForStateAsync(host.Queue, one, TransferState.Done);
        await Wait.ForStateAsync(host.Queue, two, TransferState.Done);
        Assert.Equal(["a.jpg", "b.jpg"], receiver.Received.Keys.Order());
    }

    [Fact]
    public async Task With_encryption_off_LocalSend_is_reached_over_plain_HTTP()
    {
        var (receiver, host) = await StartAsync(https: false);
        await using var _ = receiver;
        await using var __ = host;
        var target = await host.FindUsbTargetAsync();
        Assert.Equal("http", (string?)target["protocol"]);

        var jobId = await host.SendAsync(target, host.PcFile("a.jpg"));
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.True(receiver.Received.ContainsKey("a.jpg"));
    }

    [Fact]
    public async Task On_WiFi_the_sweep_finds_LocalSend_and_the_send_goes_to_its_address()
    {
        await using var receiver = new FakeLocalSendReceiver { Alias = "Test iPhone" };
        var phone = new LocalSendTestPhone();
        await using var host = await LocalSendTestHost.StartAsync(phone, new LocalSendFinderOptions
        {
            Port = receiver.Port,
            Multicast = false,
            SweepAddresses = () => [IPAddress.Loopback],
            SearchTime = TimeSpan.FromSeconds(5),
            ProbeTimeouts = LocalSendTestHost.FastTimeouts,
            SweepTimeouts = LocalSendTestHost.FastTimeouts,
        });

        var found = await DiscoverOnWifiAsync(host);
        Assert.Equal("notListening", (string?)found["usb"]);
        var seen = found["wifi"]![0]!;
        Assert.Equal("wifi", (string?)seen["route"]);
        Assert.Equal("127.0.0.1", (string?)seen["address"]);
        Assert.Equal(receiver.Port, (int)seen["port"]!);
        Assert.Equal(receiver.Fingerprint, (string?)seen["fingerprint"]);
        Assert.True((bool)seen["likely"]!);
        Assert.Contains(receiver.Registered, r => r.Alias == "DeviceDeck (TEST-PC)" && r.DeviceType == "desktop");

        var target = new JsonObject
        {
            ["route"] = "wifi",
            ["address"] = "127.0.0.1",
            ["port"] = receiver.Port,
            ["protocol"] = "https",
            ["fingerprint"] = receiver.Fingerprint,
            ["alias"] = "Test iPhone",
        };
        var connects = phone.Connects;
        var jobId = await host.SendAsync(target, host.PcFile("a.jpg"));
        await Wait.ForStateAsync(host.Queue, jobId, TransferState.Done);
        Assert.True(receiver.Received.ContainsKey("a.jpg"));
        Assert.Equal(connects, phone.Connects); // the send went over the network, not the cable
    }

    [Fact]
    public async Task A_WiFi_receiver_that_looks_like_another_phone_is_listed_but_not_picked()
    {
        await using var mine = new FakeLocalSendReceiver { Alias = "Kitchen tablet", DeviceModel = "Galaxy Tab", DeviceType = "mobile" };
        var phone = new LocalSendTestPhone();
        await using var host = await LocalSendTestHost.StartAsync(phone, new LocalSendFinderOptions
        {
            Port = mine.Port,
            Multicast = false,
            SweepAddresses = () => [IPAddress.Loopback],
            SearchTime = TimeSpan.FromSeconds(5),
            ProbeTimeouts = LocalSendTestHost.FastTimeouts,
            SweepTimeouts = LocalSendTestHost.FastTimeouts,
        });

        var found = await DiscoverOnWifiAsync(host);
        Assert.False((bool)found["wifi"]![0]!["likely"]!);
    }

    [Fact]
    public async Task Sends_left_unfinished_when_DeviceDeck_closed_are_cancelled_on_the_next_start()
    {
        await using var receiver = new FakeLocalSendReceiver { AcceptGate = new TaskCompletionSource() };
        var phone = new LocalSendTestPhone { Receiver = receiver };
        var root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", "localsend-restart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var provider = new LocalSendTestProvider(phone);
            await using var hub = new DeviceHub([provider]) { DriverPollInterval = TimeSpan.Zero };
            await hub.StartAsync(None);
            var file = Path.Combine(root, "a.jpg");
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var target = new LocalSendTarget(LocalSendRoute.Usb, 53317, "https", receiver.Fingerprint, Alias: "Clever Mango");

            string jobId;
            using (var store = new SqliteTransferStore(Path.Combine(root, "transfers.db")))
            {
                await using var service = new LocalSendService(hub, store.LoadItems, timeouts: LocalSendTestHost.FastTimeouts);
                await using var queue = new TransferQueue(hub, store, [service.Handler], new CountingKeepAwake(), TestHost.FastQueue());
                queue.Start();
                service.Attach(queue);
                jobId = (await service.SendAsync(phone.Summary.Id, target, [file], None)).Id;
                await Wait.UntilAsync(() => receiver.Prepares.Count == 1, "the Accept question");
            }

            using (var store = new SqliteTransferStore(Path.Combine(root, "transfers.db")))
            {
                await using var service = new LocalSendService(hub, store.LoadItems, timeouts: LocalSendTestHost.FastTimeouts);
                Assert.Equal(1, LocalSendService.CancelLeftovers(store));
                await using var queue = new TransferQueue(hub, store, [service.Handler], new CountingKeepAwake(), TestHost.FastQueue());
                queue.Start();
                service.Attach(queue);
                Assert.Equal(TransferState.Cancelled, queue.Get(jobId)!.State);
                await Task.Delay(300);
                Assert.Single(receiver.Prepares);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public async Task The_client_maps_an_unknown_token_to_session_lost_and_never_sends_to_a_wrong_certificate()
    {
        await using var receiver = new FakeLocalSendReceiver();
        var identity = LocalSendIdentity.Create("DeviceDeck (TEST-PC)");
        var me = identity.Info();
        var file = new LocalSendFile("0", "a.jpg", 3, "image/jpeg", null, null, null);

        // LocalSend asks for the sender's certificate and drops a connection that has none (seen on iOS, LocalSend 2.2).
        using var anonymous = new LocalSendClient(new LocalSendEndpoint("127.0.0.1", receiver.Port, true), receiver.Fingerprint, LocalSendTestHost.FastTimeouts);
        Assert.Equal(LocalSendErrors.NotListening, (await Assert.ThrowsAsync<DeviceException>(() => anonymous.GetInfoAsync(None))).Code);

        using var client = new LocalSendClient(new LocalSendEndpoint("127.0.0.1", receiver.Port, true), receiver.Fingerprint, LocalSendTestHost.FastTimeouts, identity.Certificate);
        var info = await client.GetInfoAsync(None);
        Assert.Equal(receiver.Fingerprint, info.Fingerprint);
        Assert.True(LocalSendProtocol.SameFingerprint(receiver.Fingerprint, client.PeerFingerprint));
        Assert.Contains(identity.Fingerprint, receiver.ClientFingerprints);
        Assert.Equal(64, identity.Fingerprint.Length);
        Assert.Equal(identity.Fingerprint.ToUpperInvariant(), identity.Fingerprint);
        var session = (await client.PrepareUploadAsync(me, [file], null, None))!;
        var lost = await Assert.ThrowsAsync<DeviceException>(() => client.UploadAsync(session.SessionId, "0", "not-the-token", new MemoryStream([1, 2, 3]), 3, null, None));
        Assert.Equal(LocalSendErrors.SessionLost, lost.Code);
        Assert.True(await client.CancelAsync(session.SessionId, None));

        using var pinnedWrong = new LocalSendClient(new LocalSendEndpoint("127.0.0.1", receiver.Port, true), new string('A', 64), LocalSendTestHost.FastTimeouts, identity.Certificate);
        var wrong = await Assert.ThrowsAsync<DeviceException>(() => pinnedWrong.PrepareUploadAsync(me, [file], null, None));
        Assert.Equal(LocalSendErrors.Fingerprint, wrong.Code);
        Assert.Single(receiver.Prepares);
    }

    [Fact]
    public async Task Retry_and_status_are_for_LocalSend_sends_only()
    {
        var (receiver, host) = await StartAsync();
        await using var _ = receiver;
        await using var __ = host;
        Assert.Equal("invalid_params", await ErrorKindAsync(host, "localsend.job", new { jobId = "nope" }));
        Assert.Equal("invalid_params", await ErrorKindAsync(host, "localsend.retry", new { jobId = "nope" }));
    }

    private static async Task<JsonNode> DiscoverOnWifiAsync(LocalSendTestHost host)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var found = await host.ResultAsync("localsend.discover", new { deviceId = host.Phone.Summary.Id, wifi = true });
            if (found["wifi"]!.AsArray().Count > 0)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for LocalSend on the network.");
            await Task.Delay(50);
        }
    }

    private static async Task<string> ErrorKindAsync(LocalSendTestHost host, string method, object parameters)
    {
        var response = await host.CallAsync(method, parameters);
        Assert.True(response.ContainsKey("error"), response.ToJsonString());
        return (int)response["error"]!["code"]! == -32602 ? "invalid_params" : (string)response["error"]!["data"]!["code"]!;
    }
}
