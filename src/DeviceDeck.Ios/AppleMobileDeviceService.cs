using System.Diagnostics;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Usbmux;
using Microsoft.Win32;

namespace DeviceDeck.Ios;

/// <summary>Is Apple Mobile Device Service installed, running and answering usbmux on this PC?</summary>
public static class AppleMobileDeviceService
{
    public const string ServiceName = "Apple Mobile Device Service";

    private static readonly string DefaultExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        "Apple", "Mobile Device Support", "AppleMobileDeviceService.exe");

    public static async Task<DriverStatus> GetStatusAsync(IUsbmuxConnector usbmux, CancellationToken ct)
    {
        var (installed, exe) = FindService();
        var version = exe is null ? null : FileVersion(exe);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var client = await usbmux.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.ListDevicesAsync(timeout.Token).ConfigureAwait(false);
            return new DriverStatus("ios", DriverState.Ready, version, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!installed)
                return new DriverStatus("ios", DriverState.Missing, null,
                    "Apple Mobile Device Support is not installed. Install Apple Devices from the Microsoft Store (or iTunes).");
            if (ex is DeviceException { Code: DeviceErrorCodes.DriverMissing })
                return new DriverStatus("ios", DriverState.Stopped, version,
                    "Apple Mobile Device Service is installed but not answering. Start it in Services, or reinstall Apple Devices.");
            return new DriverStatus("ios", DriverState.Error, version, "Apple Mobile Device Service answered unexpectedly: " + ex.Message);
        }
    }

    /// <summary>Whether the service is registered, and the path of its executable when known.</summary>
    public static (bool Installed, string? Exe) FindService()
    {
        if (!OperatingSystem.IsWindows())
            return (false, null);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            if (key?.GetValue("ImagePath") is string imagePath)
                return (true, ExePath(imagePath));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
        return File.Exists(DefaultExe) ? (true, DefaultExe) : (false, null);
    }

    public static string? FileVersion(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return info.FileVersion ?? info.ProductVersion;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>"\"C:\…\AppleMobileDeviceService.exe\" -arg" → the exe path.</summary>
    internal static string ExePath(string imagePath)
    {
        var s = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 0 ? s[1..end] : s.Trim('"');
        }
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : s;
    }
}
