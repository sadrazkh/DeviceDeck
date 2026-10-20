using System.Buffers.Binary;
using System.Security.Cryptography.X509Certificates;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol;

/// <summary>
/// The framing lockdown and the plist services share: a 4-byte big-endian length, then a plist
/// (we send XML; we accept XML or binary). One request at a time.
/// </summary>
public sealed class PlistConnection : IAsyncDisposable
{
    /// <summary>Icons and IORegistry dumps are well under this; anything bigger is a framing error.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    private Stream _stream;
    private bool _detached;
    private bool _broken;

    public PlistConnection(Stream stream) => _stream = stream;

    public Stream Stream => _stream;

    public bool IsTls { get; private set; }

    /// <summary>True once a send or receive failed part-way; a later reply could belong to an earlier request.</summary>
    public bool IsBroken => _broken;

    public async Task SendAsync(PlistDict message, CancellationToken ct)
    {
        var body = PlistSerializer.ToXml(message);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _broken = true;
            throw;
        }
    }

    public async Task<PlistDict> ReceiveAsync(CancellationToken ct)
    {
        byte[] body;
        try
        {
            var header = new byte[4];
            await _stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length == 0 || length > MaxMessageBytes)
                throw new FormatException($"Plist message length {length} is out of range.");
            body = new byte[length];
            await _stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }
        catch
        {
            _broken = true;
            throw;
        }
        return PlistSerializer.ParseDict(body);
    }

    public async Task<PlistDict> RequestAsync(PlistDict message, CancellationToken ct)
    {
        await SendAsync(message, ct).ConfigureAwait(false);
        return await ReceiveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Switches this connection to TLS with the pair record's host certificate.</summary>
    public async Task UpgradeToTlsAsync(X509Certificate2 hostCertificate, CancellationToken ct)
    {
        if (IsTls)
            return;
        _stream = await IosTls.AuthenticateAsync(_stream, hostCertificate, ct).ConfigureAwait(false);
        IsTls = true;
    }

    /// <summary>Hands the raw stream over (house_arrest turns into AFC); this object no longer owns it.</summary>
    public Stream Detach()
    {
        _detached = true;
        return _stream;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_detached)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
