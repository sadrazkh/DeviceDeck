using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Protocol.Lockdown;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace DeviceDeck.Ios.Protocol;

/// <summary>
/// Opens connections to one phone: lockdown (with or without a session) and lockdown-started services.
/// Every call opens fresh sockets; callers dispose what they get.
/// </summary>
public sealed class DeviceConnector(IUsbmuxConnector usbmux, int usbmuxDeviceId, string udid, Func<CancellationToken, Task<PairRecord?>> pairRecord)
{
    public string Udid { get; } = udid;

    public int UsbmuxDeviceId { get; } = usbmuxDeviceId;

    /// <summary>A raw tunnel to a port on the phone.</summary>
    public async Task<Stream> ConnectPortAsync(int port, CancellationToken ct)
    {
        var client = await usbmux.ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            return await client.ConnectToDeviceAsync(UsbmuxDeviceId, port, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Lockdown without a session: QueryType, a few GetValue keys, Pair.</summary>
    public async Task<LockdownClient> OpenLockdownAsync(CancellationToken ct)
    {
        var stream = await ConnectPortAsync(LockdownClient.Port, ct).ConfigureAwait(false);
        var lockdown = new LockdownClient(new PlistConnection(stream));
        try
        {
            var type = await lockdown.QueryTypeAsync(ct).ConfigureAwait(false);
            if (type is not null && type != "com.apple.mobile.lockdown")
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"The phone is not in normal mode (lockdown type '{type}').");
            return lockdown;
        }
        catch
        {
            await lockdown.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Lockdown with a TLS session as the paired host. Throws TrustRequired without a pair record.</summary>
    public async Task<LockdownClient> OpenSessionAsync(CancellationToken ct)
    {
        var record = await pairRecord(ct).ConfigureAwait(false)
                     ?? throw new DeviceException(DeviceErrorCodes.TrustRequired, "This PC is not paired with the iPhone yet. Tap Trust on the phone.");
        var lockdown = await OpenLockdownAsync(ct).ConfigureAwait(false);
        try
        {
            await lockdown.StartSessionAsync(record, ct).ConfigureAwait(false);
            return lockdown;
        }
        catch
        {
            await lockdown.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a lockdown service and connects to it (TLS when the phone asks for it).</summary>
    public async Task<PlistConnection> StartServiceAsync(string service, CancellationToken ct)
    {
        LockdownServiceInfo info;
        await using (var lockdown = await OpenSessionAsync(ct).ConfigureAwait(false))
            info = await lockdown.StartServiceAsync(service, ct).ConfigureAwait(false);

        var stream = await ConnectPortAsync(info.Port, ct).ConfigureAwait(false);
        var connection = new PlistConnection(stream);
        if (info.EnableServiceSsl)
        {
            try
            {
                var record = await pairRecord(ct).ConfigureAwait(false)
                             ?? throw new DeviceException(DeviceErrorCodes.TrustRequired, "The pair record disappeared.");
                await connection.UpgradeToTlsAsync(record.ClientCertificate, ct).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        return connection;
    }

    /// <summary>com.apple.afc: the Media folder (DCIM, PhotoData, Downloads, …).</summary>
    public async Task<AfcClient> StartAfcAsync(CancellationToken ct)
    {
        var connection = await StartServiceAsync(ServiceNames.Afc, ct).ConfigureAwait(false);
        return new AfcClient(connection.Detach());
    }
}

public static class ServiceNames
{
    public const string Afc = "com.apple.afc";
    public const string HouseArrest = "com.apple.mobile.house_arrest";
    public const string InstallationProxy = "com.apple.mobile.installation_proxy";
    public const string SpringBoard = "com.apple.springboardservices";
    public const string DiagnosticsRelay = "com.apple.mobile.diagnostics_relay";
}
