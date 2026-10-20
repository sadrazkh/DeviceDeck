using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Lockdown;

/// <summary>
/// The three certificates of a lockdown pairing: our root CA, our host certificate signed by it, and the
/// phone's public key (from GetValue DevicePublicKey) signed by the same root. RSA 2048, SHA-256, 10 years.
/// </summary>
public sealed record PairingCertificates(
    byte[] RootCertificatePem,
    byte[] RootPrivateKeyPem,
    byte[] HostCertificatePem,
    byte[] HostPrivateKeyPem,
    byte[] DeviceCertificatePem)
{
    public static PairingCertificates Create(byte[] devicePublicKeyPem)
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddYears(10);
        var subject = new X500DistinguishedName("");

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest(subject, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

        using var hostKey = RSA.Create(2048);
        var hostRequest = LeafRequest(subject, hostKey);
        using var host = hostRequest.Create(root, notBefore, notAfter, Serial(1));

        using var deviceKey = RSA.Create();
        deviceKey.ImportFromPem(Encoding.ASCII.GetString(devicePublicKeyPem).Trim('\0'));
        var deviceRequest = LeafRequest(subject, deviceKey);
        using var device = deviceRequest.Create(root, notBefore, notAfter, Serial(2));

        return new PairingCertificates(
            Pem(root.ExportCertificatePem()),
            Pem(rootKey.ExportRSAPrivateKeyPem()),
            Pem(host.ExportCertificatePem()),
            Pem(hostKey.ExportRSAPrivateKeyPem()),
            Pem(device.ExportCertificatePem()));
    }

    /// <summary>The record lockdown's Pair request carries (no private keys).</summary>
    public PlistDict ToPairRequestRecord(string hostId, string systemBuid) => new()
    {
        ["DeviceCertificate"] = DeviceCertificatePem,
        ["HostCertificate"] = HostCertificatePem,
        ["RootCertificate"] = RootCertificatePem,
        ["HostID"] = hostId,
        ["SystemBUID"] = systemBuid,
    };

    /// <summary>The record the PC keeps (and usbmux stores) once the phone says yes.</summary>
    public PlistDict ToStoredRecord(string hostId, string systemBuid, byte[]? escrowBag, string? wifiMac)
    {
        var d = ToPairRequestRecord(hostId, systemBuid);
        d["HostPrivateKey"] = HostPrivateKeyPem;
        d["RootPrivateKey"] = RootPrivateKeyPem;
        if (escrowBag is not null) d["EscrowBag"] = escrowBag;
        if (wifiMac is not null) d["WiFiMACAddress"] = wifiMac;
        return d;
    }

    private static CertificateRequest LeafRequest(X500DistinguishedName subject, RSA key)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static byte[] Serial(byte n) => [n];

    private static byte[] Pem(string pem) => Encoding.ASCII.GetBytes(pem.ReplaceLineEndings("\n") + "\n");
}
