using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Serialization;

namespace DeviceDeck.Bridge.JsonRpc;

/// <summary>A call that ran past <see cref="JsonRpcDispatcher.SlowCallThreshold"/>.</summary>
/// <param name="DeviceId">params.deviceId, when the call had one.</param>
/// <param name="Finished">False for the "still running" report at the threshold, true once the call has ended.</param>
/// <param name="Outcome">"ok" or the error's data.code / JSON-RPC code; null while still running.</param>
public sealed record RpcSlowCall(string Method, string? DeviceId, TimeSpan Elapsed, bool Finished, string? Outcome);

/// <summary>Per-call information the transport adds to a request.</summary>
/// <param name="Files">Paths of the files the page sent with postMessageWithAdditionalObjects (already copied into params.files).</param>
public sealed record RpcCallContext(IReadOnlyList<string> Files);

/// <summary>
/// JSON-RPC 2.0 over strings: a method registry, typed params, error mapping from <see cref="DeviceException"/>
/// (docs/bridge-contract.md). Knows nothing about WebView2.
/// </summary>
public sealed class JsonRpcDispatcher
{
    private delegate Task<object?> Handler(JsonElement parameters, RpcCallContext context, CancellationToken ct);

    /// <summary>For params: also rejects a missing required member or a null where the C# type says non-null.</summary>
    public static JsonSerializerOptions ParamsOptions { get; } = CreateParamsOptions();

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly Dictionary<string, Handler> _methods = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Methods => _methods.Keys;

    /// <summary>Raised when a call fails with something other than a device or params error (for logging).</summary>
    public event EventHandler<(string Method, Exception Error)>? UnexpectedError;

    /// <summary>
    /// Raised for a call that runs longer than <see cref="SlowCallThreshold"/>: once when it crosses the threshold
    /// (so a call that never returns still shows up) and once when it ends, with its duration.
    /// </summary>
    public event EventHandler<RpcSlowCall>? SlowCall;

    /// <summary>When a call counts as slow. Zero or less turns the reports off.</summary>
    public TimeSpan SlowCallThreshold { get; set; } = TimeSpan.FromSeconds(5);

    public JsonRpcDispatcher Register<TParams, TResult>(string method, Func<TParams, RpcCallContext, CancellationToken, Task<TResult>> handler)
        where TParams : class
    {
        _methods.Add(method, async (parameters, context, ct) =>
        {
            TParams? typed;
            try
            {
                typed = parameters.Deserialize<TParams>(ParamsOptions);
            }
            catch (JsonException ex)
            {
                throw RpcException.InvalidParams(ex.Message, ex);
            }

            if (typed is null)
            {
                throw RpcException.InvalidParams("params must be an object.");
            }

            return await handler(typed, context, ct).ConfigureAwait(false);
        });
        return this;
    }

    public JsonRpcDispatcher Register<TParams, TResult>(string method, Func<TParams, CancellationToken, Task<TResult>> handler)
        where TParams : class =>
        Register<TParams, TResult>(method, (p, _, ct) => handler(p, ct));

    /// <summary>A method without params (any params sent are ignored).</summary>
    public JsonRpcDispatcher Register<TResult>(string method, Func<CancellationToken, Task<TResult>> handler)
    {
        _methods.Add(method, async (_, _, ct) => await handler(ct).ConfigureAwait(false));
        return this;
    }

    /// <summary>A method that takes params as raw JSON (settings.set takes a partial object).</summary>
    public JsonRpcDispatcher RegisterRaw<TResult>(string method, Func<JsonElement, CancellationToken, Task<TResult>> handler)
    {
        _methods.Add(method, async (parameters, _, ct) => await handler(parameters, ct).ConfigureAwait(false));
        return this;
    }

    /// <summary>Handles one message (a request, a notification or a batch). Returns the response text, or null when nothing is owed.</summary>
    public async Task<string?> HandleAsync(string json, IReadOnlyList<string> files, CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Serialize(Failure(null, new RpcError(RpcErrorCodes.ParseError, "Parse error")));
        }

