namespace DeviceDeck.Core.Abstractions;

// Toolbox: crash reports. A device that has DeviceCapability.CrashReports also implements this.

public enum CrashReportKind
{
    /// <summary>An app or process crashed.</summary>
    Crash,
    /// <summary>An app stopped responding (iPhone hang report, Android ANR).</summary>
    Hang,
    /// <summary>The system ended processes to free memory (iPhone JetsamEvent).</summary>
    Memory,
    /// <summary>The whole phone restarted (kernel panic, Android system_server crash).</summary>
    Panic,
    /// <summary>A native crash dump (Android tombstone).</summary>
    Tombstone,
    /// <summary>Anything else the phone keeps there (resource reports, analytics).</summary>
    Diagnostic,
}

/// <param name="Id">Stable while the report exists; pass it back to read, export or delete it.</param>
/// <param name="FileName">A file name for saving it on the PC.</param>
/// <param name="Readable">False for an entry the phone lists but won't hand over (binary drop-box data).</param>
public sealed record CrashReport(string Id, string FileName, string Process, CrashReportKind Kind, DateTimeOffset? Time, long Size, bool Readable = true);

/// <param name="CanDelete">The phone lets this PC delete these reports (iPhone yes; Android without root no).</param>
/// <param name="Unreadable">Places that exist but need root on this phone: "tombstones", "anr".</param>
public sealed record CrashReportList(IReadOnlyList<CrashReport> Reports, bool CanDelete, IReadOnlyList<string> Unreadable);

public interface ICrashReportSource
{
    /// <summary>Newest first.</summary>
    Task<CrashReportList> ListCrashReportsAsync(CancellationToken ct);

    /// <summary>Copies one report's bytes into <paramref name="destination"/>; path_not_found when it is gone.</summary>
    Task ReadCrashReportAsync(string id, Stream destination, CancellationToken ct);

    /// <summary>Only when <see cref="CrashReportList.CanDelete"/>; the UI has the user confirm first.</summary>
    Task DeleteCrashReportsAsync(IReadOnlyList<string> ids, CancellationToken ct);
}
