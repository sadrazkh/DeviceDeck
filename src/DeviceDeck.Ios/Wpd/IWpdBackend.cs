namespace DeviceDeck.Ios.Wpd;

// Windows Portable Devices (WPD), as far as deleting photos needs it: the camera connection (PTP) that File Explorer
// shows as This PC › Apple iPhone › Internal Storage › DCIM. ComWpdBackend is the real one (Windows only); tests fake it.
// Calls block: callers run them off the UI thread, one connection at a time per phone.

public static class WpdBackends
{
    /// <summary>Windows Portable Devices on this PC, or null off Windows.</summary>
    public static IWpdBackend? ForThisPc() => OperatingSystem.IsWindows() ? new ComWpdBackend() : null;
}

public interface IWpdBackend
{
    /// <summary>Every portable device Windows lists right now (cameras, phones, players).</summary>
    IReadOnlyList<WpdDeviceInfo> ListDevices();

    /// <summary>Opens one device. <paramref name="readOnly"/> asks Windows for read access only, so nothing can be deleted through it.</summary>
    IWpdConnection Open(string pnpDeviceId, bool readOnly);
}

/// <param name="PnpId">The device interface path Windows lists (\\?\usb#vid_05ac&amp;pid_12a8&amp;mi_00#…).</param>
/// <param name="UsbSerial">The serial number of the USB device it belongs to (an iPhone's UDID without the dash); null when not on USB.</param>
public sealed record WpdDeviceInfo(string PnpId, string? FriendlyName, string? Manufacturer, string? Description, string? UsbSerial);

/// <summary>The device object's own properties (WPD_DEVICE_*).</summary>
public sealed record WpdDeviceProperties(string? FriendlyName, string? Manufacturer, string? Model, string? SerialNumber, string? Protocol, string? Firmware);

public enum WpdObjectKind
{
    /// <summary>A storage or another functional object (Internal Storage).</summary>
    Functional,
    Folder,
    File,
}

/// <param name="Id">The object id, valid while the connection is open.</param>
/// <param name="FileName">WPD_OBJECT_ORIGINAL_FILE_NAME, else WPD_OBJECT_NAME.</param>
/// <param name="Size">WPD_OBJECT_SIZE; null when the device doesn't say.</param>
/// <param name="CanDelete">WPD_OBJECT_CAN_DELETE; null when the device doesn't say.</param>
public sealed record WpdObject(
    string Id,
    string? ParentId,
    string? FileName,
    WpdObjectKind Kind,
    long? Size,
    bool? CanDelete,
    DateTimeOffset? Created,
    string? PersistentId);

public interface IWpdConnection : IDisposable
{
    WpdDeviceProperties Device { get; }

    /// <summary>The ids of an object's children ("DEVICE" is the root).</summary>
    IReadOnlyList<string> ChildIds(string parentId, CancellationToken ct);

    /// <summary>The object's properties, or null when it doesn't exist (any more).</summary>
    WpdObject? Get(string objectId);

    /// <summary>
    /// Deletes these objects, never recursively. Returns one HRESULT per id, in order: 0 deleted, anything else not.
    /// Throws <see cref="WpdException"/> when the device refused the whole call.
    /// </summary>
    IReadOnlyList<int> Delete(IReadOnlyList<string> objectIds);
}

/// <summary>A WPD call failed; <see cref="Exception.HResult"/> says how.</summary>
public sealed class WpdException : Exception
{
    public WpdException(string message, int hresult, Exception? inner = null) : base(message, inner) => HResult = hresult;
}

public static class WpdIds
{
    /// <summary>The root object: the device itself (WPD_DEVICE_OBJECT_ID).</summary>
    public const string DeviceObjectId = "DEVICE";
}

public static class WpdErrors
{
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int NotFound = unchecked((int)0x80070490);
    public const int FileNotFound = unchecked((int)0x80070002);
    public const int InvalidArg = unchecked((int)0x80070057);
    public const int DeviceNotConnected = unchecked((int)0x8007048F);
    public const int NotReady = unchecked((int)0x80070015);
    public const int GenFailure = unchecked((int)0x8007001F);
    public const int Busy = unchecked((int)0x800700AA);
    public const int NotSupported = unchecked((int)0x80070032);

    public static string Describe(int hr) => hr switch
    {
        0 => "ok",
        AccessDenied => "the iPhone refused (access denied)",
        NotFound or FileNotFound => "the iPhone no longer lists it",
        InvalidArg => "the iPhone doesn't know that object",
        DeviceNotConnected or NotReady or GenFailure => "the iPhone stopped answering",
        Busy => "the iPhone is busy",
        NotSupported => "the iPhone doesn't support deleting it",
        _ => $"error 0x{hr:X8}",
    };
}
