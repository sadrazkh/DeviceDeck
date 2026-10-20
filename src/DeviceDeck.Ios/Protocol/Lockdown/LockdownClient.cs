using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Lockdown;

/// <summary>
/// lockdownd on port 62078: length-prefixed plists with {Label, Request, …}. Values and services need a
/// session (StartSession with our HostID), which switches the socket to TLS.
/// </summary>
public sealed class LockdownClient(PlistConnection connection) : IAsyncDisposable
{
    public const int Port = 62078;
    public const string Label = "DeviceDeck";

    public PlistConnection Connection { get; } = connection;

    public string? SessionId { get; private set; }

    /// <summary>"com.apple.mobile.lockdown" on a normal phone.</summary>
    public async Task<string?> QueryTypeAsync(CancellationToken ct)
    {
        var reply = await RequestAsync("QueryType", null, ct).ConfigureAwait(false);
        return reply.GetString("Type");
    }

    /// <summary>One value, or null when lockdown says MissingValue.</summary>
    public async Task<object?> GetValueAsync(string? domain, string? key, CancellationToken ct)
    {
        var args = new PlistDict();
        if (domain is not null) args["Domain"] = domain;
        if (key is not null) args["Key"] = key;
        try
        {
            var reply = await RequestAsync("GetValue", args, ct).ConfigureAwait(false);
            return reply.TryGetValue("Value", out var v) ? v : null;
        }
        catch (DeviceException ex) when (ex.LockdownError() is "MissingValue")
        {
            return null;
        }
    }

    /// <summary>Every value in a domain (null = the top domain). Before pairing, only a handful are given.</summary>
    public async Task<PlistDict> GetAllValuesAsync(string? domain, CancellationToken ct) =>
        await GetValueAsync(domain, null, ct).ConfigureAwait(false) as PlistDict ?? new PlistDict();

    /// <summary>Starts a session as the host in <paramref name="record"/>; switches to TLS when the phone asks.</summary>
    public async Task StartSessionAsync(PairRecord record, CancellationToken ct)
    {
        var reply = await RequestAsync("StartSession", new PlistDict
        {
            ["HostID"] = record.HostId,
            ["SystemBUID"] = record.SystemBuid,
        }, ct).ConfigureAwait(false);
        SessionId = reply.GetString("SessionID");
        if (reply.GetBool("EnableSessionSSL") == true)
            await Connection.UpgradeToTlsAsync(record.ClientCertificate, ct).ConfigureAwait(false);
    }

    public async Task StopSessionAsync(CancellationToken ct)
    {
        if (SessionId is null)
            return;
        await RequestAsync("StopSession", new PlistDict { ["SessionID"] = SessionId }, ct).ConfigureAwait(false);
        SessionId = null;
    }

    /// <summary>Asks lockdown to start a service; connect to the returned port through usbmux.</summary>
    public async Task<LockdownServiceInfo> StartServiceAsync(string service, CancellationToken ct)
    {
        var reply = await RequestAsync("StartService", new PlistDict { ["Service"] = service }, ct).ConfigureAwait(false);
        var port = reply.GetInt("Port") ?? throw new FormatException($"StartService {service} returned no port.");
        return new LockdownServiceInfo(service, port, reply.GetBool("EnableServiceSSL") == true);
    }

    /// <summary>Sends Pair. The phone shows "Trust This Computer?"; until it is answered the reply is PairingDialogResponsePending.</summary>
    public Task<PlistDict> PairAsync(PlistDict pairRecord, CancellationToken ct) =>
        RequestAsync("Pair", new PlistDict
        {
            ["PairRecord"] = pairRecord,
            ["ProtocolVersion"] = "2",
            ["PairingOptions"] = new PlistDict { ["ExtendedPairingErrors"] = true },
        }, ct);

    public async Task<PlistDict> RequestAsync(string request, PlistDict? args, CancellationToken ct)
    {
        var message = new PlistDict { ["Label"] = Label, ["Request"] = request };
        if (args is not null)
        {
            foreach (var (k, v) in args)
                message[k] = v;
        }
        var reply = await Connection.RequestAsync(message, ct).ConfigureAwait(false);
        if (reply.GetString("Error") is { } error)
            throw ErrorFor(request, error);
        return reply;
    }

    /// <summary>Maps lockdown's error strings to DeviceErrorCodes; the raw string rides in Exception.Data.</summary>
    public static DeviceException ErrorFor(string request, string error)
    {
        var (code, text) = error switch
        {
            "PasswordProtected" => (DeviceErrorCodes.Locked, "The iPhone is locked. Unlock it and try again."),
            "InvalidHostID" or "InvalidPairRecord" or "NotPaired" or "PairingDialogResponsePending"
                or "UserDeniedPairing" or "PairingProhibitedOverThisConnection" or "InvalidHostCertificate"
                => (DeviceErrorCodes.TrustRequired, "The iPhone does not trust this PC yet. Tap Trust on the phone."),
            "InvalidService" or "ServiceProhibited" or "ServiceLimit"
                => (DeviceErrorCodes.NotSupported, "The iPhone refused to start a service."),
            "MissingValue" => (DeviceErrorCodes.NotFound, "The iPhone has no such value."),
            _ => (DeviceErrorCodes.Protocol, "The iPhone refused the request."),
        };
        var ex = new DeviceException(code, $"{text} (lockdown {request}: {error})");
        ex.Data[IosErrors.LockdownErrorKey] = error;
        return ex;
    }

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}

public sealed record LockdownServiceInfo(string Name, int Port, bool EnableServiceSsl);
