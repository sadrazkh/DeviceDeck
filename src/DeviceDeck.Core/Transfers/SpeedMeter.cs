namespace DeviceDeck.Core.Transfers;

/// <summary>Bytes per second averaged over a sliding window (five seconds in the plan).</summary>
internal sealed class SpeedMeter(TimeSpan window)
{
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();

    public void Add(long timestampTicks, long totalBytes)
    {
        _samples.Enqueue((timestampTicks, totalBytes));
        var oldest = timestampTicks - window.Ticks;
        while (_samples.Count > 2 && _samples.Peek().Ticks < oldest)
        {
            _samples.Dequeue();
        }
    }

    public void Reset() => _samples.Clear();

    /// <summary>Null until half a second of samples exists.</summary>
    public long? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var first = _samples.Peek();
            var last = _samples.Last();
            var seconds = TimeSpan.FromTicks(last.Ticks - first.Ticks).TotalSeconds;
            if (seconds < 0.5)
            {
                return null;
            }

            return (long)Math.Max(0, (last.Bytes - first.Bytes) / seconds);
        }
    }
}
