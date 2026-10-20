using System.Security.Cryptography.X509Certificates;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Lockdown;

/// <summary>
/// What the PC keeps after the phone trusted it: our host certificate and key, the root that signed
/// them, the HostID the phone knows us by, and the SystemBUID. Contains private keys: never log it.
/// </summary>
public sealed class PairRecord : IDisposable
{
    private X509Certificate2? _clientCertificate;

    private PairRecord(PlistDict raw)
    {
        Raw = raw;
        HostId = raw.GetString("HostID") ?? throw new FormatException("Pair record has no HostID.");
        SystemBuid = raw.GetString("SystemBUID") ?? throw new FormatException("Pair record has no SystemBUID.");
        HostCertificate = raw.GetData("HostCertificate") ?? throw new FormatException("Pair record has no HostCertificate.");
        HostPrivateKey = raw.GetData("HostPrivateKey") ?? throw new FormatException("Pair record has no HostPrivateKey.");
        RootCertificate = raw.GetData("RootCertificate") ?? throw new FormatException("Pair record has no RootCertificate.");
        DeviceCertificate = raw.GetData("DeviceCertificate");
        EscrowBag = raw.GetData("EscrowBag");
        WiFiMacAddress = raw.GetString("WiFiMACAddress");
    }

    public PlistDict Raw { get; }
    public string HostId { get; }
    public string SystemBuid { get; }
    public byte[] HostCertificate { get; }
    public byte[] HostPrivateKey { get; }
    public byte[] RootCertificate { get; }
    public byte[]? DeviceCertificate { get; }
    public byte[]? EscrowBag { get; }
    public string? WiFiMacAddress { get; }

    public static PairRecord Parse(ReadOnlySpan<byte> plist) => new(PlistSerializer.ParseDict(plist));

    public static PairRecord FromPlist(PlistDict dict) => new(dict);

    /// <summary>The TLS client certificate, built once and cached for this record's lifetime.</summary>
    public X509Certificate2 ClientCertificate =>
        _clientCertificate ??= IosTls.CreateClientCertificate(HostCertificate, HostPrivateKey);

    public void Dispose()
    {
        _clientCertificate?.Dispose();
        _clientCertificate = null;
    }
}
