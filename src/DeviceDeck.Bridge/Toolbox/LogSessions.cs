using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Bridge.Toolbox;

/// <summary>Live log sessions: one per log.start, ended by log.stop, the page reloading, or the phone going away.</summary>
internal sealed class LogSessions
{
    private readonly ConcurrentDictionary<string, LogSession> _sessions = new(StringComparer.Ordinal);

    public LogSession Start(string deviceId, IDeviceLogSource source, Action<string, object> publish, ToolboxServices options, CancellationToken page)
    {
        var session = new LogSession(Guid.NewGuid().ToString("N")[..12], deviceId, options.LogCapacity, options.LogFlushInterval, publish);
        _sessions[session.Id] = session;
        // A session that ended on its own (unplugged) stays for export until log.stop; a reloaded page's sessions go.
        session.Start(source, page, () =>
        {
            if (page.IsCancellationRequested)
            {
                _sessions.TryRemove(session.Id, out _);
            }
        });
        return session;
    }

    public LogSession Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw new DeviceException(DeviceErrorCodes.PathNotFound, "That log is no longer running.");

    /// <summary>The session's lines stay readable (export) until it is stopped.</summary>
    public async Task StopAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public int Count => _sessions.Count;
}

internal sealed class LogSession(string id, string deviceId, int capacity, TimeSpan flushInterval, Action<string, object> publish) : IAsyncDisposable
{
    /// <summary>Most lines one log.lines event carries; a burst beyond <see cref="MaxPending"/> drops its oldest lines from the page (export keeps them).</summary>
    public const int MaxBatch = 1000;
    public const int MaxPending = 5000;

    private readonly object _gate = new();
    private readonly LogLine[] _ring = new LogLine[Math.Max(16, capacity)];
    private readonly List<LogLine> _pending = [];
    private readonly CancellationTokenSource _stop = new();
    private long _seq;
    private long _ringStart; // seq of the oldest line kept
    private int _dropped;
    private Task _pump = Task.CompletedTask;
    private Task _flusher = Task.CompletedTask;
    private int _ended;

    public string Id { get; } = id;

    public string DeviceId { get; } = deviceId;

    public void Start(IDeviceLogSource source, CancellationToken page, Action onEnded)
    {
        var registration = page.Register(() => _stop.Cancel());
        _flusher = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(flushInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Flush();
            }
        });
        _pump = Task.Run(async () =>
        {
            ToolboxError? error = null;
            try
            {
                await foreach (var entry in source.StreamLogAsync(_stop.Token).ConfigureAwait(false))
                {
                    Add(entry);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (DeviceException ex)
            {
                error = new ToolboxError(ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                error = new ToolboxError("internal", ex.Message);
            }
            finally
            {
                await registration.DisposeAsync().ConfigureAwait(false);
            }

            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await _flusher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            Flush();
            Ended = true;
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                onEnded();
                if (!page.IsCancellationRequested)
                {
                    publish("log.ended", new LogEndedEvent(Id, error));
                }
            }
        });
    }

    /// <summary>The stream is over (stopped, unplugged); the kept lines can still be exported.</summary>
    public bool Ended { get; private set; }

    private void Add(DeviceLogEntry entry)
    {
        lock (_gate)
        {
            var line = new LogLine(++_seq, entry.Time, entry.Level, entry.Process, entry.Pid, entry.Tag, entry.Message);
            _ring[(int)(line.Seq % _ring.Length)] = line;
            if (_seq - _ringStart >= _ring.Length)
            {
                _ringStart = _seq - _ring.Length + 1;
            }

            _pending.Add(line);
            if (_pending.Count > MaxPending)
            {
                var over = _pending.Count - MaxPending;
                _pending.RemoveRange(0, over);
                _dropped += over;
            }
        }
    }

    private readonly object _flushGate = new();

    /// <summary>Sends pending lines in order; one flush at a time, so batches never overtake each other.</summary>
    private void Flush()
    {
        lock (_flushGate)
        {
            FlushLocked();
        }
    }

    private void FlushLocked()
    {
        while (true)
        {
            LogLine[] batch;
            int dropped;
            lock (_gate)
            {
                if (_pending.Count == 0 && _dropped == 0)
                {
                    return;
                }

                var take = Math.Min(MaxBatch, _pending.Count);
                batch = _pending.GetRange(0, take).ToArray();
                _pending.RemoveRange(0, take);
                dropped = _dropped;
                _dropped = 0;
            }

            publish("log.lines", new LogLinesEvent(Id, batch, dropped));
        }
    }

    /// <summary>Lines kept for export, oldest first.</summary>
    public List<LogLine> Snapshot()
    {
        lock (_gate)
        {
            var lines = new List<LogLine>((int)Math.Min(_seq, _ring.Length));
            for (var seq = Math.Max(1, _ringStart); seq <= _seq; seq++)
            {
                lines.Add(_ring[(int)(seq % _ring.Length)]);
            }

            return lines;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _ringStart = _seq + 1;
            _pending.Clear();
            _dropped = 0;
        }
    }

    /// <summary>Writes the kept lines that pass the filter as text: "2026-10-20 16:44:01.123 I SpringBoard[61] FrontBoard: …".</summary>
    public async Task<int> ExportAsync(string path, DeviceLogLevel minLevel, string? search, CancellationToken ct)
    {
        var lines = Snapshot().Where(l => Matches(l, minLevel, search)).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line.Time?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-").Append(' ')
                .Append(LevelLetter(line.Level)).Append(' ')
                .Append(line.Process);
            if (line.Pid is { } pid)
            {
                text.Append('[').Append(pid.ToString(CultureInfo.InvariantCulture)).Append(']');
            }

            if (line.Tag is { Length: > 0 } tag)
            {
                text.Append(' ').Append(tag);
            }

            text.Append(": ").Append(line.Message.Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
        }

        await File.WriteAllTextAsync(path, text.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return lines.Count;
    }

    public static bool Matches(LogLine line, DeviceLogLevel minLevel, string? search) =>
        line.Level >= minLevel
        && (string.IsNullOrWhiteSpace(search)
            || line.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || line.Process.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (line.Tag?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));

    private static char LevelLetter(DeviceLogLevel level) => level switch
    {
        DeviceLogLevel.Verbose => 'V',
        DeviceLogLevel.Debug => 'D',
        DeviceLogLevel.Info => 'I',
        DeviceLogLevel.Notice => 'N',
        DeviceLogLevel.Warning => 'W',
        DeviceLogLevel.Error => 'E',
        _ => 'F',
    };

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _ended, 1); // stopped by the page: no log.ended
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
