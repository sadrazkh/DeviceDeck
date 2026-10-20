using System.Text.Json.Serialization;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;

namespace DeviceDeck.Bridge.JsonRpc;

public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;

    /// <summary>Every device and host failure; the reason is in data.code.</summary>
    public const int ServerError = -32000;

    /// <summary>data.code for failures that are not a <see cref="DeviceException"/>.</summary>
    public const string Internal = "internal";

    /// <summary>The data.code values the contract allows (docs/bridge-contract.md, "Errors").</summary>
    public static readonly IReadOnlySet<string> KnownDataCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        DeviceErrorCodes.NotFound, DeviceErrorCodes.Disconnected, DeviceErrorCodes.Locked, DeviceErrorCodes.TrustRequired,
        DeviceErrorCodes.Unauthorized, DeviceErrorCodes.NotSupported, DeviceErrorCodes.PathNotFound, DeviceErrorCodes.PermissionDenied,
        DeviceErrorCodes.AlreadyExists, DeviceErrorCodes.InstallFailed, DeviceErrorCodes.DriverMissing, DeviceErrorCodes.Protocol,
        // Backup (round 2)
        BackupErrors.WrongPassword, BackupErrors.PasswordRequired, BackupErrors.Busy, BackupErrors.Missing, BackupErrors.Corrupt,
        BackupErrors.NoSpace, BackupErrors.Refused,
        Internal,
    };
}

/// <summary>The data member of a -32000 error.</summary>
public sealed record RpcErrorData(string Code, string Detail);

public sealed record RpcError(
    int Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcErrorData? Data = null);

/// <summary>Thrown inside the dispatcher to answer with a specific JSON-RPC error.</summary>
public sealed class RpcException(RpcError error, Exception? inner = null) : Exception(error.Message, inner)
{
    public RpcError Error { get; } = error;

    public static RpcException InvalidParams(string reason, Exception? inner = null) =>
        new(new RpcError(RpcErrorCodes.InvalidParams, $"Invalid params: {reason}"), inner);
}
