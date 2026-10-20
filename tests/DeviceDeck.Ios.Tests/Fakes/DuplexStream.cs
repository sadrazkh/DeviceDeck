using System.IO.Pipelines;

namespace DeviceDeck.Ios.Tests.Fakes;

/// <summary>An in-memory socket: what one end writes, the other reads.</summary>
internal sealed class DuplexStream(Stream readFrom, Stream writeTo, Action? onDispose = null) : Stream
{
    public static (Stream Client, Stream Server) CreatePair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var client = new DuplexStream(toClient.Reader.AsStream(), toServer.Writer.AsStream());
        var server = new DuplexStream(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        return (client, server);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => readFrom.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => readFrom.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => readFrom.ReadAsync(buffer, offset, count, ct);

    public override void Write(byte[] buffer, int offset, int count) => writeTo.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => writeTo.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => writeTo.WriteAsync(buffer, offset, count, ct);

    public override void Flush() => writeTo.Flush();

    public override Task FlushAsync(CancellationToken ct) => writeTo.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            writeTo.Dispose();
            readFrom.Dispose();
            onDispose?.Invoke();
        }
        base.Dispose(disposing);
    }
}
