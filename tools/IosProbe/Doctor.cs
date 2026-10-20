using System.Diagnostics;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios;
using DeviceDeck.Ios.Protocol.Lockdown;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace IosProbe;

/// <summary>Checks everything that can be checked without a phone: the service, usbmux, and the pair records.</summary>
internal static class Doctor
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var problems = 0;
        var connector = new TcpUsbmuxConnector();
        Console.WriteLine("DeviceDeck iOS doctor");
        Console.WriteLine();

        // 1. Apple Mobile Device Service on disk.
        var (installed, exe) = AppleMobileDeviceService.FindService();
        var version = exe is null ? null : AppleMobileDeviceService.FileVersion(exe);
        if (installed)
            Line("Apple Mobile Device Service", $"installed, version {version ?? "unknown"}");
        else
        {
            problems++;
            Line("Apple Mobile Device Service", "NOT installed — install Apple Devices from the Microsoft Store (or iTunes)");
        }
        if (exe is not null)
            Line("  executable", exe);

        // 2. usbmux answers ListDevices.
        IReadOnlyList<UsbmuxDevice>? devices = null;
        try
        {
            var sw = Stopwatch.StartNew();
            await using var client = await connector.ConnectAsync(ct);
            devices = await client.ListDevicesAsync(ct);
            Line($"usbmux {connector.Endpoint}", $"reachable; ListDevices answered in {sw.ElapsedMilliseconds} ms with {devices.Count} device(s)");
            foreach (var d in devices)
                Line("", $"  usbmux id {d.DeviceId}: {d.Udid} over {d.ConnectionType}");
        }
        catch (DeviceException ex)
        {
            problems++;
            Line($"usbmux {connector.Endpoint}", $"NOT reachable [{ex.Code}]: {ex.Message}");
        }

        // 3. ReadBUID — the PC's identity in pairings.
        if (devices is not null)
        {
            try
            {
                await using var client = await connector.ConnectAsync(ct);
                var buid = await client.ReadBuidAsync(ct);
                Line("usbmux ReadBUID", buid is null ? "not answered (pairing will reuse an existing record's SystemBUID)" : $"SystemBUID {buid}");
            }
            catch (Exception ex) when (ex is DeviceException or IOException)
            {
                Line("usbmux ReadBUID", "failed: " + ex.Message);
            }
        }

        // 4. What the app's Drivers card will say.
        var status = await AppleMobileDeviceService.GetStatusAsync(connector, ct);
        Line("Driver status (Drivers card)", $"{status.State}{(status.Version is null ? "" : ", " + status.Version)}{(status.Detail is null ? "" : " — " + status.Detail)}");
        if (status.State != DriverState.Ready)
            problems++;

        // 5. Pair records: listed, parsed, and their host keys loaded the way the TLS session will load them.
        var store = new PairRecordStore(connector, null, new IosProviderOptions().LocalPairRecordDirectory);
        var files = store.ListFiles();
        Line("Pair records", $"{files.Count} found ({store.Directory}; DeviceDeck's own: {store.LocalDirectory})");
        foreach (var file in files)
            Line("", $"  {file.Udid,-40} {file.ModifiedUtc.ToLocalTime():yyyy-MM-dd}  {CheckRecord(file)}");

        // 6. Does usbmux hand pair records out itself? (DeviceDeck falls back to the files if not.)
        if (devices is not null && files.Count > 0)
        {
            try
            {
                await using var client = await connector.ConnectAsync(ct);
                var bytes = await client.ReadPairRecordAsync(files[0].Udid, ct);
                Line("usbmux ReadPairRecord", bytes is null ? "not answered; DeviceDeck reads the files above" : $"supported ({bytes.Length} bytes for {files[0].Udid})");
            }
            catch (Exception ex) when (ex is DeviceException or IOException)
            {
                Line("usbmux ReadPairRecord", "failed: " + ex.Message);
            }
        }

        Line("Ports", "DeviceDeck only connects to usbmux; it listens on no port");
        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "Result: OK" : $"Result: {problems} problem(s)");
        return problems == 0 ? 0 : 2;
    }

    private static string CheckRecord(PairRecordFile file)
    {
        try
        {
            using var record = PairRecord.Parse(File.ReadAllBytes(file.Path));
            var cert = record.ClientCertificate;
            return cert.HasPrivateKey
                ? $"ok — host certificate and key load for TLS (valid until {cert.NotAfter:yyyy-MM-dd})"
                : "host certificate has no usable key";
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return $"unusable: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void Line(string label, string value) =>
        Console.WriteLine(label.Length == 0 ? $"  {"",-30}   {value}" : $"  {label,-30} : {value}");
}
