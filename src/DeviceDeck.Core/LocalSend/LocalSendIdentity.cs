using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.LocalSend;

/// <summary>
/// Who DeviceDeck says it is to LocalSend: the name the phone's Accept question shows ("DeviceDeck (OFFICE-PC)") and a
/// random fingerprint. DeviceDeck runs no LocalSend server, so the fingerprint only keeps it from finding itself.
/// </summary>
public sealed class LocalSendIdentity(string alias, string fingerprint)
{
    public static LocalSendIdentity ForThisPc() => new($"DeviceDeck ({Environment.MachineName})", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));

    public string Alias { get; } = alias;
    public string Fingerprint { get; } = fingerprint;

    public LocalSendInfo Info(bool? announce = null) => new()
    {
        Alias = Alias,
        Version = LocalSendProtocol.Version,
        DeviceModel = "Windows",
        DeviceType = "desktop",
        Fingerprint = Fingerprint,
        Port = LocalSendProtocol.DefaultPort,
        Protocol = "https",
        Download = false,
        Announce = announce,
    };

    /// <summary>A client for a job's target: through the phone's cable (usbmux) or straight to its address.</summary>
    public static LocalSendClient Connect(IDevice device, LocalSendTarget target, LocalSendTimeouts? timeouts = null)
    {
        if (target.Route == LocalSendRoute.Usb)
        {
            if (device is not IDevicePortTunnel tunnel)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't be reached over its cable; use Wi-Fi.");
            }

            return new LocalSendClient(
                new LocalSendEndpoint("127.0.0.1", target.Port, target.Https, ct => new ValueTask<Stream>(tunnel.ConnectPortAsync(target.Port, ct))),
                target.Fingerprint, timeouts);
        }

        if (string.IsNullOrWhiteSpace(target.Address))
        {
            throw new ArgumentException("A Wi-Fi target needs the receiver's address.");
        }

        return new LocalSendClient(new LocalSendEndpoint(target.Address, target.Port, target.Https), target.Fingerprint, timeouts);
    }
}

/// <summary>MIME types for LocalSend's fileType: image/* and video/* are what it saves into Photos.</summary>
public static class LocalSendMime
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".bmp"] = "image/bmp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".dng"] = "image/x-adobe-dng",
        [".mov"] = "video/quicktime",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/x-m4v",
        [".3gp"] = "video/3gpp",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".avi"] = "video/x-msvideo",
    };

    public static string Of(string fileName) =>
        Types.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    public static bool IsMedia(string fileName) => Of(fileName) is var type && (type.StartsWith("image/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal));
}
