using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Usbmux;

/// <summary>One entry in usbmux's device list. A phone on USB and Wi-Fi at once appears twice, with two ids.</summary>
/// <param name="DeviceId">usbmux's handle, valid until the phone detaches.</param>
/// <param name="Udid">The phone's UDID, dashed the way pair records are named (00008110-001C…).</param>
public sealed record UsbmuxDevice(int DeviceId, string Udid, UsbmuxConnectionType ConnectionType, int? ProductId, PlistDict Properties)
{
    public static UsbmuxDevice FromProperties(int deviceId, PlistDict props)
    {
        var serial = props.GetString("SerialNumber")
                     ?? throw new FormatException("usbmux device entry has no SerialNumber.");
        var type = props.GetString("ConnectionType") switch
        {
            "USB" => UsbmuxConnectionType.Usb,
            "Network" => UsbmuxConnectionType.Network,
            _ => UsbmuxConnectionType.Unknown,
        };
        return new UsbmuxDevice(props.GetInt("DeviceID") ?? deviceId, NormalizeUdid(serial), type, props.GetInt("ProductID"), props);
    }

    /// <summary>
    /// Newer phones have 24-hex-digit UDIDs written as 8-16 with a dash. Some usbmux builds report
    /// them without the dash; pair records always use it.
    /// </summary>
    public static string NormalizeUdid(string serial)
    {
        var s = serial.Trim();
        if (s.Length == 24 && s.All(Uri.IsHexDigit))
            return string.Concat(s.AsSpan(0, 8), "-", s.AsSpan(8)).ToUpperInvariant();
        if (s.Length == 25 && s[8] == '-')
            return s.ToUpperInvariant();
        return s;
    }
}

public enum UsbmuxConnectionType { Unknown, Usb, Network }

public enum UsbmuxEventKind { Attached, Detached, Paired }

public sealed record UsbmuxEvent(UsbmuxEventKind Kind, int DeviceId, UsbmuxDevice? Device);
