using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Services;

/// <summary>diagnostics_relay: IORegistry lookups, restart and shutdown.</summary>
public static class DiagnosticsRelayClient
{
    /// <summary>The IORegistry properties of the first entry of <paramref name="entryClass"/> (e.g. IOPMPowerSource).</summary>
    public static async Task<PlistDict?> IORegistryAsync(PlistConnection connection, string entryClass, CancellationToken ct)
    {
        var reply = await RequestAsync(connection, new PlistDict
        {
            ["Request"] = "IORegistry",
            ["EntryClass"] = entryClass,
        }, ct).ConfigureAwait(false);
        return reply.GetDict("Diagnostics")?.GetDict("IORegistry");
    }

    /// <summary>The phone restarts once this connection closes.</summary>
    public static Task RestartAsync(PlistConnection connection, CancellationToken ct) =>
        RequestAsync(connection, new PlistDict
        {
            ["Request"] = "Restart",
            ["WaitForDisconnect"] = true,
            ["DisplayPass"] = false,
            ["DisplayFail"] = false,
        }, ct);

    /// <summary>The phone shuts down once this connection closes.</summary>
    public static Task ShutdownAsync(PlistConnection connection, CancellationToken ct) =>
        RequestAsync(connection, new PlistDict
        {
            ["Request"] = "Shutdown",
            ["WaitForDisconnect"] = true,
            ["DisplayPass"] = false,
            ["DisplayFail"] = false,
        }, ct);

    public static async Task GoodbyeAsync(PlistConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.RequestAsync(new PlistDict { ["Request"] = "Goodbye" }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            // The phone may close first; that is fine.
        }
    }

    private static async Task<PlistDict> RequestAsync(PlistConnection connection, PlistDict request, CancellationToken ct)
    {
        var reply = await connection.RequestAsync(request, ct).ConfigureAwait(false);
        var status = reply.GetString("Status");
        if (status == "Success")
            return reply;
        var name = request.GetString("Request");
        throw status switch
        {
            "UnknownRequest" => new DeviceException(DeviceErrorCodes.NotSupported, $"The phone does not support diagnostics_relay {name}."),
            _ => new DeviceException(DeviceErrorCodes.Protocol, $"diagnostics_relay {name} failed ({status ?? "no status"})."),
        };
    }
}
