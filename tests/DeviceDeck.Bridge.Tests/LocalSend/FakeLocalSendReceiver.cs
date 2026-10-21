using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DeviceDeck.Core.LocalSend;

namespace DeviceDeck.Bridge.Tests.LocalSend;

/// <summary>
/// LocalSend on a phone, in process: an HTTPS (or HTTP) server on 127.0.0.1 with a self-signed certificate that answers
/// protocol v2 like the app does: one session at a time (409 to a second), a PIN (401), a decline (403), nothing to
/// send (204), the Accept question (<see cref="AcceptGate"/>), tokens checked per file (403), sha256 checked (422),
/// /cancel. It can also read slowly, drop the connection in the middle of a file, or say nothing at all (suspended).
/// </summary>
internal sealed class FakeLocalSendReceiver : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> SharedCertificate = new(() => CreateCertificate("LocalSend User"));

    private readonly TcpListener _listener;
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _life = new();
    private readonly Task _accepting;
    private readonly Lock _gate = new();
    private readonly ConcurrentBag<TcpClient> _clients = [];
    private Session? _session;

    public FakeLocalSendReceiver(bool https = true, X509Certificate2? certificate = null)
    {
        Https = https;
        _certificate = https ? certificate ?? SharedCertificate.Value : null;
        Fingerprint = _certificate is null ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : LocalSendProtocol.Fingerprint(_certificate);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptLoopAsync);
    }

    public static X509Certificate2 CreateCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // SChannel can't serve TLS with an ephemeral key: go through PKCS#12 so the key is a real one.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    public bool Https { get; }
    public int Port { get; }
    public string Fingerprint { get; }

    public string Alias { get; set; } = "Clever Mango";
    public string? DeviceModel { get; set; } = "iPhone";
    public string DeviceType { get; set; } = "mobile";

    // ---- how it behaves ----

    /// <summary>LocalSend asks for this PIN (401 without it or with a wrong one).</summary>
    public string? Pin { get; set; }

    /// <summary>The answer to /prepare-upload instead of a session: 403 declined, 409 busy, 429, 500, 204 nothing to send.</summary>
    public int? PrepareStatus { get; set; }

    /// <summary>/prepare-upload waits for this, as LocalSend waits for the Accept tap.</summary>
    public TaskCompletionSource? AcceptGate { get; set; }

    /// <summary>Reads uploads at this pace (null = as fast as possible).</summary>
    public long? BytesPerSecond { get; set; }

    /// <summary>File names whose next upload breaks off half way: the connection is dropped (once each).</summary>
    public ConcurrentDictionary<string, bool> DropOnce { get; } = new(StringComparer.Ordinal);

    /// <summary>File names whose next upload is answered 422, as if the bytes came out different (once each).</summary>
    public ConcurrentDictionary<string, bool> CorruptOnce { get; } = new(StringComparer.Ordinal);

    /// <summary>Accepts connections and never answers (iOS suspended the app).</summary>
    public bool Silent { get; set; }

    // ---- what happened ----

    public ConcurrentDictionary<string, byte[]> Received { get; } = new(StringComparer.Ordinal);
    public ConcurrentQueue<(string? Pin, IReadOnlyList<LocalSendFile> Files)> Prepares { get; } = new();
    public ConcurrentQueue<string> Cancels { get; } = new();
    public ConcurrentQueue<LocalSendInfo> Registered { get; } = new();
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>The bytes of a file received so far in the upload going on now.</summary>
    public long UploadingBytes => Interlocked.Read(ref _uploading);

    private long _uploading;

    public string? ActiveSessionId
    {
        get
        {
            lock (_gate)
            {
                return _session?.Id;
            }
        }
    }

    /// <summary>A raw connection to the server, as usbmux Connect gives one.</summary>
    public async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, Port, ct);
        return client.GetStream();
    }

    public LocalSendInfo Info() => new()
    {
        Alias = Alias,
        Version = "2.1",
        DeviceModel = DeviceModel,
        DeviceType = DeviceType,
        Fingerprint = Fingerprint,
        Port = LocalSendProtocol.DefaultPort,
        Protocol = Https ? "https" : "http",
        Download = false,
    };

    private sealed class Session(string id, Dictionary<string, (LocalSendFile File, string Token)> files)
    {
        public string Id { get; } = id;
        public Dictionary<string, (LocalSendFile File, string Token)> Files { get; } = files;
        public HashSet<string> Over { get; } = [];
    }

    private async Task AcceptLoopAsync()
    {
        while (!_life.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_life.Token);
            }
            catch (Exception)
            {
                return;
            }

            _clients.Add(client);
            _ = Task.Run(() => ServeAsync(client, _life.Token));
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            if (Silent)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return;
            }

            if (_certificate is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, ct);
                stream = ssl;
            }

            var reader = new HttpReader(stream);
            while (!ct.IsCancellationRequested)
            {
                var request = await reader.ReadHeadAsync(ct);
                if (request is null)
                {
                    return;
                }

                Requests.Enqueue($"{request.Method} {request.Path}");
                if (!await HandleAsync(request, reader, stream, ct))
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
        {
        }
    }

    /// <summary>Answers one request; false closes the connection.</summary>
    private async Task<bool> HandleAsync(HttpHead request, HttpReader reader, Stream stream, CancellationToken ct)
    {
        switch (request.Method, request.Path)
        {
            case ("GET", "/api/localsend/v2/info"):
                await reader.ReadBodyAsync(request.Length, ct);
                await RespondAsync(stream, 200, Info(), ct);
                return true;

            case ("POST", "/api/localsend/v2/register"):
                var registered = JsonSerializer.Deserialize<LocalSendInfo>(await reader.ReadBodyAsync(request.Length, ct), LocalSendProtocol.Json);
                if (registered is not null)
                {
                    Registered.Enqueue(registered);
                }

                await RespondAsync(stream, 200, Info(), ct);
                return true;

            case ("POST", "/api/localsend/v2/prepare-upload"):
                return await PrepareAsync(request, reader, stream, ct);

            case ("POST", "/api/localsend/v2/upload"):
                return await UploadAsync(request, reader, stream, ct);

            case ("POST", "/api/localsend/v2/cancel"):
                await reader.ReadBodyAsync(request.Length, ct);
                var id = request.Query.GetValueOrDefault("sessionId");
                lock (_gate)
                {
                    if (_session?.Id == id)
                    {
                        _session = null;
                    }
                }

                if (id is not null)
                {
                    Cancels.Enqueue(id);
                }

                await RespondAsync(stream, 200, null, ct);
                return true;

            default:
                await reader.ReadBodyAsync(request.Length, ct);
                await RespondAsync(stream, 404, null, ct);
                return true;
        }
    }

    private async Task<bool> PrepareAsync(HttpHead request, HttpReader reader, Stream stream, CancellationToken ct)
    {
        var body = JsonSerializer.Deserialize<LocalSendPrepareRequest>(await reader.ReadBodyAsync(request.Length, ct), LocalSendProtocol.Json);
        if (body is null || body.Files.Count == 0)
        {
            await RespondAsync(stream, 400, null, ct);
            return true;
        }

        var pin = request.Query.GetValueOrDefault("pin");
        Prepares.Enqueue((pin, body.Files.Values.ToList()));
        if (Pin is not null && pin != Pin)
        {
            await RespondAsync(stream, 401, null, ct);
            return true;
        }

        if (PrepareStatus is { } forced)
        {
            await RespondAsync(stream, forced, null, ct);
            return true;
        }

        bool busy;
        lock (_gate)
        {
            busy = _session is not null;
        }

        if (busy)
        {
            await RespondAsync(stream, 409, null, ct);
            return true;
        }

        if (AcceptGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        var session = new Session(Guid.NewGuid().ToString("N"), body.Files.ToDictionary(f => f.Key, f => (f.Value, Guid.NewGuid().ToString("N"))));
        lock (_gate)
        {
            busy = _session is not null;
            if (!busy)
            {
                _session = session;
            }
        }

        if (busy)
        {
            await RespondAsync(stream, 409, null, ct);
            return true;
        }

        await RespondAsync(stream, 200, new LocalSendSession(session.Id, session.Files.ToDictionary(f => f.Key, f => f.Value.Token)), ct);
        return true;
    }

    private async Task<bool> UploadAsync(HttpHead request, HttpReader reader, Stream stream, CancellationToken ct)
    {
        var sessionId = request.Query.GetValueOrDefault("sessionId");
        var fileId = request.Query.GetValueOrDefault("fileId");
        var token = request.Query.GetValueOrDefault("token");
        LocalSendFile file;
        Session session;
        lock (_gate)
        {
            if (_session is null || _session.Id != sessionId || fileId is null || !_session.Files.TryGetValue(fileId, out var entry) || entry.Token != token
                || _session.Over.Contains(fileId))
            {
                file = null!;
                session = null!;
            }
            else
            {
                file = entry.File;
                session = _session;
            }
        }

        if (file is null)
        {
            await reader.ReadBodyAsync(request.Length, ct);
            await RespondAsync(stream, 403, null, ct);
            return true;
        }

        using var content = new MemoryStream();
        Interlocked.Exchange(ref _uploading, 0);
        var drop = DropOnce.TryRemove(file.FileName, out _);
        var left = request.Length;
        var buffer = new byte[16 * 1024];
        while (left > 0)
        {
            if (drop && content.Length >= request.Length / 2)
            {
                // The connection breaks in the middle of the file; LocalSend marks the file failed.
                lock (_gate)
                {
                    FinishFile(session, fileId!);
                }

                return false;
            }

            var read = await reader.ReadSomeAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (read == 0)
            {
                lock (_gate)
                {
                    FinishFile(session, fileId!);
                }

                return false;
            }

            content.Write(buffer, 0, read);
            left -= read;
            Interlocked.Add(ref _uploading, read);
            if (BytesPerSecond is { } pace)
            {
                await Task.Delay(TimeSpan.FromSeconds(read / (double)pace), ct);
            }
        }

        var bytes = content.ToArray();
        var corrupt = CorruptOnce.TryRemove(file.FileName, out _)
            || (file.Sha256 is { } sha && !string.Equals(sha, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase));
        lock (_gate)
        {
            FinishFile(session, fileId!);
        }

        if (corrupt)
        {
            await RespondAsync(stream, 422, null, ct);
            return true;
        }

        Received[file.FileName] = bytes;
        await RespondAsync(stream, 200, null, ct);
        return true;
    }

    // Caller holds _gate. When every file has its answer the session is over.
    private void FinishFile(Session session, string fileId)
    {
        session.Over.Add(fileId);
        if (session.Over.Count == session.Files.Count && ReferenceEquals(_session, session))
        {
            _session = null;
        }
    }

    private static async Task RespondAsync(Stream stream, int status, object? body, CancellationToken ct)
    {
        var payload = body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, LocalSendProtocol.Json);
        var head = $"HTTP/1.1 {status} {(HttpStatusCode)status}\r\nContent-Length: {payload.Length}\r\n"
                   + (payload.Length > 0 ? "Content-Type: application/json; charset=utf-8\r\n" : "")
                   + "Connection: keep-alive\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, ct);
        }

        await stream.FlushAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _life.CancelAsync();
        _listener.Stop();
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        try
        {
            await _accepting;
        }
        catch (Exception)
        {
        }

        _life.Dispose();
    }

    internal sealed record HttpHead(string Method, string Path, Dictionary<string, string> Query, long Length);

    /// <summary>Just enough HTTP/1.1 for the protocol: a request line, headers, a body of Content-Length bytes.</summary>
    internal sealed class HttpReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public async Task<HttpHead?> ReadHeadAsync(CancellationToken ct)
        {
            var line = await ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line))
            {
                return null;
            }

            var parts = line.Split(' ');
            var target = parts[1];
            var cut = target.IndexOf('?');
            var path = cut < 0 ? target : target[..cut];
            var query = new Dictionary<string, string>(StringComparer.Ordinal);
            if (cut >= 0)
            {
                foreach (var pair in target[(cut + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    query[Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }

            long length = 0;
            while (await ReadLineAsync(ct) is { Length: > 0 } header)
            {
                var colon = header.IndexOf(':');
                var name = header[..colon].Trim();
                var value = header[(colon + 1)..].Trim();
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    length = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                }
                else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The fake receiver reads bodies with a Content-Length only.");
                }
            }

            return new HttpHead(parts[0], path, query, length);
        }

        public async Task<byte[]> ReadBodyAsync(long length, CancellationToken ct)
        {
            var body = new byte[length];
            var done = 0;
            while (done < length)
            {
                var read = await ReadSomeAsync(body.AsMemory(done), ct);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                done += read;
            }

            return body;
        }

        public async ValueTask<int> ReadSomeAsync(Memory<byte> into, CancellationToken ct)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, ct);
                if (_end == 0)
                {
                    return 0;
                }
            }

            var count = Math.Min(into.Length, _end - _start);
            _buffer.AsMemory(_start, count).CopyTo(into);
            _start += count;
            return count;
        }

        private async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await ReadSomeAsync(one, ct) == 0)
                {
                    return line.Length == 0 ? null : line.ToString();
                }

                if (one[0] == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append((char)one[0]);
            }
        }
    }
}
