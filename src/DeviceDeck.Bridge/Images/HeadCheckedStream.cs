namespace DeviceDeck.Bridge.Images;

/// <summary>
/// A memory stream that refuses to grow past a cap, and stops the writer after the first bytes when they are
/// not the kind of file wanted (a video when a picture was expected), so nothing large crosses the USB pipe for nothing.
/// </summary>
internal sealed class HeadCheckedStream(long maxBytes, Func<ReadOnlySpan<byte>, bool> acceptHead) : MemoryStream
{
    private const int HeadLength = 16;
    private bool _headChecked;

    public sealed class RejectedException() : IOException("The original is not a picture or is too large.");

    // Every write funnels into this overload: MemoryStream's span overload calls back into it when subclassed.
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(count);
        base.Write(buffer, offset, count);
        CheckHead();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var copy = buffer.ToArray();
        Write(copy, 0, copy.Length);
    }

    public override void WriteByte(byte value)
    {
        Check(1);
        base.WriteByte(value);
        CheckHead();
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    private void Check(int count)
    {
        if (Length + count > maxBytes)
        {
            throw new RejectedException();
        }
    }

    private void CheckHead()
    {
        if (_headChecked || Length < HeadLength)
        {
            return;
        }

        _headChecked = true;
        if (!acceptHead(GetBuffer().AsSpan(0, HeadLength)))
        {
            throw new RejectedException();
        }
    }
}
