using DeviceDeck.Android.Adb;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

internal sealed class AndroidDeviceActions(IAdbChannel channel) : IDeviceActions
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>adb's reboot: service — "" (system), "recovery" or "bootloader".</summary>
    public Task RebootAsync(RebootTarget target, CancellationToken ct) => channel.RebootAsync(target switch
    {
        RebootTarget.Recovery => "recovery",
        RebootTarget.Bootloader => "bootloader",
        _ => "",
    }, ct);

    /// <summary>`reboot -p` powers off; the connection drops as it does, which is the success case.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        ShellResult result;
        try
        {
            result = await channel.ShellAsync("reboot -p", ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.Disconnected or DeviceErrorCodes.NotFound)
        {
            return;
        }
        if (!result.Succeeded)
        {
            throw AdbErrors.FromShell("reboot", result);
        }
    }

    /// <summary>`screencap -p` over shell v2 (no PTY, so the PNG bytes arrive untouched).</summary>
    public async Task<byte[]> TakeScreenshotAsync(CancellationToken ct)
    {
        using var png = new MemoryStream();
        var result = await channel.ShellToStreamAsync("screencap -p", png, ct).ConfigureAwait(false);
        byte[] bytes = png.ToArray();
        if (!result.Succeeded || bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            throw new DeviceException(DeviceErrorCodes.Protocol,
                result.Error.Length > 0 ? $"The phone couldn't take a screenshot: {AdbErrors.FirstLine(result.Error)}" : "The phone couldn't take a screenshot.");
        }
        return bytes;
    }
}
