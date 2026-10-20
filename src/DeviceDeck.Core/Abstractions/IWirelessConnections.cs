namespace DeviceDeck.Core.Abstractions;

// Toolbox: connecting over Wi-Fi. Android's wireless debugging lives on the provider (the adb server pairs and
// connects); an iPhone's "show this iPhone when on Wi-Fi" switch lives on the device.

/// <param name="Serial">The adb serial of the phone once connected (it shows up in device.list under that id).</param>
/// <param name="Message">adb's own answer, for the details line.</param>
public sealed record WirelessResult(string? Serial, string Message);

/// <summary>Android wireless debugging (Android 11+) through the adb server.</summary>
public interface IWirelessDebugging
{
    /// <summary>
    /// Pairs with the "Pair device with pairing code" dialog's address and six-digit code.
    /// Throws unauthorized for a wrong or expired code, device_not_found when nothing answers there.
    /// </summary>
    Task<WirelessResult> PairAsync(string host, int port, string code, CancellationToken ct);

    /// <summary>Connects to a paired phone at the address shown on the Wireless debugging screen.</summary>
    Task<WirelessResult> ConnectAsync(string host, int port, CancellationToken ct);

    /// <summary>
    /// QR pairing: waits until a phone that scanned "WIFI:T:ADB;S:<paramref name="serviceName"/>;P:<paramref name="password"/>;;"
    /// announces itself on the network, pairs with it, then connects to it. Runs until done or <paramref name="ct"/> ends.
    /// </summary>
    Task<WirelessResult> PairWithQrAsync(string serviceName, string password, IProgress<string>? stage, CancellationToken ct);

    /// <summary>Drops a Wi-Fi connection (the phone stays paired).</summary>
    Task DisconnectAsync(string serial, CancellationToken ct);
}

/// <summary>An iPhone's Wi-Fi sync switch ("Show this iPhone when on Wi-Fi" in Finder / Apple Devices).</summary>
public interface IWifiSyncSettings
{
    /// <summary>Null when the phone doesn't say.</summary>
    Task<bool?> GetWifiSyncAsync(CancellationToken ct);

    Task SetWifiSyncAsync(bool enabled, CancellationToken ct);
}
