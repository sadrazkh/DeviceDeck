using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;

namespace DeviceDeck.Ios.Backup;

/// <summary>
/// com.apple.mobilebackup2: DeviceLink 300.0, then a "Hello" agreeing on protocol 2.0/2.1, then one request
/// (Backup, Restore, ChangePassword…) whose file traffic <see cref="BackupFolderHost"/> serves until the result.
/// </summary>
internal sealed class MobileBackup2Client : IAsyncDisposable
{
    public const string ServiceName = "com.apple.mobilebackup2";
    private static readonly List<object> SupportedVersions = [2.0, 2.1];

    private readonly DeviceLinkChannel _dl;

    private MobileBackup2Client(DeviceLinkChannel dl, double protocolVersion)
    {
        _dl = dl;
        ProtocolVersion = protocolVersion;
    }

    public double ProtocolVersion { get; }

    public DeviceLinkChannel Channel => _dl;

    public static async Task<MobileBackup2Client> ConnectAsync(DeviceConnector connector, CancellationToken ct)
    {
        var stream = await BackupServices.StartWithEscrowBagAsync(connector, ServiceName, ct).ConfigureAwait(false);
        var dl = new DeviceLinkChannel(stream);
        try
        {
            await dl.VersionExchangeAsync(300, 0, ct).ConfigureAwait(false);
            await dl.SendProcessMessageAsync(new PlistDict
            {
                ["MessageName"] = "Hello",
                ["SupportedProtocolVersions"] = SupportedVersions,
            }, ct).ConfigureAwait(false);
            var reply = await dl.ReceiveProcessMessageAsync(ct).ConfigureAwait(false);
            var error = reply.GetLong("ErrorCode");
            if (reply.GetString("MessageName") != "Response" || error is null)
                throw new FormatException("mobilebackup2 answered Hello without a response code.");
            if (error != 0)
                throw new DeviceException(DeviceErrorCodes.NotSupported, error == 1
                    ? "This iPhone's backup service speaks a protocol version DeviceDeck doesn't know."
                    : $"The iPhone's backup service refused to start (error {error}).");
            return new MobileBackup2Client(dl, reply.GetDouble("ProtocolVersion") ?? 0);
        }
        catch
        {
            await dl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Backup, Restore, Info, List: {TargetIdentifier, SourceIdentifier?, Options?}.</summary>
    public Task SendRequestAsync(string request, string target, string? source, PlistDict? options, CancellationToken ct)
    {
        var message = new PlistDict { ["MessageName"] = request, ["TargetIdentifier"] = target };
        if (source is not null)
            message["SourceIdentifier"] = source;
        if (options is not null)
            message["Options"] = options;
        return _dl.SendProcessMessageAsync(message, ct);
    }

    /// <summary>A message that carries its own fields (ChangePassword).</summary>
    public Task SendMessageAsync(string name, PlistDict fields, CancellationToken ct)
    {
        var message = new PlistDict(fields) { ["MessageName"] = name };
        return _dl.SendProcessMessageAsync(message, ct);
    }

    /// <summary>Serves the phone until it sends the result; a non-zero ErrorCode throws with the phone's own text.</summary>
    public async Task<PlistDict> RunAsync(BackupFolderHost host, string operation, CancellationToken ct)
    {
        var result = await host.RunAsync(_dl, ct).ConfigureAwait(false);
        var code = result.GetLong("ErrorCode") ?? 0;
        if (code != 0)
            throw ErrorFor(operation, code, result.GetString("ErrorDescription"));
        return result;
    }

    /// <summary>Maps the phone's result to DeviceDeck's codes; the phone's description stays in the message.</summary>
    public static DeviceException ErrorFor(string operation, long code, string? description)
    {
        var text = description ?? "";
        var detail = string.IsNullOrWhiteSpace(text) ? $"error {code}" : $"{text} (error {code})";
        if (Contains(text, "Find My"))
            return new DeviceException(BackupErrors.Refused, $"Turn off Find My on the iPhone before restoring. The iPhone said: {detail}.");
        if (Contains(text, "password"))
            return new DeviceException(BackupErrors.WrongPassword, $"The backup password isn't right. The iPhone said: {detail}.");
        if (Contains(text, "space"))
            return new DeviceException(BackupErrors.NoSpace, $"There isn't enough free space for the {operation}. The iPhone said: {detail}.");
        if (Contains(text, "cancel"))
            return new DeviceException(BackupErrors.Refused, $"The {operation} was cancelled on the iPhone ({detail}).");
        return new DeviceException(DeviceErrorCodes.Protocol, $"The iPhone stopped the {operation}: {detail}.");
    }

    private static bool Contains(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _dl.DisconnectAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or NotSupportedException)
        {
            // The phone may have hung up first.
        }
        await _dl.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Starting the services a backup uses.</summary>
internal static class BackupServices
{
    public const string NotificationProxy = "com.apple.mobile.notification_proxy";

    /// <summary>
    /// Starts a lockdown service with the pair record's escrow bag, as iTunes does for backups, so it starts while the
    /// phone is locked (after its first unlock since boot). TLS when the phone asks for it.
    /// </summary>
    public static async Task<Stream> StartWithEscrowBagAsync(DeviceConnector connector, string service, CancellationToken ct)
    {
        var record = await connector.GetPairRecordAsync(ct).ConfigureAwait(false)
                     ?? throw new DeviceException(DeviceErrorCodes.TrustRequired, "This PC is not paired with the iPhone yet. Tap Trust on the phone.");
        long port;
        bool ssl;
        await using (var lockdown = await connector.OpenSessionAsync(ct).ConfigureAwait(false))
        {
            var args = new PlistDict { ["Service"] = service };
            if (record.EscrowBag is { Length: > 0 } bag)
                args["EscrowBag"] = bag;
            var reply = await lockdown.RequestAsync("StartService", args, ct).ConfigureAwait(false);
            port = reply.GetLong("Port") ?? throw new FormatException($"StartService {service} returned no port.");
            ssl = reply.GetBool("EnableServiceSSL") == true;
        }

        var stream = await connector.ConnectPortAsync((int)port, ct).ConfigureAwait(false);
        if (!ssl)
            return stream;
        try
        {
            return await IosTls.AuthenticateAsync(stream, record.ClientCertificate, ct).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
