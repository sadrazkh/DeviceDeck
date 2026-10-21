using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.LocalSend;

/// <summary>
/// Who DeviceDeck is to LocalSend: the name the phone's Accept question shows ("DeviceDeck (OFFICE-PC)") and a
/// self-signed certificate. LocalSend's server asks for the sender's certificate in the TLS handshake (mutual TLS)
/// and drops a connection without one, so every request presents it; the fingerprint is its SHA-256, as LocalSend's
/// own. Kept per PC (<see cref="ForThisPc"/>), so the phone sees the same sender every time and can keep it as a favourite.
/// </summary>
public sealed class LocalSendIdentity
{
    public const string FileName = "localsend-identity.pfx";

    public LocalSendIdentity(string alias, X509Certificate2 certificate)
    {
        Alias = alias;
        Certificate = certificate;
        Fingerprint = LocalSendProtocol.Fingerprint(certificate);
    }

    /// <summary>
    /// This PC's identity, from <paramref name="folder"/>/localsend-identity.pfx (made there the first time); a fresh one
    /// for this run when no folder is given or the file can't be read or written.
    /// </summary>
    public static LocalSendIdentity ForThisPc(string? folder = null)
    {
        var alias = $"DeviceDeck ({Environment.MachineName})";
        var path = folder is null ? null : Path.Combine(folder, FileName);
        if (path is not null && File.Exists(path))
        {
            try
            {
                return new LocalSendIdentity(alias, X509CertificateLoader.LoadPkcs12FromFile(path, null));
            }
            catch (CryptographicException)
            {
                // Unreadable: make a new one below (the phone then sees a new sender).
            }
        }

        var pkcs12 = NewCertificate();
        if (path is not null)
        {
            try
            {
                Directory.CreateDirectory(folder!);
                File.WriteAllBytes(path, pkcs12);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Then it lasts for this run only.
            }
        }

        return new LocalSendIdentity(alias, X509CertificateLoader.LoadPkcs12(pkcs12, null));
    }

    /// <summary>A new self-signed certificate (RSA 2048, ten years) as PKCS#12, the form SChannel can sign TLS with.</summary>
    public static byte[] NewCertificate(string name = "DeviceDeck")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return created.Export(X509ContentType.Pkcs12);
    }

    /// <summary>A throwaway identity (tests, probes).</summary>
    public static LocalSendIdentity Create(string alias) => new(alias, X509CertificateLoader.LoadPkcs12(NewCertificate(), null));

    public string Alias { get; }
    public X509Certificate2 Certificate { get; }
    public string Fingerprint { get; }

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
    public LocalSendClient Connect(IDevice device, LocalSendTarget target, LocalSendTimeouts? timeouts = null)
    {
        if (target.Route == LocalSendRoute.Usb)
        {
            if (device is not IDevicePortTunnel tunnel)
            {
                throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't be reached over its cable; use Wi-Fi.");
            }

            return new LocalSendClient(
                new LocalSendEndpoint("127.0.0.1", target.Port, target.Https, ct => new ValueTask<Stream>(tunnel.ConnectPortAsync(target.Port, ct))),
                target.Fingerprint, timeouts, Certificate);
        }

        if (string.IsNullOrWhiteSpace(target.Address))
        {
            throw new ArgumentException("A Wi-Fi target needs the receiver's address.");
        }

        return new LocalSendClient(new LocalSendEndpoint(target.Address, target.Port, target.Https), target.Fingerprint, timeouts, Certificate);
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
