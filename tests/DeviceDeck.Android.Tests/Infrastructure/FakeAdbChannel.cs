using System.Text;
using System.Text.RegularExpressions;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Apps;
using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests.Infrastructure;

internal sealed record FakeReply(int ExitCode, byte[] Stdout, string Stderr)
{
    public static FakeReply Text(string stdout, int exitCode = 0, string stderr = "") => new(exitCode, Encoding.UTF8.GetBytes(stdout), stderr);
}

/// <summary>
/// An <see cref="IAdbChannel"/> that answers shell commands from rules (usually a fixture folder), keeps a fake
/// file store for sync push/pull and `dd` range reads, and records what was asked.
/// </summary>
internal sealed partial class FakeAdbChannel : IAdbChannel
{
    private readonly List<(Func<string, bool> Match, Func<string, FakeReply> Reply)> _rules = [];

    public string Serial { get; init; } = "FAKE0001";

    public List<string> Commands { get; } = [];

    public List<string> Unmatched { get; } = [];

    /// <summary>Phone paths and their bytes, for pull/push, dd and stat %s.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public List<(string Command, byte[] Input)> Execs { get; } = [];

    public Func<string, byte[], string> ExecHandler { get; set; } = (_, _) => "Success\n";

    public List<string> Reboots { get; } = [];

    public FakeAdbChannel On(string command, string stdout, int exitCode = 0, string stderr = "")
    {
        _rules.Insert(0, (c => c == command, _ => FakeReply.Text(stdout, exitCode, stderr)));
        return this;
    }

    public FakeAdbChannel On(Func<string, bool> match, Func<string, FakeReply> reply)
    {
        _rules.Insert(0, (match, reply));
        return this;
    }

    public FakeAdbChannel OnPrefix(string prefix, string stdout, int exitCode = 0, string stderr = "") =>
        On(c => c.StartsWith(prefix, StringComparison.Ordinal), _ => FakeReply.Text(stdout, exitCode, stderr));

    public static FakeAdbChannel FromFixture(FixtureSet set)
    {
        var fake = new FakeAdbChannel();
        fake.On("true", "");
        void Map(string command, string file)
        {
            if (set.TryRead(file) is { } text)
            {
                fake.On(command, text);
            }
        }
        Map("getprop", "getprop.txt");
        Map("settings get global device_name", "settings-device-name.txt");
        Map("date +%z", "date-z.txt");
        Map("df -k /data /storage/*", "df.txt");
        Map("ls -1 /storage", "ls-storage.txt");
        Map("sm list-volumes public", "sm-list-volumes.txt");
        Map("dumpsys diskstats", "dumpsys-diskstats.txt");
        Map("dumpsys battery", "dumpsys-battery.txt");
        Map(BatteryParser.SysfsCommand, "battery-sysfs.txt");
        Map(BatteryParser.StickyCycleCountCommand, "battery-sticky.txt");
        Map("service call iphonesubinfo 1 s16 com.android.shell", "iphonesubinfo.txt");
        Map(AndroidAppManager.ListCommand, "pm-list-packages.txt");
        Map("pm list packages -s", "pm-list-packages-system.txt");
        Map("pm list packages providers.media", "pm-list-media-provider.txt");
        Map("dumpsys package packages", "dumpsys-package.txt");
        Map(FileListingParser.StatListCommand("'/sdcard/'"), "stat-sdcard.txt");
        Map("ls -la '/sdcard/'", "ls-la-sdcard.txt");
        if (set.TryRead("details-script.txt") is { } details)
        {
            var sections = ShellScript.Split(details);
            fake.On("grep MemTotal /proc/meminfo", sections.GetValueOrDefault("meminfo") ?? "");
            fake.On("uname -r", sections.GetValueOrDefault("kernel") ?? "");
            fake.On("cat /sys/class/net/wlan0/address; ip link show wlan0", sections.GetValueOrDefault("mac") ?? "");
            fake.On("service call iphonesubinfo 1 s16 com.android.shell", sections.GetValueOrDefault("imei") ?? "");
        }
        if (set.TryRead("content-query-media.txt") is { } media)
        {
            fake.On(c => c.StartsWith("content query --uri content://media/external/file", StringComparison.Ordinal)
                         && c.EndsWith("--where 'media_type IN (1,3)'", StringComparison.Ordinal),
                _ => FakeReply.Text(media));
        }
        return fake;
    }

