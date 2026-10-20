using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DeviceDeck.Ios.Protocol;

/// <summary>TLS for lockdown sessions and services, on SslStream (SChannel on Windows).</summary>
public static class IosTls
{
    /// <summary>Set to "1.2" to force TLS 1.2 if a phone's handshake fails with the default (1.2 or 1.3).</summary>
    public const string ProtocolEnvironmentVariable = "DEVICEDECK_IOS_TLS";

    /// <summary>
    /// Builds the client certificate from the pair record's PEM host certificate and key.
    /// SChannel cannot use the ephemeral key CreateFromPem makes, so it goes through PKCS#12;
    /// the key lives in the user's key store until the certificate is disposed.
    /// </summary>
    public static X509Certificate2 CreateClientCertificate(byte[] certificatePem, byte[] privateKeyPem)
    {
        var ephemeral = X509Certificate2.CreateFromPem(
            Encoding.ASCII.GetString(certificatePem).AsSpan(),
            Encoding.ASCII.GetString(privateKeyPem).AsSpan());
        if (!OperatingSystem.IsWindows())
            return ephemeral;
        using (ephemeral)
        {
            var pfx = ephemeral.Export(X509ContentType.Pkcs12);
            return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.UserKeySet);
        }
    }

    public static async Task<SslStream> AuthenticateAsync(Stream inner, X509Certificate2 clientCertificate, CancellationToken ct)
    {
        // The phone's certificate is the device certificate from our own pairing, reached over the
        // local usbmux socket; there is no public PKI to check it against, so it is accepted as is.
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = string.Empty,
            ClientCertificates = [clientCertificate],
            LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            EnabledSslProtocols = Environment.GetEnvironmentVariable(ProtocolEnvironmentVariable) == "1.2"
                ? SslProtocols.Tls12
                : SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        };
        try
        {
            await ssl.AuthenticateAsClientAsync(options, ct).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
