using System.Diagnostics;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using DeviceDeck.Core.LocalSend;
using DeviceDeck.Ios.Device;

namespace IosProbe;

/// <summary>
/// "localsend-raw": the raw facts of LocalSend's port over the cable, read-only. Each test opens its own usbmux tunnel:
/// does anything arrive unasked, does plain HTTP get bytes back, does a TLS handshake finish (with and without SNI /
/// ALPN, TLS 1.2 and 1.3), and what GET /info answers inside TLS. Never calls prepare-upload or sends a file.
/// </summary>
internal static class LocalSendRaw
{
    private const string Info = "GET /api/localsend/v2/info HTTP/1.1\r\nHost: 127.0.0.1:53317\r\nConnection: close\r\n\r\n";

    public static async Task RunAsync(IosDevice device, int port, int waitMs, string? ip, CancellationToken ct)
    {
        Console.WriteLine(ip is null
            ? $"usbmux Connect to port {port} on {device.Summary.Name} (wait {waitMs} ms per read)"
            : $"TCP to {ip}:{port} over the network (wait {waitMs} ms per read)");
        async Task<Stream> OpenAsync()
        {
            if (ip is null)
                return await device.ConnectPortAsync(port, ct);
            var tcp = new System.Net.Sockets.TcpClient { NoDelay = true };
            await tcp.ConnectAsync(System.Net.IPAddress.Parse(ip), port, ct);
            return tcp.GetStream();
        }

        await TestAsync("A unasked bytes", async s => Console.WriteLine("    " + await ReadAsync(s, waitMs, ct)));

        await TestAsync("B plain HTTP GET /info", async s =>
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes(Info), ct);
            await s.FlushAsync(ct);
            Console.WriteLine("    " + await ReadAsync(s, waitMs, ct));
        });

        await TlsAsync("C TLS, no SNI, no ALPN", null, null, SslProtocols.None);
        await TlsAsync("D TLS 1.2 only, no SNI", null, null, SslProtocols.Tls12);
        await TlsAsync("E TLS, SNI localhost, ALPN http/1.1", "localhost", [SslApplicationProtocol.Http11], SslProtocols.None);
        await TlsAsync("F TLS, then GET / (any answer at all?)", null, null, SslProtocols.None,
            "GET / HTTP/1.1\r\nHost: 127.0.0.1:53317\r\nConnection: close\r\n\r\n");
        var register = System.Text.Json.JsonSerializer.Serialize(LocalSendIdentity.ForThisPc().Info(), LocalSendProtocol.Json);
        await TlsAsync("G TLS, then POST /register", null, null, SslProtocols.None,
            $"POST /api/localsend/v2/register HTTP/1.1\r\nHost: 127.0.0.1:53317\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(register)}\r\nConnection: close\r\n\r\n{register}");
        await TlsAsync("H TLS, wait 1 s, then GET /info", null, null, SslProtocols.None, Info, delayMs: 1000);
        using var mine = ClientCertificate();
        Console.WriteLine($"  (client certificate for I/J: fingerprint {LocalSendProtocol.Fingerprint(mine)})");
        await TlsAsync("I TLS with a client certificate, then GET /info", null, null, SslProtocols.None, Info, client: mine);
        await TlsAsync("J TLS with a client certificate, then POST /register", null, null, SslProtocols.None,
            $"POST /api/localsend/v2/register HTTP/1.1\r\nHost: 127.0.0.1:53317\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(register)}\r\nConnection: close\r\n\r\n{register}", client: mine);

        async Task TlsAsync(string name, string? sni, List<SslApplicationProtocol>? alpn, SslProtocols protocols, string request = Info, int delayMs = 0,
            System.Security.Cryptography.X509Certificates.X509Certificate2? client = null) =>
            await TestAsync(name, async s =>
            {
                using var ssl = new SslStream(s, leaveInnerStreamOpen: true, (_, _, _, _) => true);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(waitMs * 3);
                var clock = Stopwatch.StartNew();
                try
                {
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = sni ?? "",
                        ApplicationProtocols = alpn,
                        EnabledSslProtocols = protocols,
                        ClientCertificates = client is null ? null : [client],
                        LocalCertificateSelectionCallback = client is null ? null : (_, _, _, _, _) => client,
                    }, limit.Token);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    handshake FAILED after {clock.ElapsedMilliseconds} ms: {Describe(ex)}");
                    return;
                }

                var cert = ssl.RemoteCertificate;
                Console.WriteLine($"    handshake ok in {clock.ElapsedMilliseconds} ms: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}, ALPN '{ssl.NegotiatedApplicationProtocol}'");
                if (cert is not null)
                    Console.WriteLine($"    certificate {cert.Subject}, fingerprint {LocalSendProtocol.Fingerprint(cert)}");
                if (delayMs > 0)
                    Console.WriteLine($"    before the request: {await ReadAsync(ssl, delayMs, ct)}");
                await ssl.WriteAsync(Encoding.UTF8.GetBytes(request), ct);
                await ssl.FlushAsync(ct);
                Console.WriteLine($"    {request[..request.IndexOf(' ', 5)]}: " + await ReadAsync(ssl, waitMs * 2, ct));
            });

        async Task TestAsync(string name, Func<Stream, Task> body)
        {
            Console.WriteLine($"  {name}");
            var clock = Stopwatch.StartNew();
            Stream stream;
            try
            {
                stream = await OpenAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    connect FAILED after {clock.ElapsedMilliseconds} ms: {Describe(ex)}");
                return;
            }

            Console.WriteLine($"    connected in {clock.ElapsedMilliseconds} ms ({stream.GetType().Name})");
            try
            {
                await body(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    FAILED: {Describe(ex)}");
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 ClientCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=DeviceDeck probe", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12), null);
    }

    private static async Task<string> ReadAsync(Stream stream, int waitMs, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(waitMs);
        var clock = Stopwatch.StartNew();
        var all = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            while (all.Length < 4096)
            {
                var n = await stream.ReadAsync(buffer, limit.Token);
                if (n == 0)
                    return $"{all.Length} bytes then closed by the phone after {clock.ElapsedMilliseconds} ms{Show(all)}";
                all.Write(buffer, 0, n);
            }

            return $"{all.Length}+ bytes in {clock.ElapsedMilliseconds} ms{Show(all)}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return all.Length == 0 ? $"nothing in {waitMs} ms" : $"{all.Length} bytes, then silence{Show(all)}";
        }
        catch (Exception ex)
        {
            return $"{all.Length} bytes, then {Describe(ex)} after {clock.ElapsedMilliseconds} ms{Show(all)}";
        }
    }

    private static string Show(MemoryStream bytes)
    {
        if (bytes.Length == 0)
            return "";
        var text = Encoding.ASCII.GetString(bytes.ToArray(), 0, (int)Math.Min(bytes.Length, 300));
        var printable = new string(text.Select(c => c is >= ' ' and <= '~' ? c : c is '\r' or '\n' ? '|' : '.').ToArray());
        return $": \"{printable}\"";
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }
}
