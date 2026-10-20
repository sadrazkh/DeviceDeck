using System.Runtime.CompilerServices;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Toolbox;

namespace DeviceDeck.Ios.Device;

// Toolbox services of an iPhone: the live log (syslog_relay), crash reports (crashreportmover + crashreportcopymobile)
// and the "show this iPhone when on Wi-Fi" switch (lockdown's wireless_lockdown domain).
public sealed partial class IosDevice : IDeviceLogSource, ICrashReportSource, IWifiSyncSettings
{
    public const string WirelessLockdownDomain = "com.apple.mobile.wireless_lockdown";
    public const string WifiConnectionsKey = "EnableWifiConnections";

    public async IAsyncEnumerable<DeviceLogEntry> StreamLogAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var connection = await UseAsync(c => c.StartServiceAsync(SyslogRelay.ServiceName, ct)).ConfigureAwait(false);
        await using var stream = connection.Detach();
        var splitter = new SyslogRelay.Splitter();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await ReadLogAsync(stream, buffer, ct).ConfigureAwait(false);
            if (read < 0)
            {
                yield break; // cancelled: the caller stopped listening
            }

            var now = DateTimeOffset.Now;
            foreach (var message in splitter.Push(buffer.AsSpan(0, read)))
            {
                yield return SyslogRelay.Parse(SyslogRelay.Decode(message), now);
            }
        }
    }

    /// <summary>Bytes read, or -1 once <paramref name="ct"/> is cancelled. The phone closing the stream means it went away.</summary>
    private static async Task<int> ReadLogAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int read;
        try
        {
            read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return -1;
        }
        catch (Exception ex) when (IosErrors.Translate(ex) is DeviceException mapped && !ReferenceEquals(mapped, ex))
        {
            if (ct.IsCancellationRequested)
            {
                return -1;
            }

            throw mapped;
        }

        if (read == 0)
        {
            throw IosErrors.Disconnected("The iPhone stopped sending its log.");
        }

        return read;
    }

    public Task<CrashReportList> ListCrashReportsAsync(CancellationToken ct) => UseAsync(async c =>
    {
        try
        {
            // Moves reports written since the last sync into the folder the copy service serves.
            await using var mover = await c.StartServiceAsync(IosCrashReports.MoverService, ct).ConfigureAwait(false);
            await IosCrashReports.WaitForMoverAsync(mover.Stream, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.NotSupported or DeviceErrorCodes.Disconnected)
        {
            // Older reports are still listed without the move.
        }

        await using var afc = await OpenCrashFolderAsync(c, ct).ConfigureAwait(false);
        var reports = await IosCrashReports.ListAsync(afc, ct).ConfigureAwait(false);
        return new CrashReportList(reports, CanDelete: true, Unreadable: []);
    });

    public Task ReadCrashReportAsync(string id, Stream destination, CancellationToken ct) => UseAsync(async c =>
    {
        var path = IosCrashReports.PathOf(id);
        await using var afc = await OpenCrashFolderAsync(c, ct).ConfigureAwait(false);
        await afc.ReadFileAsync(path, destination, null, ct).ConfigureAwait(false);
    });

    public Task DeleteCrashReportsAsync(IReadOnlyList<string> ids, CancellationToken ct) => UseAsync(async c =>
    {
        var paths = ids.Select(IosCrashReports.PathOf).ToList();
        await using var afc = await OpenCrashFolderAsync(c, ct).ConfigureAwait(false);
        foreach (var path in paths)
        {
            try
            {
                await afc.RemovePathAsync(path, ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.PathNotFound)
            {
                // Already gone: that is what was asked for.
            }
        }
    });

    private static async Task<AfcClient> OpenCrashFolderAsync(DeviceConnector c, CancellationToken ct)
    {
        var connection = await c.StartServiceAsync(IosCrashReports.CopyService, ct).ConfigureAwait(false);
        return new AfcClient(connection.Detach());
    }

    public Task<bool?> GetWifiSyncAsync(CancellationToken ct) => UseAsync(async c =>
    {
        await using var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false);
        return await lockdown.GetValueAsync(WirelessLockdownDomain, WifiConnectionsKey, ct).ConfigureAwait(false) as bool?;
    });

    public Task SetWifiSyncAsync(bool enabled, CancellationToken ct) => UseAsync(async c =>
    {
        await using var lockdown = await c.OpenSessionAsync(ct).ConfigureAwait(false);
        await lockdown.RequestAsync("SetValue", new PlistDict
        {
            ["Domain"] = WirelessLockdownDomain,
            ["Key"] = WifiConnectionsKey,
            ["Value"] = enabled,
        }, ct).ConfigureAwait(false);
    });
}
