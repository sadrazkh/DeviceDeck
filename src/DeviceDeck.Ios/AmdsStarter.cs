using System.ComponentModel;
using System.Diagnostics;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace DeviceDeck.Ios;

/// <summary>Runs a program with administrator rights: Windows shows its own prompt (UAC) first; never silently.</summary>
public interface IElevatedRunner
{
    /// <summary>The program's exit code. Throws <see cref="Win32Exception"/> 1223 when the prompt was declined.</summary>
    Task<int> RunElevatedAsync(string fileName, string arguments, CancellationToken ct);
}

/// <summary>ShellExecute with the "runas" verb: the UAC prompt, then the program, without a console window.</summary>
public sealed class ShellElevatedRunner : IElevatedRunner
{
    public async Task<int> RunElevatedAsync(string fileName, string arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} didn't start.");
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }
}

/// <summary>
/// "Start Apple Mobile Device Service" on the Drivers card: `sc start "Apple Mobile Device Service"` through Windows'
/// administrator prompt (starting a service needs it), then waits for usbmux to answer.
/// </summary>
internal sealed class AmdsStarter(
    IUsbmuxConnector usbmux,
    IElevatedRunner runner,
    Func<(bool Installed, string? Exe)> findService,
    TimeSpan? wait = null,
    TimeSpan? pollInterval = null)
{
    private const int ErrorCancelled = 1223; // the UAC prompt was declined
    private const int ServiceAlreadyRunning = 1056;
    private const int ServiceDisabled = 1058;
    private const int ServiceDoesNotExist = 1060;

    /// <summary>sc.exe in System32.</summary>
    public static string ScExe { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");

    /// <summary>The arguments: the service's key name, quoted.</summary>
    public static string Arguments { get; } = $"start \"{AppleMobileDeviceService.ServiceName}\"";

    public async Task StartAsync(CancellationToken ct)
    {
        if (!findService().Installed)
        {
            throw new DeviceException(DeviceErrorCodes.DriverMissing,
                "Apple Mobile Device Support isn't installed. Get Apple Devices from the Microsoft Store first.");
        }

        if (await AnswersAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        int exit;
        try
        {
            exit = await runner.RunElevatedAsync(ScExe, Arguments, ct).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied,
                "Windows asked for permission to start Apple Mobile Device Service, and it wasn't given.", ex);
        }

        switch (exit)
        {
            case 0:
            case ServiceAlreadyRunning:
                break;
            case ServiceDisabled:
                throw new DeviceException(DeviceErrorCodes.DriverMissing,
                    "Apple Mobile Device Service is turned off in Windows. In Services, set its startup type to Automatic, then try again.");
            case ServiceDoesNotExist:
                throw new DeviceException(DeviceErrorCodes.DriverMissing,
                    "Apple Mobile Device Service isn't installed. Get Apple Devices from the Microsoft Store first.");
            default:
                throw new DeviceException(DeviceErrorCodes.DriverMissing, $"Windows couldn't start Apple Mobile Device Service (error {exit}).");
        }

        // The service reports "start pending" first; usbmux answers a moment later.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait ?? TimeSpan.FromSeconds(20));
        try
        {
            while (!await AnswersAsync(timeout.Token).ConfigureAwait(false))
            {
                await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(500), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceException(DeviceErrorCodes.DriverMissing,
                "Apple Mobile Device Service started but isn't answering yet. Check again in a moment.");
        }
    }

    private async Task<bool> AnswersAsync(CancellationToken ct)
    {
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(3));
            await using var client = await usbmux.ConnectAsync(probe.Token).ConfigureAwait(false);
            await client.ListDevicesAsync(probe.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
