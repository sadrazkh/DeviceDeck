using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Toolbox;

/// <summary>
/// `logcat -v threadtime -v year` lines: "2026-10-20 16:44:01.123  1290  1312 I ActivityManager: Start proc …".
/// Without "-v year" (Android 6 and older) the date is "10-20".
/// </summary>
internal static partial class LogcatParser
{
    /// <summary>The last 300 lines, then everything new, until the connection closes.</summary>
    public const string Command = "logcat -v threadtime -v year -T 300";

    /// <summary>pid → process name; toybox ps on Android 8+, the old toolbox ps before.</summary>
    public const string ProcessListCommand = "ps -A -o PID,NAME 2>/dev/null || ps";

    [GeneratedRegex(@"^(?:(?<year>\d{4})-)?(?<month>\d\d)-(?<day>\d\d) (?<time>\d\d:\d\d:\d\d\.\d{3})\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEFAS]) (?<tag>.*?)\s*:(?: (?<msg>.*))?$")]
    private static partial Regex Line();

    /// <summary>Null for logcat's own "--------- beginning of main" lines and blank lines.</summary>
    public static DeviceLogEntry? Parse(string line, TimeSpan phoneOffset, DateTimeOffset now, IReadOnlyDictionary<int, string>? processes)
    {
        line = line.TrimEnd('\r');
        if (line.Length == 0 || line.StartsWith("--------- ", StringComparison.Ordinal))
        {
            return null;
        }

        var m = Line().Match(line);
        if (!m.Success)
        {
            return new DeviceLogEntry(null, DeviceLogLevel.Info, "", null, null, line);
        }

        var year = m.Groups["year"].Success ? m.Groups["year"].Value : now.ToOffset(phoneOffset).Year.ToString(CultureInfo.InvariantCulture);
        DateTimeOffset? time = null;
        if (DateTime.TryParseExact($"{year}-{m.Groups["month"].Value}-{m.Groups["day"].Value} {m.Groups["time"].Value}", "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = new DateTimeOffset(parsed, phoneOffset);
        }

        var pid = int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture);
        var process = processes is not null && processes.TryGetValue(pid, out var name) ? name : "";
        var tag = m.Groups["tag"].Value.Trim();
        return new DeviceLogEntry(time, LevelOf(m.Groups["level"].Value[0]), process, pid, tag.Length == 0 ? null : tag, m.Groups["msg"].Value);
    }

    public static DeviceLogLevel LevelOf(char level) => level switch
    {
        'V' or 'S' => DeviceLogLevel.Verbose,
        'D' => DeviceLogLevel.Debug,
        'I' => DeviceLogLevel.Info,
        'W' => DeviceLogLevel.Warning,
        'E' => DeviceLogLevel.Error,
        _ => DeviceLogLevel.Fault, // F (fatal), A (assert)
    };

    /// <summary>"  PID NAME" (toybox) or "USER PID PPID … NAME" (toolbox): pid → name.</summary>
    public static Dictionary<int, string> ParseProcesses(string ps)
    {
        var map = new Dictionary<int, string>();
        var lines = ps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return map;
        }

        var header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pidColumn = Array.FindIndex(header, h => h == "PID");
        if (pidColumn < 0)
        {
            return map;
        }

        foreach (var line in lines.Skip(1))
        {
            var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length > pidColumn + 1 && int.TryParse(columns[pidColumn], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                map[pid] = columns[^1];
            }
        }

        return map;
    }
}

/// <summary>A write-only stream that hands each complete UTF-8 line to a callback (for shell output that never ends).</summary>
internal sealed class LineSink(Action<string> onLine) : Stream
{
    private readonly MemoryStream _partial = new();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            var newline = buffer.IndexOf((byte)'\n');
            if (newline < 0)
            {
                _partial.Write(buffer);
                return;
            }

            _partial.Write(buffer[..newline]);
            onLine(Encoding.UTF8.GetString(_partial.GetBuffer(), 0, (int)_partial.Length));
            _partial.SetLength(0);
            buffer = buffer[(newline + 1)..];
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    /// <summary>Hands over a last line that had no newline.</summary>
    public override void Flush()
    {
        if (_partial.Length > 0)
        {
            onLine(Encoding.UTF8.GetString(_partial.GetBuffer(), 0, (int)_partial.Length));
            _partial.SetLength(0);
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
