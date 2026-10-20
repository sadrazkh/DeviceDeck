using System.Text.Json.Nodes;
using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Bridge.Tests;

public sealed class DispatcherTests
{
    private sealed record EchoParams(string Name, IReadOnlyList<string>? Files = null, int Count = 1);

    private static JsonRpcDispatcher Echo()
    {
        var rpc = new JsonRpcDispatcher();
        rpc.Register<EchoParams, object>("echo", (p, ctx, _) => Task.FromResult<object>(new { p.Name, p.Count, files = p.Files, fromContext = ctx.Files.Count }));
        rpc.Register<object>("boom.device", _ => throw new DeviceException(DeviceErrorCodes.Locked, "Unlock the iPhone to continue.", new IOException("AFC_E_LOCKED")));
        rpc.Register<object>("boom.custom", _ => throw new DeviceException("weird_code", "Odd."));
        rpc.Register<object>("boom.bug", _ => throw new NullReferenceException("oops"));
        rpc.Register<object>("boom.argument", _ => throw new ArgumentException("limit must be positive."));
        return rpc;
    }

    private static async Task<JsonObject> Call(JsonRpcDispatcher rpc, string json, params string[] files)
    {
        var reply = await rpc.HandleAsync(json, files, CancellationToken.None);
        Assert.NotNull(reply);
        return JsonNode.Parse(reply)!.AsObject();
    }

    [Fact]
    public async Task Answers_with_the_same_numeric_or_string_id()
    {
        var rpc = Echo();
        var numeric = await Call(rpc, """{"jsonrpc":"2.0","id":7,"method":"echo","params":{"name":"a"}}""");
        Assert.Equal("2.0", (string?)numeric["jsonrpc"]);
        Assert.Equal(7, (int)numeric["id"]!);
        Assert.Equal("a", (string?)numeric["result"]!["name"]);
        Assert.False(numeric.ContainsKey("error"));

        var text = await Call(rpc, """{"jsonrpc":"2.0","id":"x-1","method":"echo","params":{"name":"b","count":3}}""");
        Assert.Equal("x-1", (string?)text["id"]);
        Assert.Equal(3, (int)text["result"]!["count"]!);
    }

    [Fact]
    public async Task Unknown_method_is_32601()
    {
        var reply = await Call(Echo(), """{"jsonrpc":"2.0","id":1,"method":"nope"}""");
        Assert.Equal(RpcErrorCodes.MethodNotFound, (int)reply["error"]!["code"]!);
        Assert.Equal(1, (int)reply["id"]!);
    }

    [Fact]
    public async Task Bad_json_is_32700_with_null_id()
    {
        var reply = await Call(Echo(), "{not json");
        Assert.Equal(RpcErrorCodes.ParseError, (int)reply["error"]!["code"]!);
        Assert.True(reply.ContainsKey("id"));
        Assert.Null(reply["id"]);
    }

