using System.Runtime.CompilerServices;
using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Mirror;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Android.Toolbox;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

// Toolbox services of an Android phone: logcat, crash reports from the drop box, ringtones and scrcpy mirroring.
internal sealed partial class AndroidDevice : IDeviceLogSource, ICrashReportSource, IRingtoneTarget, IScreenMirrorSource
{
    public const string RingtonesFolder = "/sdcard/Ringtones";

    internal AndroidProviderOptions Options { get; }

    // ---- live log ----

    public async IAsyncEnumerable<DeviceLogEntry> StreamLogAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var profile = RequireProfile();
        var processes = new ProcessNames(Channel);
        await processes.RefreshAsync(ct).ConfigureAwait(false);

        var lines = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(20_000)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sink = new LineSink(line => lines.Writer.TryWrite(line));
        var shell = Task.Run(async () =>
        {
            try
            {
                var result = await Channel.ShellToStreamAsync(LogcatParser.Command, sink, stop.Token).ConfigureAwait(false);
                sink.Flush();
                lines.Writer.TryComplete(result.Succeeded
                    ? new DeviceException(DeviceErrorCodes.Disconnected, "The phone stopped sending its log.")
                    : AdbErrors.FromShell("logcat", result));
            }
            catch (OperationCanceledException)
            {
                lines.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                lines.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var entry = LogcatParser.Parse(line, profile.UtcOffset, DateTimeOffset.UtcNow, processes.Current);
                if (entry is null)
                {
                    continue;
                }

                if (entry.Process.Length == 0 && entry.Pid is { } pid)
                {
                    // A process started after the last ps: look again (at most every few seconds).
                    await processes.RefreshIfStaleAsync(ct).ConfigureAwait(false);
                    entry = entry with { Process = processes.Current.GetValueOrDefault(pid, "") };
                }

                yield return entry;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await shell.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>pid → name from ps, refreshed when an unknown pid shows up.</summary>
    private sealed class ProcessNames(IAdbChannel channel)
    {
        private static readonly TimeSpan MinAge = TimeSpan.FromSeconds(3);
        private DateTime _readAt = DateTime.MinValue;

        public IReadOnlyDictionary<int, string> Current { get; private set; } = new Dictionary<int, string>();

        public async Task RefreshAsync(CancellationToken ct)
        {
            _readAt = DateTime.UtcNow;
            var result = await channel.ShellAsync(LogcatParser.ProcessListCommand, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                Current = LogcatParser.ParseProcesses(result.Output);
            }
        }

        public Task RefreshIfStaleAsync(CancellationToken ct) =>
            DateTime.UtcNow - _readAt < MinAge ? Task.CompletedTask : RefreshAsync(ct);
    }

    // ---- crash reports ----

    public async Task<CrashReportList> ListCrashReportsAsync(CancellationToken ct)
    {
        var profile = RequireProfile();
        var output = await Channel.ShellAsync(AndroidCrashReports.ListScript(), ct).ConfigureAwait(false);
        return AndroidCrashReports.Parse(ShellScript.Split(output.Output), profile.UtcOffset);
    }

    public async Task ReadCrashReportAsync(string id, Stream destination, CancellationToken ct)
    {
        RequireProfile();
        var command = AndroidCrashReports.ReadCommand(id);
        if (id.StartsWith("dropbox/", StringComparison.Ordinal))
        {
            var result = await Channel.ShellAsync(command, ct).ConfigureAwait(false);
            var body = result.Succeeded ? AndroidCrashReports.DropBoxBody(result.Output) : null;
            if (body is null)
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That crash report is no longer on the phone.");
            }

            await destination.WriteAsync(Encoding.UTF8.GetBytes(body), ct).ConfigureAwait(false);
            return;
        }

        var file = await Channel.ShellToStreamAsync(command, destination, ct).ConfigureAwait(false);
        if (!file.Succeeded)
        {
            throw AdbErrors.FromShell("cat", file, id);
        }
    }

    public Task DeleteCrashReportsAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        Task.FromException(new DeviceException(DeviceErrorCodes.PermissionDenied,
            "Android keeps these reports where only the system can delete them."));

    // ---- ringtones ----

    public IReadOnlyCollection<string> RingtoneExtensions { get; } = [".mp3", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wav", ".flac", ".amr"];

    public async Task<RingtoneResult> AddRingtoneAsync(string fileName, Stream source, long length, bool setAsDefault, CancellationToken ct)
    {
        RequireProfile();
        var name = RingtoneInstaller.SafeFileName(fileName);
        if (!RingtoneExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "Android can't use that file type as a ringtone.");
        }

        var path = RingtonesFolder + "/" + name;
        await Channel.ShellAsync("mkdir -p " + ShellQuote.Quote(RingtonesFolder), ct).ConfigureAwait(false);
        await Channel.PushAsync(source, path, null, ct).ConfigureAwait(false);
        await new MediaScanner(Channel).ScanAsync([path], ct).ConfigureAwait(false);

        var id = await RingtoneInstaller.FindMediaIdAsync(Channel, MediaScanner.CanonicalPath(path), ct).ConfigureAwait(false);
        var isDefault = false;
        if (setAsDefault)
        {
            if (id is null)
            {
                throw new DeviceException(DeviceErrorCodes.Protocol, "The ringtone was copied, but Android hasn't listed it yet, so it couldn't be made the ringtone. Pick it in Settings → Sound.");
            }

            var set = await Channel.ShellAsync(RingtoneInstaller.SetDefaultCommand(id.Value), ct).ConfigureAwait(false);
            isDefault = set.Succeeded;
        }

        return new RingtoneResult(path, isDefault);
    }

    // ---- mirroring ----

    public async Task<IScreenMirrorSession> StartMirrorAsync(MirrorOptions options, CancellationToken ct)
    {
        RequireProfile();
        if (Channel is not IAdbSocketChannel sockets)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "Mirroring needs a direct adb connection.");
        }

        var jar = ScrcpyServerFile.Locate(Options.ScrcpyServerPath)
                  ?? throw new DeviceException(DeviceErrorCodes.DriverMissing,
                      "scrcpy-server is missing; run tools/fetch-scrcpy-server.ps1 (it ships in the release zip).");
        return await ScrcpySession.StartAsync(Channel, sockets, jar, options, ct).ConfigureAwait(false);
    }
}
