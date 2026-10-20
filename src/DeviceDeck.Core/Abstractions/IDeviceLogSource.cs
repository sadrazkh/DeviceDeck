namespace DeviceDeck.Core.Abstractions;

// Toolbox: the live log. A device that has DeviceCapability.Syslog also implements this.

/// <summary>Ordered from least to most severe, so a filter is "this level and up".</summary>
public enum DeviceLogLevel { Verbose, Debug, Info, Notice, Warning, Error, Fault }

/// <param name="Time">When the phone logged it, in the phone's local time; null when the line had no time.</param>
/// <param name="Process">The process name (iPhone) or the process name / pid placeholder (Android).</param>
/// <param name="Tag">Android's log tag; the iPhone's sub-system in parentheses, when there is one.</param>
public sealed record DeviceLogEntry(DateTimeOffset? Time, DeviceLogLevel Level, string Process, int? Pid, string? Tag, string Message);

public interface IDeviceLogSource
{
    /// <summary>
    /// Streams the phone's live log (iPhone syslog_relay, Android logcat) until <paramref name="ct"/> is cancelled.
    /// Ends with a <see cref="DeviceException"/> (device_disconnected) when the phone goes away.
    /// </summary>
    IAsyncEnumerable<DeviceLogEntry> StreamLogAsync(CancellationToken ct);
}
