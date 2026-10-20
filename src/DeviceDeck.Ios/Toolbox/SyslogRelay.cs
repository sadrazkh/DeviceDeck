using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Ios.Toolbox;

/// <summary>
/// com.apple.syslog_relay: once started, the phone streams its system log as text, each message ending in "\n\0".
/// Bytes outside ASCII arrive vis(3)-encoded ("\M-b\M^@\M^Y" is UTF-8 E2 80 99).
/// </summary>
public static partial class SyslogRelay
{
    public const string ServiceName = "com.apple.syslog_relay";

    /// <summary>Splits a byte stream into messages; keeps a partial message between reads.</summary>
    public sealed class Splitter
    {
        private readonly MemoryStream _pending = new();

        /// <summary>Feeds <paramref name="chunk"/> and returns the messages it completed (raw bytes, still vis-encoded).</summary>
        public List<byte[]> Push(ReadOnlySpan<byte> chunk)
        {
            var messages = new List<byte[]>();
            foreach (var b in chunk)
            {
                if (b == 0)
                {
                    // "\n\0" ends a message; a stray NUL (the stream starts with one) ends nothing.
                    var length = (int)_pending.Length;
                    if (length > 0)
                    {
                        var buffer = _pending.GetBuffer();
                        var end = buffer[length - 1] == '\n' ? length - 1 : length;
                        messages.Add(buffer.AsSpan(0, end).ToArray());
                        _pending.SetLength(0);
                    }

                    continue;
                }

                _pending.WriteByte(b);
            }

            return messages;
        }
    }

    /// <summary>Undoes vis(3) escapes (\M-x, \M^x, \^x, \\) and decodes the result as UTF-8.</summary>
    public static string Decode(ReadOnlySpan<byte> raw)
    {
        if (raw.IndexOf((byte)'\\') < 0)
        {
            return Encoding.UTF8.GetString(raw);
        }

        var bytes = new byte[raw.Length];
        var n = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b == '\\' && i + 1 < raw.Length)
            {
                if (raw[i + 1] == '\\')
                {
                    bytes[n++] = (byte)'\\';
                    i++;
                    continue;
                }

                if (raw[i + 1] == 'M' && i + 3 < raw.Length && raw[i + 2] == '-')
                {
                    bytes[n++] = (byte)(raw[i + 3] | 0x80);
                    i += 3;
                    continue;
                }

                if (raw[i + 1] == 'M' && i + 3 < raw.Length && raw[i + 2] == '^')
                {
                    bytes[n++] = (byte)(Control(raw[i + 3]) | 0x80);
                    i += 3;
                    continue;
                }

                if (raw[i + 1] == '^' && i + 2 < raw.Length)
                {
                    bytes[n++] = Control(raw[i + 2]);
                    i += 2;
                    continue;
                }
            }

            bytes[n++] = b;
        }

        return Encoding.UTF8.GetString(bytes, 0, n);

        static byte Control(byte c) => c == '?' ? (byte)0x7F : (byte)(c ^ 0x40);
    }

    // "Oct 20 16:44:01 Sadras-iPhone SpringBoard(FrontBoard)[61] <Notice>: message"
    [GeneratedRegex(@"^(?<date>[A-Z][a-z]{2} [ \d]\d \d\d:\d\d:\d\d) (?<host>\S+) (?<proc>[^\[\]]+?)(?:\((?<sub>[^()]*)\))?\[(?<pid>\d+)\](?: \((?<cat>[^)]*)\))? <(?<level>[A-Za-z]+)>: ?(?<msg>.*)$", RegexOptions.Singleline)]
    private static partial Regex Line();

    /// <summary>One message as an entry; a line that doesn't look like syslog keeps its text with level Info.</summary>
    /// <param name="now">For the year (syslog leaves it out) and the time zone.</param>
    public static DeviceLogEntry Parse(string text, DateTimeOffset now)
    {
        var m = Line().Match(text);
        if (!m.Success)
        {
            return new DeviceLogEntry(null, DeviceLogLevel.Info, "", null, null, text.TrimEnd('\n'));
        }

        DateTimeOffset? time = null;
        var date = m.Groups["date"].Value.Replace("  ", " ", StringComparison.Ordinal);
        if (DateTime.TryParseExact($"{now.Year} {date}", "yyyy MMM d HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            var local = new DateTimeOffset(parsed, now.Offset);
            // Just after New Year a December line belongs to last year.
            time = local > now.AddDays(2) ? local.AddYears(-1) : local;
        }

        var sub = m.Groups["sub"].Success ? m.Groups["sub"].Value : m.Groups["cat"].Success ? m.Groups["cat"].Value : null;
        return new DeviceLogEntry(
            time,
            LevelOf(m.Groups["level"].Value),
            m.Groups["proc"].Value,
            int.TryParse(m.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ? pid : null,
            string.IsNullOrEmpty(sub) ? null : sub,
            m.Groups["msg"].Value.TrimEnd('\n'));
    }

    public static DeviceLogLevel LevelOf(string level) => level.ToLowerInvariant() switch
    {
        "debug" => DeviceLogLevel.Debug,
        "info" => DeviceLogLevel.Info,
        "notice" => DeviceLogLevel.Notice,
        "warning" or "warn" => DeviceLogLevel.Warning,
        "error" or "err" => DeviceLogLevel.Error,
        "critical" or "alert" or "emergency" or "fault" => DeviceLogLevel.Fault,
        _ => DeviceLogLevel.Info,
    };
}
