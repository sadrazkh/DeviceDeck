using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Lockdown;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

public class LockdownTests
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private static (FakeUsbmux Mux, FakePhone Phone, DeviceConnector Connector) Setup(bool paired = true)
    {
        var mux = new FakeUsbmux();
        var phone = mux.Attach(1, new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId });
        if (paired)
            mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        var store = new PairRecordStore(mux, directory: Path.Combine(Path.GetTempPath(), "devicedeck-no-such-dir"));
        var connector = new DeviceConnector(mux, 1, Udid, ct => store.LoadAsync(Udid, ct));
        return (mux, phone, connector);
    }

    [Fact]
    public async Task Frames_are_big_endian_length_prefixed_plists()
    {
        var (client, server) = DuplexStream.CreatePair();
        await using var connection = new PlistConnection(client);
        await connection.SendAsync(new PlistDict { ["Request"] = "QueryType" }, Ct);

        var header = new byte[4];
        await server.ReadExactlyAsync(header, Ct);
        var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        var body = new byte[length];
        await server.ReadExactlyAsync(body, Ct);
        Assert.Equal("QueryType", PlistSerializer.ParseDict(body).GetString("Request"));

        // Replies may be binary.
        await Wire.WritePlistAsync(server, new PlistDict { ["Type"] = "com.apple.mobile.lockdown" }, Ct, binary: true);
        Assert.Equal("com.apple.mobile.lockdown", (await connection.ReceiveAsync(Ct)).GetString("Type"));
    }

    [Fact]
    public async Task Oversized_frame_is_rejected()
    {
        var (client, server) = DuplexStream.CreatePair();
        await using var connection = new PlistConnection(client);
        await server.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, Ct);
        await server.FlushAsync(Ct);
        await Assert.ThrowsAsync<FormatException>(() => connection.ReceiveAsync(Ct));
    }

    [Fact]
    public async Task Unpaired_GetValue_returns_the_public_subset()
    {
        var (_, _, connector) = Setup(paired: false);
        await using var lockdown = await connector.OpenLockdownAsync(Ct);
        var values = await lockdown.GetAllValuesAsync(null, Ct);

        Assert.Equal("iPhone17,3", values.GetString("ProductType"));
        Assert.Null(values.GetString("SerialNumber"));
    }

    [Fact]
    public async Task Session_without_pair_record_is_trust_required()
    {
        var (_, _, connector) = Setup(paired: false);
        var ex = await Assert.ThrowsAsync<DeviceException>(() => connector.OpenSessionAsync(Ct));
        Assert.Equal(DeviceErrorCodes.TrustRequired, ex.Code);
    }

    [Fact]
    public async Task Session_gives_all_values_and_domains()
    {
        var (_, phone, connector) = Setup();
        phone.Domains["com.apple.disk_usage"] = new PlistDict { ["TotalDiskCapacity"] = 256_000_000_000L };

        await using var lockdown = await connector.OpenSessionAsync(Ct);
        Assert.Equal("FAKE-SESSION", lockdown.SessionId);
        var values = await lockdown.GetAllValuesAsync(null, Ct);
        Assert.Equal("F2LXK000N70F", values.GetString("SerialNumber"));
        Assert.Equal(256_000_000_000L, await lockdown.GetValueAsync("com.apple.disk_usage", "TotalDiskCapacity", Ct));
        Assert.Null(await lockdown.GetValueAsync("com.apple.disk_usage", "NoSuchKey", Ct));
    }

    [Theory]
    [InlineData("PasswordProtected", DeviceErrorCodes.Locked)]
    [InlineData("InvalidHostID", DeviceErrorCodes.TrustRequired)]
    [InlineData("PairingDialogResponsePending", DeviceErrorCodes.TrustRequired)]
    [InlineData("UserDeniedPairing", DeviceErrorCodes.TrustRequired)]
    [InlineData("SomethingNew", DeviceErrorCodes.Protocol)]
    public async Task StartSession_errors_map_to_device_error_codes(string lockdownError, string expected)
    {
        var (_, phone, connector) = Setup();
        phone.StartSessionError = lockdownError;
        var ex = await Assert.ThrowsAsync<DeviceException>(() => connector.OpenSessionAsync(Ct));
        Assert.Equal(expected, ex.Code);
        Assert.Equal(lockdownError, ex.LockdownError());
    }

    [Fact]
    public async Task Wrong_host_id_is_trust_required()
    {
        var (_, phone, connector) = Setup();
        phone.ExpectedHostId = "SOMEONE-ELSE";
        var ex = await Assert.ThrowsAsync<DeviceException>(() => connector.OpenSessionAsync(Ct));
        Assert.Equal(DeviceErrorCodes.TrustRequired, ex.Code);
    }

    [Fact]
    public async Task StartService_connects_to_the_returned_port()
    {
        var (_, phone, connector) = Setup();
        phone.Media.AddFile("/DCIM/100APPLE/IMG_0001.JPG", [1, 2, 3]);

        await using var afc = await connector.StartAfcAsync(Ct);
        Assert.Equal(["100APPLE"], await afc.ReadDirectoryAsync("/DCIM", Ct));
        Assert.Contains("lockdown:StartService", phone.Log);
    }

    [Fact]
    public async Task Pair_record_falls_back_to_the_lockdown_folder()
    {
        var dir = Directory.CreateTempSubdirectory("devicedeck-lockdown-").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, Udid + ".plist"), TestPairing.StoredRecordXml(), Ct);
            var store = new PairRecordStore(new FakeUsbmux(), dir);

            using var record = await store.LoadAsync(Udid, Ct);
            Assert.NotNull(record);
            Assert.Equal(TestPairing.HostId, record.HostId);
            Assert.Equal(TestPairing.SystemBuid, record.SystemBuid);
            Assert.Null(await store.LoadAsync("00000000-0000000000000000", Ct));
            Assert.Equal(Udid, Assert.Single(store.ListFiles()).Udid);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Tls_upgrade_presents_the_pair_records_host_certificate()
    {
        // The "phone" side: an SslStream server with the device certificate, asking for a client certificate.
        var certs = TestPairing.Certificates;
        using var deviceCert = ServerCertificate(certs.DeviceCertificatePem);
        using var record = PairRecord.FromPlist(TestPairing.StoredRecord());

        var (client, server) = DuplexStream.CreatePair();
        string? seenClientThumbprint = null;
        var serverTask = Task.Run(async () =>
        {
            await using var ssl = new SslStream(server, false, (_, cert, _, _) =>
            {
                seenClientThumbprint = cert?.GetCertHashString();
                return true;
            });
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = deviceCert,
                ClientCertificateRequired = true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, Ct);
            var msg = await Wire.ReadPlistAsync(ssl, Ct);
            await Wire.WritePlistAsync(ssl, new PlistDict { ["Echo"] = msg!.GetString("Request")! }, Ct);
        });

        await using var connection = new PlistConnection(client);
        await connection.UpgradeToTlsAsync(record.ClientCertificate, Ct);
        var reply = await connection.RequestAsync(new PlistDict { ["Request"] = "GetValue" }, Ct);
        await serverTask;

        Assert.True(connection.IsTls);
        Assert.Equal("GetValue", reply.GetString("Echo"));
        using var expected = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(certs.HostCertificatePem));
        Assert.Equal(expected.Thumbprint, seenClientThumbprint);
    }

    [Fact]
    public void Pairing_certificates_chain_to_the_root()
    {
        var certs = TestPairing.Certificates;
        using var root = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(certs.RootCertificatePem));
        using var host = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(certs.HostCertificatePem));
        using var device = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(certs.DeviceCertificatePem));

        // Lockdown certificates have empty names, so Windows' chain builder cannot link them by name;
        // check each signature against the root key directly instead.
        using var rootKey = root.GetRSAPublicKey()!;
        Assert.True(SignedBy(host, rootKey));
        Assert.True(SignedBy(device, rootKey));
        Assert.True(SignedBy(root, rootKey));
        Assert.True(root.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.False(host.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Equal(TestPairing.DeviceKey.ExportSubjectPublicKeyInfo(), device.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", System.Text.Encoding.ASCII.GetString(certs.HostPrivateKeyPem));
    }

    private static bool SignedBy(X509Certificate2 cert, System.Security.Cryptography.RSA key)
    {
        // Certificate ::= SEQUENCE { tbsCertificate, signatureAlgorithm, signatureValue BIT STRING }
        var reader = new System.Formats.Asn1.AsnReader(cert.RawData, System.Formats.Asn1.AsnEncodingRules.DER).ReadSequence();
        var tbs = reader.ReadEncodedValue().ToArray();
        reader.ReadSequence();
        var signature = reader.ReadBitString(out _);
        return key.VerifyData(tbs, signature, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    }

    private static X509Certificate2 ServerCertificate(byte[] certPem)
    {
        using var withKey = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(certPem)).CopyWithPrivateKey(TestPairing.DeviceKey);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }
}
