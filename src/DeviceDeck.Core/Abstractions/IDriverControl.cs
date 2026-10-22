namespace DeviceDeck.Core.Abstractions;

/// <summary>
/// A provider whose driver the Drivers card can start, and that says when its driver may have changed (v0.6).
/// Android: starts (or restarts) DeviceDeck's private adb server. iPhone: asks Windows, with its administrator prompt,
/// to start Apple Mobile Device Service.
/// </summary>
public interface IDriverControl
{
    /// <summary>
    /// Starts the driver, or starts it again when it failed. Returns once it answers (or throws
    /// <see cref="DeviceException"/> with one plain sentence: <see cref="DeviceErrorCodes.DriverMissing"/> when there
    /// is nothing to start, <see cref="DeviceErrorCodes.PermissionDenied"/> when Windows' prompt was declined).
    /// </summary>
    Task StartDriverAsync(CancellationToken ct);

    /// <summary>
    /// Raised when the driver's status may have changed: a server came up or went away, tools were installed.
    /// The hub then asks <see cref="IDeviceProvider.GetDriverStatusAsync"/> again and tells the page if the answer changed.
    /// </summary>
    event EventHandler? DriverStatusChanged;
}
