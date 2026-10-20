using System.Globalization;
using System.Text.RegularExpressions;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.Toolbox;

/// <summary>
/// The phone's crash reports, the way Finder and iTunes fetch them: com.apple.crashreportmover moves new reports
/// into place and answers "ping"; com.apple.crashreportcopymobile then serves that folder over AFC.
/// </summary>
public static partial class IosCrashReports
{
    public const string MoverService = "com.apple.crashreportmover";
    public const string CopyService = "com.apple.crashreportcopymobile";

    /// <summary>How long to wait for the mover's "ping"; with thousands of reports it can take a while.</summary>
    public static readonly TimeSpan MoverTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Folders deeper than this are not walked (the tree is flat apart from Retired/, Panics/ and a few more).</summary>
    private const int MaxDepth = 4;

    /// <summary>Waits for the mover's "ping\0". Returns false when it didn't come in time (listing goes ahead anyway).</summary>
    public static async Task<bool> WaitForMoverAsync(Stream mover, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MoverTimeout);
        var buffer = new byte[16];
        var read = 0;
        try
        {
            while (read < 4)
            {
                var n = await mover.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }

        return read >= 4 && buffer.AsSpan(0, 4).SequenceEqual("ping"u8);
    }

    /// <summary>Every file in the crash-report folder (recursively), newest first.</summary>
    public static async Task<List<CrashReport>> ListAsync(AfcClient afc, CancellationToken ct)
    {
        var reports = new List<CrashReport>();
        await WalkAsync(afc, "/", 0, reports, ct).ConfigureAwait(false);
        for (var i = 0; i < reports.Count; i++)
        {
            // An .ips name alone doesn't say crash (WiFiLQMMetrics-….ips is a metrics log): its first line does.
            if (reports[i].Kind == CrashReportKind.Crash && reports[i].Id.Contains(".ips", StringComparison.OrdinalIgnoreCase))
            {
                reports[i] = await RefineAsync(afc, reports[i], ct).ConfigureAwait(false);
            }
        }

        return reports.OrderByDescending(r => r.Time ?? DateTimeOffset.MinValue).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Bytes read from the start of an .ips report for its JSON header line.</summary>
    private const int HeaderBytes = 1024;

    private static async Task<CrashReport> RefineAsync(AfcClient afc, CrashReport report, CancellationToken ct)
    {
        try
        {
            var handle = await afc.OpenAsync(PathOf(report.Id), AfcFileMode.ReadOnly, ct).ConfigureAwait(false);
            byte[] head;
            try
            {
                head = await afc.ReadAsync(handle, HeaderBytes, ct).ConfigureAwait(false);
            }
            finally
            {
                await afc.CloseAsync(handle, ct).ConfigureAwait(false);
            }

            return FromHeader(report, head);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound)
        {
            return report;
        }
    }

    /// <summary>
    /// The kind from an .ips report's first line, a JSON object with "bug_type": 309 / 109 crash, 298 jetsam (memory),
    /// 288 stackshot (hang), 210 panic, anything else a diagnostic (analytics, metrics, resource use).
    /// "app_name" or "name" names the process when there is one.
    /// </summary>
    public static CrashReport FromHeader(CrashReport report, ReadOnlySpan<byte> head)
    {
        var newline = head.IndexOf((byte)'\n');
        var line = newline >= 0 ? head[..newline] : head;
        string? bugType = null;
        string? process = null;
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(line, new System.Text.Json.JsonReaderOptions { AllowTrailingCommas = true });
            using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var root = doc.RootElement;
                bugType = root.TryGetProperty("bug_type", out var b) ? b.ToString() : null;
                process = root.TryGetProperty("app_name", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String ? a.GetString()
                    : root.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString()
                    : null;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return report; // a text report (older iOS): keep what the name said
        }

        var kind = bugType switch
        {
            null => report.Kind,
            "309" or "109" => CrashReportKind.Crash,
            "298" => CrashReportKind.Memory,
            "288" => CrashReportKind.Hang,
            "210" => CrashReportKind.Panic,
            _ => CrashReportKind.Diagnostic,
        };
        return report with { Kind = kind, Process = string.IsNullOrWhiteSpace(process) ? report.Process : process };
    }

    private static async Task WalkAsync(AfcClient afc, string folder, int depth, List<CrashReport> into, CancellationToken ct)
    {
        IReadOnlyList<string> names;
        try
        {
            names = await afc.ReadDirectoryAsync(folder, ct).ConfigureAwait(false);
        }
        catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.PathNotFound)
        {
            return;
        }

        foreach (var name in names)
        {
            var path = folder == "/" ? "/" + name : folder + "/" + name;
            var info = await afc.GetFileInfoAsync(path, ct).ConfigureAwait(false);
            if (info is null)
            {
                continue;
            }

            if (info.IsDirectory)
            {
                if (depth < MaxDepth)
                {
                    await WalkAsync(afc, path, depth + 1, into, ct).ConfigureAwait(false);
                }

                continue;
            }

            into.Add(Describe(path.TrimStart('/'), info.Size, info.Modified));
        }
    }

