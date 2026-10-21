using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace PhotoBench;

/// <summary>
/// A USB link for the fake iPhone: every connection shares one wire of <see cref="BytesPerSecond"/>, and whatever is
/// written arrives <see cref="OneWay"/> later, without holding up the writer (so requests sent back to back overlap,
/// as on the real link). Each connection is a pair of in-memory pipes with a delay line in each direction.
/// </summary>
internal sealed class SimulatedLink(double bytesPerSecond, TimeSpan oneWay)
{
    private readonly Lock _wire = new();
    private long _wireFree;

    public double BytesPerSecond { get; } = bytesPerSecond;

    public TimeSpan OneWay { get; } = oneWay;

    public (Stream Client, Stream Server) CreatePair()
    {
        var toServer = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var toClient = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var client = new LinkEnd(toClient.Reader.AsStream(), new DelayLine(this, toServer.Writer.AsStream()));
        var server = new LinkEnd(toServer.Reader.AsStream(), new DelayLine(this, toClient.Writer.AsStream()));
        return (client, server);
    }

    /// <summary>When <paramref name="bytes"/> written now arrive at the other end (Stopwatch ticks).</summary>
    private long Schedule(int bytes)
    {
        var now = Stopwatch.GetTimestamp();
        var transmit = (long)(bytes / BytesPerSecond * Stopwatch.Frequency);
        lock (_wire)
        {
            _wireFree = Math.Max(_wireFree, now) + transmit;
            return _wireFree + (long)(OneWay.TotalSeconds * Stopwatch.Frequency);
        }
    }

    /// <summary>Waits until a Stopwatch timestamp: sleeps for the bulk, spins the last stretch (timers tick in 1–15 ms).</summary>
    private static async Task WaitUntilAsync(long due)
    {
        while (true)
        {
            var left = (due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (left <= 0)
            {
                return;
            }

            if (left > 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(left - 1.5)).ConfigureAwait(false);
            }
            else
            {
                Thread.Yield();
            }
        }
    }

    /// <summary>Asks Windows for 1 ms timers in this process while the fake runs; undone on dispose.</summary>
    public static IDisposable FineTimers() => OperatingSystem.IsWindows() ? new TimerResolution() : new Nothing();

    private sealed class TimerResolution : IDisposable
    {
        public TimerResolution() => _ = timeBeginPeriod(1);

        public void Dispose() => _ = timeEndPeriod(1);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>Writes go into a queue and reach the pipe at their due time, in order.</summary>
    private sealed class DelayLine
    {
        private readonly SimulatedLink _link;
        private readonly Stream _target;
        private readonly Channel<(long Due, byte[] Data)> _queue = Channel.CreateUnbounded<(long, byte[])>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _pump;

        public DelayLine(SimulatedLink link, Stream target)
        {
            _link = link;
            _target = target;
            _pump = Task.Run(PumpAsync);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            _queue.Writer.TryWrite((_link.Schedule(data.Length), data.ToArray()));
        }

        private async Task PumpAsync()
        {
            try
            {
                await foreach (var (due, data) in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await WaitUntilAsync(due).ConfigureAwait(false);
                    await _target.WriteAsync(data).ConfigureAwait(false);
                    await _target.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The reader went away.
            }
            finally
            {
                await _target.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void Complete() => _queue.Writer.TryComplete();

        public Task Drained => _pump;
    }

    private sealed class LinkEnd(Stream readFrom, DelayLine writeTo) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => readFrom.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => readFrom.ReadAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => readFrom.ReadAsync(buffer, offset, count, ct);

        public override void Write(byte[] buffer, int offset, int count) => writeTo.Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            writeTo.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            writeTo.Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                writeTo.Complete();
                readFrom.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