        if (root is JsonArray batch)
        {
            if (batch.Count == 0)
            {
                return Serialize(Failure(null, new RpcError(RpcErrorCodes.InvalidRequest, "Invalid Request: empty batch")));
            }

            var replies = await Task.WhenAll(batch.Select(node => HandleOneAsync(node, files, ct))).ConfigureAwait(false);
            var owed = replies.Where(r => r is not null).ToArray();
            return owed.Length == 0 ? null : Serialize(owed);
        }

        var reply = await HandleOneAsync(root, files, ct).ConfigureAwait(false);
        return reply is null ? null : Serialize(reply);
    }

    /// <summary>The text of an event (a notification) for <see cref="IBridgeTransport.Post"/>.</summary>
    public static string Notification(string method, object? parameters) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters is null ? null : JsonSerializer.SerializeToNode(parameters, parameters.GetType(), DeviceDeckJson.Options),
        }, DeviceDeckJson.Options);

    private async Task<JsonObject?> HandleOneAsync(JsonNode? node, IReadOnlyList<string> files, CancellationToken ct)
    {
        if (node is not JsonObject request)
        {
            return Failure(null, new RpcError(RpcErrorCodes.InvalidRequest, "Invalid Request"));
        }

        var hasId = request.TryGetPropertyValue("id", out var idNode);
        var id = idNode?.DeepClone();
        if (hasId && idNode is not null && idNode.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
        {
            return Failure(null, new RpcError(RpcErrorCodes.InvalidRequest, "Invalid Request: id must be a string or a number"));
        }

        if (request["jsonrpc"]?.GetValueKind() != JsonValueKind.String || request["jsonrpc"]!.GetValue<string>() != "2.0")
        {
            return Failure(id, new RpcError(RpcErrorCodes.InvalidRequest, "Invalid Request: jsonrpc must be \"2.0\""));
        }

        if (request["method"]?.GetValueKind() != JsonValueKind.String)
        {
            return Failure(id, new RpcError(RpcErrorCodes.InvalidRequest, "Invalid Request: method must be a string"));
        }

        var method = request["method"]!.GetValue<string>();
        var paramsNode = request["params"];
        if (paramsNode is not null and not JsonObject and not JsonArray)
        {
            return hasId ? Failure(id, new RpcError(RpcErrorCodes.InvalidRequest, "Invalid Request: params must be an object")) : null;
        }

        if (!_methods.TryGetValue(method, out var handler))
        {
            return hasId ? Failure(id, new RpcError(RpcErrorCodes.MethodNotFound, $"Method not found: {method}")) : null;
        }

        if (paramsNode is JsonArray)
        {
            // Every method in the contract takes named params.
            return hasId ? Failure(id, RpcException.InvalidParams("params must be an object, not an array.").Error) : null;
        }

        // Dropped files: the page sends params.files = [] and the host fills it from the additional objects.
        var parameters = paramsNode as JsonObject ?? [];
        if (files.Count > 0)
        {
            parameters = (JsonObject)parameters.DeepClone();
            parameters["files"] = new JsonArray(files.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray());
        }

        object? result;
        var deviceId = parameters["deviceId"] is JsonValue device && device.TryGetValue<string>(out var text) ? text : null;
        var watch = new CallWatch(method, deviceId, SlowCallThreshold);
        using var watchdog = watch.Threshold > TimeSpan.Zero && SlowCall is not null
            ? new Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref watch.Ended, 0, 0) == 0)
                {
                    SlowCall?.Invoke(this, new RpcSlowCall(method, deviceId, Stopwatch.GetElapsedTime(watch.Started), Finished: false, Outcome: null));
                }
            }, null, watch.Threshold, Timeout.InfiniteTimeSpan)
            : null;
        try
        {
            using var doc = JsonDocument.Parse(parameters.ToJsonString());
            result = await handler(doc.RootElement.Clone(), new RpcCallContext(files), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = MapError(ex);
            if (error.Data?.Code == RpcErrorCodes.Internal)
            {
                UnexpectedError?.Invoke(this, (method, ex));
            }

            ReportIfSlow(watch, error.Data?.Code ?? error.Code.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return hasId ? Failure(id, error) : null;
        }

        ReportIfSlow(watch, "ok");

        if (!hasId)
        {
            return null;
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result is null ? null : JsonSerializer.SerializeToNode(result, result.GetType(), DeviceDeckJson.Options),
        };
    }

    private sealed class CallWatch(string method, string? deviceId, TimeSpan threshold)
    {
        public readonly string Method = method;
        public readonly string? DeviceId = deviceId;
        public readonly TimeSpan Threshold = threshold;
        public readonly long Started = Stopwatch.GetTimestamp();
        public int Ended;
    }

    private void ReportIfSlow(CallWatch watch, string outcome)
    {
        Interlocked.Exchange(ref watch.Ended, 1);
        var elapsed = Stopwatch.GetElapsedTime(watch.Started);
        if (watch.Threshold > TimeSpan.Zero && elapsed >= watch.Threshold)
        {
            SlowCall?.Invoke(this, new RpcSlowCall(watch.Method, watch.DeviceId, elapsed, Finished: true, outcome));
        }
    }

    /// <summary>Exception → JSON-RPC error, per the contract's Errors section.</summary>
    public static RpcError MapError(Exception ex) => ex switch
    {
        RpcException rpc => rpc.Error,
        DeviceException device => new RpcError(
            RpcErrorCodes.ServerError,
            string.IsNullOrWhiteSpace(device.Message) ? DefaultMessage(device.Code) : device.Message,
            new RpcErrorData(
                RpcErrorCodes.KnownDataCodes.Contains(device.Code) ? device.Code : RpcErrorCodes.Internal,
                Detail(device))),
        ArgumentException argument => new RpcError(RpcErrorCodes.InvalidParams, $"Invalid params: {argument.Message}"),
        InvalidOperationException invalid => new RpcError(RpcErrorCodes.ServerError, invalid.Message, new RpcErrorData(RpcErrorCodes.Internal, invalid.Message)),
        OperationCanceledException => new RpcError(RpcErrorCodes.ServerError, "The request was cancelled.", new RpcErrorData(RpcErrorCodes.Internal, "OperationCanceledException")),
        _ => new RpcError(RpcErrorCodes.ServerError, "Something went wrong in DeviceDeck.", new RpcErrorData(RpcErrorCodes.Internal, $"{ex.GetType().Name}: {ex.Message}")),
    };

    private static string Detail(DeviceException ex)
    {
        var detail = ex.InnerException is { } inner ? $"{inner.GetType().Name}: {inner.Message}" : ex.Code;
        return RpcErrorCodes.KnownDataCodes.Contains(ex.Code) ? detail : $"{ex.Code}: {detail}";
    }

    private static string DefaultMessage(string code) => code switch
    {
        DeviceErrorCodes.NotFound => "That phone isn't connected.",
        DeviceErrorCodes.Disconnected => "The phone was disconnected.",
        DeviceErrorCodes.Locked => "Unlock the phone to continue.",
        DeviceErrorCodes.TrustRequired => "Unlock the iPhone and tap Trust.",
        DeviceErrorCodes.Unauthorized => "Tap Allow on the phone to allow USB debugging.",
        DeviceErrorCodes.NotSupported => "This phone can't do that.",
        DeviceErrorCodes.PathNotFound => "That file or folder isn't there.",
        DeviceErrorCodes.PermissionDenied => "The phone doesn't allow that.",
        DeviceErrorCodes.AlreadyExists => "Something with that name is already there.",
        DeviceErrorCodes.InstallFailed => "The app couldn't be installed.",
        DeviceErrorCodes.DriverMissing => "A driver this phone needs isn't installed.",
        _ => "The phone answered in a way DeviceDeck didn't expect.",
    };

    private static JsonObject Failure(JsonNode? id, RpcError error) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = JsonSerializer.SerializeToNode(error, DeviceDeckJson.Options),
    };

    private static string Serialize(object value) => JsonSerializer.Serialize(value, DeviceDeckJson.Options);

    private static JsonSerializerOptions CreateParamsOptions()
    {
        var options = new JsonSerializerOptions(DeviceDeckJson.Options)
        {
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