    // "Instagram-2026-10-19-201102.ips", "JetsamEvent-2026-10-19-180544.ips", "ExcUserFault_Photos-2026-10-16-111530.ips",
    // "stacks+com.apple.mobilemail-2026-10-01-100000.ips", "panic-full-2026-10-12-042201.0002.ips", "x.ips.ca.synced".
    [GeneratedRegex(@"^(?<name>.+?)[-_](?<date>\d{4}-\d{2}-\d{2}-\d{6})(?<ext>(?:\.[A-Za-z0-9]+)*)$")]
    private static partial Regex ReportName();

    /// <summary>Process, kind and time from the report's file name (its path relative to the folder).</summary>
    public static CrashReport Describe(string id, long size, DateTimeOffset? modified)
    {
        var fileName = id[(id.LastIndexOf('/') + 1)..];
        var m = ReportName().Match(fileName);
        var name = m.Success ? m.Groups["name"].Value : Path.GetFileNameWithoutExtension(fileName);
        DateTimeOffset? time = modified;
        if (m.Success && DateTime.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            time = new DateTimeOffset(parsed);
        }

        var (kind, process) = Classify(name, id);
        return new CrashReport(id, fileName, process, kind, time, size);
    }

    private static (CrashReportKind Kind, string Process) Classify(string name, string id)
    {
        if (name.StartsWith("JetsamEvent", StringComparison.OrdinalIgnoreCase))
        {
            return (CrashReportKind.Memory, "JetsamEvent");
        }

        if (name.StartsWith("panic", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Panics/", StringComparison.OrdinalIgnoreCase))
        {
            return (CrashReportKind.Panic, "kernel");
        }

        // "WhatsApp.diskwrites_resource", "Maps.cpu_resource": resource-use reports, not crashes.
        var resource = name.IndexOf("_resource", StringComparison.OrdinalIgnoreCase);
        if (resource > 0 && name.LastIndexOf('.', resource) is var dot and > 0)
        {
            return (CrashReportKind.Diagnostic, name[..dot]);
        }

        foreach (var prefix in new[] { "ExcResource_", "ExcUserFault_", "ExcResourceThreshold_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (CrashReportKind.Diagnostic, name[prefix.Length..]);
            }
        }

        if (name.StartsWith("stacks", StringComparison.OrdinalIgnoreCase) || name.Contains("hang", StringComparison.OrdinalIgnoreCase))
        {
            var plus = name.IndexOf('+');
            return (CrashReportKind.Hang, plus >= 0 ? name[(plus + 1)..] : name);
        }

        var extension = Path.GetExtension(id.EndsWith(".synced", StringComparison.OrdinalIgnoreCase) ? id[..^".synced".Length] : id);
        return extension.ToLowerInvariant() is ".ips" or ".crash"
            ? (CrashReportKind.Crash, name)
            : (CrashReportKind.Diagnostic, name);
    }

    /// <summary>A report id must stay inside the folder the copy service serves.</summary>
    public static string PathOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Contains("..", StringComparison.Ordinal) || id.Contains('\\'))
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That crash report is no longer on the iPhone.");
        }

        return "/" + id.TrimStart('/');
    }
}