    [Theory]
    [InlineData("""{"id":1,"method":"echo"}""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"echo"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":5}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"echo"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"echo","params":"x"}""")]
    [InlineData("""42""")]
    [InlineData("""[]""")]
    public async Task Malformed_requests_are_32600(string json)
    {
        var reply = await Call(Echo(), json);
        Assert.Equal(RpcErrorCodes.InvalidRequest, (int)reply["error"]!["code"]!);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"echo","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"echo","params":{"name":null}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"echo","params":{"name":"a","count":"three"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"echo","params":["a"]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"boom.argument"}""")]
    public async Task Missing_or_wrong_params_are_32602(string json)
    {
        var reply = await Call(Echo(), json);
        Assert.Equal(RpcErrorCodes.InvalidParams, (int)reply["error"]!["code"]!);
    }

    [Fact]
    public async Task Notifications_get_no_reply_even_when_they_fail()
    {
        var rpc = Echo();
        Assert.Null(await rpc.HandleAsync("""{"jsonrpc":"2.0","method":"echo","params":{"name":"a"}}""", [], CancellationToken.None));
        Assert.Null(await rpc.HandleAsync("""{"jsonrpc":"2.0","method":"boom.bug"}""", [], CancellationToken.None));
        Assert.Null(await rpc.HandleAsync("""{"jsonrpc":"2.0","method":"nope"}""", [], CancellationToken.None));
    }

    [Fact]
    public async Task Batches_answer_each_request_and_skip_notifications()
    {
        var reply = await Echo().HandleAsync("""
            [
              {"jsonrpc":"2.0","id":1,"method":"echo","params":{"name":"a"}},
              {"jsonrpc":"2.0","method":"echo","params":{"name":"b"}},
              {"jsonrpc":"2.0","id":2,"method":"nope"}
            ]
            """, [], CancellationToken.None);
        var array = JsonNode.Parse(reply!)!.AsArray();
        Assert.Equal(2, array.Count);
        Assert.Contains(array, r => (int)r!["id"]! == 1 && r["result"] is not null);
        Assert.Contains(array, r => (int)r!["id"]! == 2 && (int)r["error"]!["code"]! == RpcErrorCodes.MethodNotFound);
    }

    [Fact]
    public async Task Device_errors_map_to_32000_with_the_provider_code()
    {
        var reply = await Call(Echo(), """{"jsonrpc":"2.0","id":3,"method":"boom.device"}""");
        var error = reply["error"]!;
        Assert.Equal(-32000, (int)error["code"]!);
        Assert.Equal("Unlock the iPhone to continue.", (string?)error["message"]);
        Assert.Equal("device_locked", (string?)error["data"]!["code"]);
        Assert.Contains("AFC_E_LOCKED", (string?)error["data"]!["detail"]);
    }

    [Fact]
    public async Task Unknown_provider_codes_and_bugs_become_internal()
    {
        var custom = await Call(Echo(), """{"jsonrpc":"2.0","id":1,"method":"boom.custom"}""");
        Assert.Equal("internal", (string?)custom["error"]!["data"]!["code"]);
        Assert.Contains("weird_code", (string?)custom["error"]!["data"]!["detail"]);

        var rpc = Echo();
        var reported = new List<string>();
        rpc.UnexpectedError += (_, e) => reported.Add(e.Method);
        var bug = await Call(rpc, """{"jsonrpc":"2.0","id":2,"method":"boom.bug"}""");
        Assert.Equal(-32000, (int)bug["error"]!["code"]!);
        Assert.Equal("internal", (string?)bug["error"]!["data"]!["code"]);
        Assert.Equal(["boom.bug"], reported);
    }

    [Theory]
    [InlineData(DeviceErrorCodes.NotFound)]
    [InlineData(DeviceErrorCodes.Disconnected)]
    [InlineData(DeviceErrorCodes.Locked)]
    [InlineData(DeviceErrorCodes.TrustRequired)]
    [InlineData(DeviceErrorCodes.Unauthorized)]
    [InlineData(DeviceErrorCodes.NotSupported)]
    [InlineData(DeviceErrorCodes.PathNotFound)]
    [InlineData(DeviceErrorCodes.PermissionDenied)]
    [InlineData(DeviceErrorCodes.AlreadyExists)]
    [InlineData(DeviceErrorCodes.InstallFailed)]
    [InlineData(DeviceErrorCodes.DriverMissing)]
    [InlineData(DeviceErrorCodes.Protocol)]
    public void Every_contract_code_passes_through_with_a_plain_message(string code)
    {
        var error = JsonRpcDispatcher.MapError(new DeviceException(code, ""));
        Assert.Equal(-32000, error.Code);
        Assert.Equal(code, error.Data!.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.EndsWith(".", error.Message);
    }

    [Fact]
    public async Task Dropped_files_replace_params_files()
    {
        var reply = await Call(Echo(), """{"jsonrpc":"2.0","id":9,"method":"echo","params":{"name":"drop","files":[]}}""", @"C:\Films\Interstellar.mkv", @"C:\Films\Arrival.mp4");
        var result = reply["result"]!;
        Assert.Equal([@"C:\Films\Interstellar.mkv", @"C:\Films\Arrival.mp4"], result["files"]!.AsArray().Select(f => (string)f!));
        Assert.Equal(2, (int)result["fromContext"]!);
    }

    [Fact]
    public void Events_are_notifications_without_an_id()
    {
        var json = JsonRpcDispatcher.Notification("device.removed", new Methods.DeviceRemovedEvent("abc"));
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.False(node.ContainsKey("id"));
        Assert.Equal("2.0", (string?)node["jsonrpc"]);
        Assert.Equal("device.removed", (string?)node["method"]);
        Assert.Equal("abc", (string?)node["params"]!["deviceId"]);
    }

    private sealed record SlowParams(string DeviceId, int Ms);

    private static (JsonRpcDispatcher Rpc, List<RpcSlowCall> Reports) Slow(TimeSpan threshold)
    {
        var rpc = new JsonRpcDispatcher { SlowCallThreshold = threshold };
        rpc.Register<SlowParams, object>("device.storage", async (p, ct) =>
        {
            await Task.Delay(p.Ms, ct);
            return new { ok = true };
        });
        rpc.Register<SlowParams, object>("device.battery", async (p, ct) =>
        {
            await Task.Delay(p.Ms, ct);
            throw new DeviceException(DeviceErrorCodes.Locked, "Unlock the iPhone.");
        });
        var reports = new List<RpcSlowCall>();
        rpc.SlowCall += (_, r) =>
        {
            lock (reports)
            {
                reports.Add(r);
            }
        };
        return (rpc, reports);
    }

    [Fact]
    public async Task Slow_calls_are_reported_while_running_and_when_they_end()
    {
        var (rpc, reports) = Slow(TimeSpan.FromMilliseconds(150));
        // The call runs well past the 150 ms threshold, so a late timer on a loaded CI runner still lands while it runs.
        var reply = await Call(rpc, """{"jsonrpc":"2.0","id":1,"method":"device.storage","params":{"deviceId":"iphone-1","ms":1200}}""");
        Assert.True((bool)reply["result"]!["ok"]!);

        Assert.Equal(2, reports.Count);
        var running = reports[0];
        Assert.Equal(("device.storage", "iphone-1", false, (string?)null), (running.Method, running.DeviceId, running.Finished, running.Outcome));
        var ended = reports[1];
        Assert.Equal(("device.storage", "iphone-1", true, "ok"), (ended.Method, ended.DeviceId, ended.Finished, ended.Outcome));
        Assert.True(running.Elapsed.TotalMilliseconds >= 140, $"running reported after {running.Elapsed.TotalMilliseconds} ms, before the threshold");
        Assert.True(running.Elapsed < ended.Elapsed, "the running report came after the call had ended");
        Assert.True(ended.Elapsed.TotalMilliseconds >= 1190, $"reported {ended.Elapsed.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task A_slow_failure_reports_its_error_code_and_fast_calls_report_nothing()
    {
        // A wide gap between "fast" (0 ms) and the threshold (1 s): a CI runner can stall a 10 ms call past 150 ms.
        var (rpc, reports) = Slow(TimeSpan.FromSeconds(1));
        await Call(rpc, """{"jsonrpc":"2.0","id":1,"method":"device.storage","params":{"deviceId":"a","ms":0}}""");
        Assert.Empty(reports);

        var failed = await Call(rpc, """{"jsonrpc":"2.0","id":2,"method":"device.battery","params":{"deviceId":"a","ms":1500}}""");
        Assert.Equal("device_locked", (string?)failed["error"]!["data"]!["code"]);
        Assert.Equal("device_locked", reports.Single(r => r.Finished).Outcome);

        var (off, none) = Slow(TimeSpan.Zero);
        await Call(off, """{"jsonrpc":"2.0","id":3,"method":"device.storage","params":{"deviceId":"a","ms":200}}""");
        Assert.Empty(none);
    }

    [Fact]
    public void Registering_a_method_twice_fails()
    {
        var rpc = Echo();
        Assert.Throws<ArgumentException>(() => rpc.Register<object>("echo", _ => Task.FromResult<object>(1)));
    }
}
