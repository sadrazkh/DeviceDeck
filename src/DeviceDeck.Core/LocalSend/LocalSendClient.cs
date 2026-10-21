using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.LocalSend;

/// <summary>How to reach one receiver: a host and port, and for the cable route a function that opens the tunnel.</summary>
/// <param name="Connect">Opens a raw stream to the receiver (usbmux); null for an ordinary TCP connection to Host.</param>
public sealed record LocalSendEndpoint(string Host, int Port, bool Https, Func<CancellationToken, ValueTask<Stream>>? Connect = null);

public sealed class LocalSendTimeouts
{
    public static LocalSendTimeouts Default { get; } = new();

    /// <summary>Connecting plus the TLS handshake. A suspended LocalSend accepts the connection and then says nothing.</summary>
    public TimeSpan Connect { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>/info, /register and /cancel.</summary>
    public TimeSpan Short { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How long the phone may sit on the Accept question.</summary>
    public TimeSpan Accept { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>No byte taken for this long in the middle of a file: LocalSend went to the background (iOS suspends it in seconds).</summary>
    public TimeSpan Stall { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>After the last byte, the receiver writes the file (and saves it to Photos) before it answers.</summary>
    public TimeSpan Finish { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// A sender's calls to one LocalSend receiver (protocol v2): /info, /register, /prepare-upload, /upload, /cancel.
/// HTTPS certificates are self-signed, so instead of a CA chain the certificate's SHA-256 must equal the pinned
/// fingerprint; without a pin (the first contact) any certificate is taken and its fingerprint is in <see cref="PeerFingerprint"/>.
/// Failures come out as <see cref="DeviceException"/> with a <see cref="LocalSendErrors"/> code.
/// </summary>
public sealed class LocalSendClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly LocalSendTimeouts _timeouts;
    private readonly string? _pinned;
    private volatile bool _certificateRejected;

    /// <param name="clientCertificate">
    /// The sender's own certificate (<see cref="LocalSendIdentity.Certificate"/>). LocalSend asks for one in the TLS handshake
    /// and drops the connection without it, right after the handshake and before any answer.
    /// </param>
    public LocalSendClient(LocalSendEndpoint endpoint, string? pinnedFingerprint, LocalSendTimeouts? timeouts = null,
        System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate = null)
    {
        Endpoint = endpoint;
        _pinned = endpoint.Https ? pinnedFingerprint : null;
        _timeouts = timeouts ?? LocalSendTimeouts.Default;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = _timeouts.Connect,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Validate(certificate),
                ClientCertificates = clientCertificate is null ? null : [clientCertificate],
                LocalCertificateSelectionCallback = clientCertificate is null ? null : (_, _, _, _, _) => clientCertificate,
            },
        };
        if (endpoint.Connect is { } connect)
        {
            handler.ConnectCallback = async (_, ct) => await connect(ct).ConfigureAwait(false);
        }

        var host = IPAddress.TryParse(endpoint.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{endpoint.Host}]" : endpoint.Host;
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"{(endpoint.Https ? "https" : "http")}://{host}:{endpoint.Port}{LocalSendProtocol.ApiBase}"),
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public LocalSendEndpoint Endpoint { get; }

    /// <summary>The fingerprint of the certificate the receiver showed last (HTTPS only).</summary>
    public string? PeerFingerprint { get; private set; }

    private bool Validate(System.Security.Cryptography.X509Certificates.X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        var fingerprint = LocalSendProtocol.Fingerprint(certificate);
        PeerFingerprint = fingerprint;
        if (_pinned is null || LocalSendProtocol.SameFingerprint(fingerprint, _pinned))
        {
            return true;
        }

        _certificateRejected = true;
        return false;
    }

    /// <summary>GET /info: the receiver's identity (the old discovery call, still answered by LocalSend).</summary>
    public Task<LocalSendInfo> GetInfoAsync(CancellationToken ct) =>
        CallAsync("info", ct, _timeouts.Short, async token =>
        {
            using var response = await _http.GetAsync("info", HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, Stage.Info, pinGiven: false, token).ConfigureAwait(false);
            return await ReadInfoAsync(response, token).ConfigureAwait(false);
        });

    /// <summary>POST /register: tells the receiver who we are, and it answers with who it is.</summary>
    public Task<LocalSendInfo> RegisterAsync(LocalSendInfo me, CancellationToken ct) =>
        CallAsync("register", ct, _timeouts.Short, async token =>
        {
            using var response = await _http.PostAsync("register", Json(me with { Announce = null }), token).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, Stage.Info, pinGiven: false, token).ConfigureAwait(false);
            return await ReadInfoAsync(response, token).ConfigureAwait(false);
        });

    /// <summary>
    /// POST /prepare-upload: asks to send these files; the phone shows Accept (unless Quick Save is on) and the call waits
    /// for the answer. Null when the receiver answers 204 (nothing to send). Throws pin_required / pin_wrong (401),
    /// rejected (403), busy (409, 429).
    /// </summary>
    public Task<LocalSendSession?> PrepareUploadAsync(LocalSendInfo me, IReadOnlyList<LocalSendFile> files, string? pin, CancellationToken ct) =>
        CallAsync("prepare-upload", ct, _timeouts.Accept, async token =>
        {
            var body = new LocalSendPrepareRequest(me with { Announce = null }, files.ToDictionary(f => f.Id, StringComparer.Ordinal));
            var uri = string.IsNullOrEmpty(pin) ? "prepare-upload" : "prepare-upload?pin=" + Uri.EscapeDataString(pin);
            using var response = await _http.PostAsync(uri, Json(body), token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                return null;
            }

            await ThrowIfFailedAsync(response, Stage.Prepare, !string.IsNullOrEmpty(pin), token).ConfigureAwait(false);
            var session = await ReadJsonAsync<LocalSendSession>(response, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(session?.SessionId) || session.Files is null)
            {
                throw new DeviceException(LocalSendErrors.Failed, "LocalSend answered without a session.");
            }

            return session;
        }, timeoutCode: LocalSendErrors.NotListening, timeoutMessage: "Nobody answered the request on the iPhone. Open LocalSend, keep it open and try again.");

    /// <summary>
    /// POST /upload: one file's bytes. Progress reports bytes sent so far. A receiver that stops taking data for
    /// <see cref="LocalSendTimeouts.Stall"/> ends the upload with connection_lost.
    /// </summary>
    public async Task UploadAsync(string sessionId, string fileId, string token, Stream content, long length, IProgress<long>? progress, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watched = new WatchedStream(content, progress);
        var uri = $"upload?sessionId={Uri.EscapeDataString(sessionId)}&fileId={Uri.EscapeDataString(fileId)}&token={Uri.EscapeDataString(token)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Content = new StreamContent(watched, 1 << 18);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var watchdog = new Timer(_ =>
        {
            var idle = watched.IdleFor;
            var allowed = watched.Position >= length ? _timeouts.Finish : _timeouts.Stall;
            if (idle > allowed)
            {
                try
                {
                    stall.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            await ThrowIfFailedAsync(response, Stage.Upload, pinGiven: false, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new DeviceException(LocalSendErrors.ConnectionLost, "LocalSend went to the background on the iPhone (it locked, or another app came to the front), so sending stopped. Open LocalSend again, keep it on screen, and try again.");
        }
        catch (Exception ex) when (Translate(ex, ct, Stage.Upload) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>POST /cancel: ends the session on the phone. Best effort; never throws.</summary>
    public async Task<bool> CancelAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeouts.Short);
            using var response = await _http.PostAsync("cancel?sessionId=" + Uri.EscapeDataString(sessionId), null, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private enum Stage { Info, Prepare, Upload }

    // A body with its length (no chunked encoding): the simplest thing for a receiver to read.
    private static ByteArrayContent Json<T>(T body)
    {
        var content = new ByteArrayContent(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body, LocalSendProtocol.Json));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private async Task<T> CallAsync<T>(string what, CancellationToken ct, TimeSpan timeout, Func<CancellationToken, Task<T>> call,
        string timeoutCode = LocalSendErrors.NotListening, string? timeoutMessage = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await call(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceException(timeoutCode, timeoutMessage ?? $"LocalSend didn't answer ({what}). Open LocalSend on the iPhone and keep it open.");
        }
        catch (Exception ex) when (Translate(ex, ct, what == "prepare-upload" ? Stage.Prepare : Stage.Info) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>Transport failures as LocalSend errors; null leaves the exception as it is (cancellation, our own errors).</summary>
    private Exception? Translate(Exception ex, CancellationToken ct, Stage stage)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // The tunnel's errors come wrapped in HttpRequestException: a gone phone stays a gone phone (the queue waits for it).
        if ((ex as DeviceException ?? Find<DeviceException>(ex)) is { } device)
        {
            if (device.Code == IDevicePortTunnel.PortClosedCode)
            {
                return new DeviceException(LocalSendErrors.NotListening, "LocalSend isn't open on the iPhone. Open it and keep it open.", device);
            }

            return ReferenceEquals(device, ex) ? null : device;
        }

        if (_certificateRejected && Find<AuthenticationException>(ex) is { } auth)
        {
            return new DeviceException(LocalSendErrors.Fingerprint, "A different LocalSend answered than the one found before. Look for the iPhone again.", auth);
        }

        if (ex is HttpRequestException or IOException or SocketException or TimeoutException or AuthenticationException or OperationCanceledException)
        {
            return stage == Stage.Upload
                ? new DeviceException(LocalSendErrors.ConnectionLost, "The connection to LocalSend dropped in the middle of a file.", ex)
                : new DeviceException(LocalSendErrors.NotListening, "LocalSend on the iPhone isn't answering. Open it and keep it open.", ex);
        }

        if (ex is System.Text.Json.JsonException)
        {
            return new DeviceException(LocalSendErrors.Failed, "LocalSend answered in a way DeviceDeck didn't expect.", ex);
        }

        return null;
    }

    private static T? Find<T>(Exception? ex) where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, Stage stage, bool pinGiven, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail;
        try
        {
            detail = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            if (detail.Length > 300)
            {
                detail = detail[..300];
            }
        }
        catch (Exception)
        {
            detail = "";
        }

        var inner = new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {detail}", null, response.StatusCode);
        throw (stage, (int)response.StatusCode) switch
        {
            (Stage.Prepare, 401) when pinGiven => new DeviceException(LocalSendErrors.PinWrong, "That PIN isn't the one LocalSend shows. Check it and try again.", inner),
            (Stage.Prepare, 401) => new DeviceException(LocalSendErrors.PinRequired, "LocalSend on the iPhone asks for a PIN.", inner),
            (Stage.Prepare, 403) => new DeviceException(LocalSendErrors.Rejected, "The iPhone declined the photos.", inner),
            (_, 409) => new DeviceException(LocalSendErrors.Busy, "LocalSend on the iPhone is busy with another transfer. Wait for it to finish, then try again.", inner),
            (_, 429) => new DeviceException(LocalSendErrors.Busy, "LocalSend asked to wait a moment before trying again.", inner),
            (Stage.Upload, 403) => new DeviceException(LocalSendErrors.SessionLost, "LocalSend no longer expects these files (cancelled on the iPhone?). Try again.", inner),
            (Stage.Upload, 422) => new DeviceException(LocalSendErrors.Checksum, "The file arrived damaged (checksum mismatch). Try again.", inner),
            _ => new DeviceException(LocalSendErrors.Failed, $"LocalSend answered {(int)response.StatusCode}{(detail.Length > 0 ? ": " + detail : ".")}", inner),
        };
    }

    private static async Task<LocalSendInfo> ReadInfoAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var info = await ReadJsonAsync<LocalSendInfo>(response, ct).ConfigureAwait(false);
        if (info is null || string.IsNullOrEmpty(info.Fingerprint))
        {
            throw new DeviceException(LocalSendErrors.Failed, "LocalSend answered without saying who it is.");
        }

        return info;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(LocalSendProtocol.Json, ct).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new DeviceException(LocalSendErrors.Failed, "LocalSend answered in a way DeviceDeck didn't expect.", ex);
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The upload's source: counts what the HTTP stack took, for progress and the stall watchdog.</summary>
    private sealed class WatchedStream(Stream inner, IProgress<long>? progress) : Stream
    {
        private long _position;
        private long _lastActivity = Environment.TickCount64;

        public TimeSpan IdleFor => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => Interlocked.Read(ref _position);
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Count(int read)
        {
            // Asked for more: the previous chunk went out, so the receiver is taking data.
            Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);
            if (read > 0)
            {
                progress?.Report(Interlocked.Add(ref _position, read));
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
