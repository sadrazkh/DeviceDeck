using System.Security.Cryptography;
using System.Text;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol.Lockdown;

namespace DeviceDeck.Ios.Tests.Fakes;

/// <summary>A made-up phone key pair and a pair record for it, generated per test run (nothing secret is committed).</summary>
internal static class TestPairing
{
    public const string HostId = "5F3C2B10-8A9D-4E6F-9C1B-0D2E3F4A5B6C";
    public const string SystemBuid = "A1B2C3D4-0000-4000-8000-00000000BEEF";

    private static readonly Lazy<(PairingCertificates Certs, RSA DeviceKey)> Shared = new(() =>
    {
        var deviceKey = RSA.Create(2048);
        var devicePublicPem = Encoding.ASCII.GetBytes(deviceKey.ExportRSAPublicKeyPem());
        return (PairingCertificates.Create(devicePublicPem), deviceKey);
    });

    public static PairingCertificates Certificates => Shared.Value.Certs;

    public static RSA DeviceKey => Shared.Value.DeviceKey;

    public static PlistDict StoredRecord() => Certificates.ToStoredRecord(HostId, SystemBuid, [1, 2, 3], "a4:c3:61:00:11:23");

    public static byte[] StoredRecordXml() => PlistSerializer.ToXml(StoredRecord());
}
