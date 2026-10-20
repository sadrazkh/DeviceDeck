using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;

namespace IosProbe;

/// <summary>The toolbox services from the command line: syslog for a few seconds, crash reports, the Wi-Fi sync switch.</summary>
internal static class ToolboxCommands
{
    /// <summary>
    /// Streams syslog_relay for <paramref name="seconds"/> and prints counts by level and process.
    /// Messages can hold personal data, so only <paramref name="show"/> lines are printed, cut to 100 characters.
    /// </summary>
    public static async Task SyslogAsync(IosDevice device, int seconds, int show, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(seconds));
        var entries = new List<DeviceLogEntry>();
        var started = DateTime.UtcNow;
        DateTime? first = null;
        await foreach (var entry in device.StreamLogAsync(window.Token))
        {
            first ??= DateTime.UtcNow;
            entries.Add(entry);
            if (entries.Count <= show)
            {
                var text = entry.Message.ReplaceLineEndings(" ");
                Console.WriteLine($"  {entry.Time:HH:mm:ss} {entry.Level,-7} {entry.Process}[{entry.Pid}]{(entry.Tag is null ? "" : $" ({entry.Tag})")}: {(text.Length > 100 ? text[..100] + "…" : text)}");
            }
        }

        var took = DateTime.UtcNow - started;
        Console.WriteLine($"{entries.Count} lines in {took.TotalSeconds:0.0} s; first after {((first ?? DateTime.UtcNow) - started).TotalMilliseconds:0} ms; {entries.Count(e => e.Time is null)} without a parsed time.");
        foreach (var group in entries.GroupBy(e => e.Level).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key,-8} {group.Count()}");
        Console.WriteLine("Busiest processes:");
        foreach (var group in entries.GroupBy(e => e.Process).OrderByDescending(g => g.Count()).Take(8))
            Console.WriteLine($"  {group.Count(),6}  {(group.Key.Length == 0 ? "(unparsed)" : group.Key)}");
    }

    public static async Task CrashesAsync(IosDevice device, int limit, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var list = await device.ListCrashReportsAsync(ct);
        Console.WriteLine($"{list.Reports.Count} crash reports ({list.Reports.Sum(r => r.Size) / 1024.0:0} KB) in {clock.Elapsed.TotalSeconds:0.0} s; deletable: {list.CanDelete}");
        foreach (var group in list.Reports.GroupBy(r => r.Kind).OrderBy(g => g.Key))
            Console.WriteLine($"  {group.Key,-11} {group.Count()}");
        Console.WriteLine($"Newest {Math.Min(limit, list.Reports.Count)}:");
        foreach (var r in list.Reports.Take(limit))
            Console.WriteLine($"  {r.Time:yyyy-MM-dd HH:mm}  {r.Kind,-10} {r.Size,9:N0}  {r.Process}  ({r.Id})");
    }

    public static async Task WifiSyncAsync(IosDevice device, string? set, bool confirmed, CancellationToken ct)
    {
        if (set is not null)
        {
            if (!confirmed)
                throw new CliException("Changing Wi-Fi sync changes the phone; add --yes to confirm.");
            await device.SetWifiSyncAsync(set is "on" or "true" or "1", ct);
        }

        var enabled = await device.GetWifiSyncAsync(ct);
        Console.WriteLine($"Wi-Fi sync (show this iPhone when on Wi-Fi): {(enabled is null ? "not reported" : enabled.Value ? "on" : "off")}; connected by {device.Summary.Connection}");
    }
}
