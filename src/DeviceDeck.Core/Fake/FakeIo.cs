using System.Diagnostics;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

/// <summary>Paced copies for the fake phones, so the transfer drawer shows believable speeds and a pulled "cable" stops mid-file.</summary>
internal static class FakeIo
{
    private const int Chunk = 256 * 1024;

    /// <summary>Writes <paramref name="head"/> followed by zero filler until <paramref name="length"/> bytes are written.</summary>
    public static async Task ProduceAsync(FakeDevice device, ReadOnlyMemory<byte> head, long length, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[(int)Math.Min(Chunk, Math.Max(length, 1))];
        var clock = Stopwatch.StartNew();
        long done = 0;
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            device.EnsureConnected();

            var count = (int)Math.Min(buffer.Length, length - done);
            Array.Clear(buffer, 0, count);
            if (done < head.Length)
            {
                var fromHead = (int)Math.Min(count, head.Length - done);
                head.Span.Slice((int)done, fromHead).CopyTo(buffer);
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            done += count;
            progress?.Report(done);
            await PaceAsync(device, done, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads <paramref name="source"/> to the end (or <paramref name="length"/> bytes) and returns how many bytes arrived.</summary>
    public static async Task<long> ConsumeAsync(FakeDevice device, Stream source, long length, IProgress<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[Chunk];
        var clock = Stopwatch.StartNew();
        long done = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            device.EnsureConnected();

            var want = length > 0 ? (int)Math.Min(buffer.Length, length - done) : buffer.Length;
            if (want <= 0)
            {
                break;
            }

            var read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            done += read;
            progress?.Report(done);
            await PaceAsync(device, done, clock, ct).ConfigureAwait(false);
        }

        if (length > 0 && done != length)
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, "The copy ended early.", new IOException($"Expected {length} bytes, got {done}."));
        }

        return done;
    }

    public static async Task DelayAsync(FakeDevice device, TimeSpan delay, CancellationToken ct)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        device.EnsureConnected();
    }

    private static async Task PaceAsync(FakeDevice device, long done, Stopwatch clock, CancellationToken ct)
    {
        if (device.Options.BytesPerSecond is not { } rate || rate <= 0)
        {
            return;
        }

        var ahead = TimeSpan.FromSeconds((double)done / rate) - clock.Elapsed;
        if (ahead > TimeSpan.FromMilliseconds(2))
        {
            await Task.Delay(ahead, ct).ConfigureAwait(false);
        }
    }
}