    [GeneratedRegex(@"echo '@@dd:(?<name>[^@]+)@@'; \{ (?<cmd>.*?); \} 2>/dev/null; ", RegexOptions.Singleline)]
    private static partial Regex ScriptPart();

    [GeneratedRegex(@"^dd if='(?<path>.*)' bs=(?<bs>\d+) skip=(?<skip>\d+) count=(?<count>\d+) 2>/dev/null$")]
    private static partial Regex Dd();

    [GeneratedRegex(@"^stat -L -c %s '(?<path>.*)'$")]
    private static partial Regex StatSize();

    public FakeReply Answer(string command)
    {
        if (command.StartsWith("echo '@@dd:", StringComparison.Ordinal))
        {
            // Emulate ShellScript.Build: each part's stdout after its marker, errors dropped.
            var output = new StringBuilder();
            foreach (Match part in ScriptPart().Matches(command))
            {
                output.Append("@@dd:").Append(part.Groups["name"].Value).Append("@@\n");
                var reply = Answer(part.Groups["cmd"].Value);
                output.Append(Encoding.UTF8.GetString(reply.Stdout));
            }
            return FakeReply.Text(output.ToString());
        }
        foreach (var (match, reply) in _rules)
        {
            if (match(command))
            {
                return reply(command);
            }
        }
        if (Dd().Match(command) is { Success: true } dd)
        {
            if (!Files.TryGetValue(dd.Groups["path"].Value, out byte[]? data))
            {
                return FakeReply.Text("", 1, "dd: No such file or directory");
            }
            long block = long.Parse(dd.Groups["bs"].Value);
            long start = Math.Min(data.Length, long.Parse(dd.Groups["skip"].Value) * block);
            long length = Math.Min(data.Length - start, long.Parse(dd.Groups["count"].Value) * block);
            return new FakeReply(0, data.AsSpan((int)start, (int)length).ToArray(), "");
        }
        if (StatSize().Match(command) is { Success: true } stat)
        {
            return Files.TryGetValue(stat.Groups["path"].Value, out byte[]? data)
                ? FakeReply.Text(data.Length + "\n")
                : FakeReply.Text("", 1, $"stat: '{stat.Groups["path"].Value}': No such file or directory");
        }
        Unmatched.Add(command);
        return FakeReply.Text("", 127, $"fake: no rule for: {command}");
    }

    public Task<ShellResult> ShellAsync(string command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Commands.Add(command);
        var reply = Answer(command);
        return Task.FromResult(new ShellResult(reply.ExitCode, Encoding.UTF8.GetString(reply.Stdout), reply.Stderr));
    }

    public async Task<ShellResult> ShellToStreamAsync(string command, Stream stdout, CancellationToken ct)
    {
        Commands.Add(command);
        var reply = Answer(command);
        await stdout.WriteAsync(reply.Stdout, ct);
        return new ShellResult(reply.ExitCode, "", reply.Stderr);
    }

    public async Task<string> ExecAsync(string command, Stream? input, IProgress<long>? progress, CancellationToken ct)
    {
        Commands.Add("exec:" + command);
        byte[] bytes = [];
        if (input != null)
        {
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
            progress?.Report(bytes.Length);
        }
        Execs.Add((command, bytes));
        return ExecHandler(command, bytes);
    }

    public Task<IAdbSyncSession> OpenSyncAsync(CancellationToken ct) => Task.FromResult<IAdbSyncSession>(new Sync(this));

    public Task RebootAsync(string target, CancellationToken ct)
    {
        Reboots.Add(target);
        return Task.CompletedTask;
    }

    private sealed class Sync(FakeAdbChannel owner) : IAdbSyncSession
    {
        public async Task PullAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            if (!owner.Files.TryGetValue(remotePath, out byte[]? data))
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"fake: {remotePath} not found");
            }
            await destination.WriteAsync(data, ct);
            progress?.Report(data.Length);
        }

        public async Task PushAsync(Stream source, string remotePath, DateTimeOffset modified, IProgress<long>? progress, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            owner.Files[remotePath] = buffer.ToArray();
            progress?.Report(buffer.Length);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
