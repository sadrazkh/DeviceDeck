using System.Runtime.CompilerServices;
using System.Text;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

// The toolbox side of a simulated phone: a live log that never ends, a handful of crash reports, ringtones in the
// Ringtones folder and the iPhone's Wi-Fi sync switch. Mirroring needs a real phone (there is no fake video).
public sealed partial class FakeDevice : IDeviceLogSource, ICrashReportSource, IRingtoneTarget, IWifiSyncSettings, IScreenMirrorSource
{
    private List<(CrashReport Report, string Text)>? _crashes;
    private bool _wifiSync;

    private bool IsIphone => Summary.Platform == DevicePlatform.Ios;

    // ---- live log ----

    public async IAsyncEnumerable<DeviceLogEntry> StreamLogAsync([EnumeratorCancellation] CancellationToken ct)
    {
        EnsureConnected();
        Require(DeviceCapability.Syslog, "show its log");
        var random = new Random(Summary.Id.GetHashCode(StringComparison.Ordinal));
        var lines = IsIphone ? IphoneLog : AndroidLog;
        while (true)
        {
            try
            {
                await Task.Delay(random.Next(40, 160), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            EnsureConnected();
            var count = random.Next(1, 4);
            for (var i = 0; i < count; i++)
            {
                var (level, process, pid, tag, message) = lines[random.Next(lines.Length)];
                yield return new DeviceLogEntry(DateTimeOffset.Now, level, process, pid, tag,
                    message.Replace("{n}", random.Next(1, 999).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
        }
    }

    private static readonly (DeviceLogLevel, string, int?, string?, string)[] IphoneLog =
    [
        (DeviceLogLevel.Notice, "SpringBoard", 61, "FrontBoard", "Scene update for com.apple.mobileslideshow: foreground"),
        (DeviceLogLevel.Info, "backboardd", 72, null, "Display brightness changed to 0.{n}"),
        (DeviceLogLevel.Notice, "kernel", 0, null, "AppleBCMWLAN: link quality {n}"),
        (DeviceLogLevel.Debug, "locationd", 96, "CoreLocation", "Location update, accuracy {n} m"),
        (DeviceLogLevel.Error, "mDNSResponder", 118, null, "Query timed out for _apple-mobdev2._tcp.local"),
        (DeviceLogLevel.Warning, "Photos", 1402, "PhotoLibraryServices", "Thumbnail cache miss for asset {n}"),
        (DeviceLogLevel.Notice, "lockdownd", 54, null, "Service com.apple.syslog_relay started for DeviceDeck"),
        (DeviceLogLevel.Fault, "MobileMail", 2210, "Message", "Background fetch took {n} ms"),
        (DeviceLogLevel.Info, "wifid", 88, null, "Associated with network, RSSI -{n}"),
    ];

    private static readonly (DeviceLogLevel, string, int?, string?, string)[] AndroidLog =
    [
        (DeviceLogLevel.Info, "system_server", 1290, "ActivityManager", "Start proc {n}:com.sec.android.gallery3d/u0a201 for activity"),
        (DeviceLogLevel.Debug, "system_server", 1290, "WindowManager", "Focus moved to Window{{n} u0 com.android.launcher}"),
        (DeviceLogLevel.Verbose, "com.android.systemui", 2104, "StatusBar", "Battery level {n}"),
        (DeviceLogLevel.Warning, "com.google.android.gms", 3388, "GmsClient", "Connection timed out after {n} ms"),
        (DeviceLogLevel.Error, "com.example.notes", 5120, "AndroidRuntime", "FATAL EXCEPTION: main (sample)"),
        (DeviceLogLevel.Info, "adbd", 801, "adbd", "host-{n}: connected"),
        (DeviceLogLevel.Debug, "surfaceflinger", 640, "SurfaceFlinger", "Frame {n} presented"),
        (DeviceLogLevel.Fault, "init", 1, "init", "Service 'vendor.sample' exited with status {n}"),
    ];

    // ---- crash reports ----

    private List<(CrashReport Report, string Text)> Crashes()
    {
        lock (Gate)
        {
            return _crashes ??= SampleCrashes(IsIphone);
        }
    }

    public Task<CrashReportList> ListCrashReportsAsync(CancellationToken ct)
    {
        EnsureConnected();
        Require(DeviceCapability.CrashReports, "show crash reports");
        IReadOnlyList<CrashReport> reports;
        lock (Gate)
        {
            reports = Crashes().Select(c => c.Report).OrderByDescending(r => r.Time).ToArray();
        }

        return Task.FromResult(new CrashReportList(reports, CanDelete: IsIphone, IsIphone ? [] : ["tombstones", "anr"]));
    }

    public async Task ReadCrashReportAsync(string id, Stream destination, CancellationToken ct)
    {
        EnsureConnected();
        string? text;
        lock (Gate)
        {
            text = Crashes().FirstOrDefault(c => c.Report.Id == id).Text;
        }

        if (text is null)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That crash report is no longer on the phone.");
        }

        await destination.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
    }

    public Task DeleteCrashReportsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        EnsureConnected();
        if (!IsIphone)
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Android keeps these reports where only the system can delete them.");
        }

        lock (Gate)
        {
            Crashes().RemoveAll(c => ids.Contains(c.Report.Id));
        }

        return Task.CompletedTask;
    }

    private static List<(CrashReport, string)> SampleCrashes(bool iphone)
    {
        var now = new DateTimeOffset(2026, 10, 19, 21, 14, 0, TimeSpan.FromHours(3.5));
        (CrashReport, string) Report(string id, string process, CrashReportKind kind, double hoursAgo, string body)
        {
            var text = $"Process: {process}\nDate/Time: {now.AddHours(-hoursAgo):yyyy-MM-dd HH:mm:ss zzz}\n\n{body}\n";
            return (new CrashReport(id, id.Replace('/', '_'), process, kind, now.AddHours(-hoursAgo), Encoding.UTF8.GetByteCount(text)), text);
        }

        return iphone
            ?
            [
                Report("Instagram-2026-10-19-201102.ips", "Instagram", CrashReportKind.Crash, 1.1,
                    "Exception Type: EXC_BAD_ACCESS (SIGSEGV)\nTriggered by Thread: 0\n\nThread 0 Crashed:\n0   libobjc.A.dylib  objc_msgSend + 32\n1   Instagram        0x104a2c000 + 1188772"),
                Report("JetsamEvent-2026-10-19-180544.ips", "JetsamEvent", CrashReportKind.Memory, 3.2,
                    "{\"bug_type\":\"298\",\"largestProcess\":\"Safari\",\"memoryStatus\":{\"pageSize\":16384}}"),
                Report("Retired/Telegram-2026-10-17-093012.ips", "Telegram", CrashReportKind.Hang, 59.7,
                    "Exception Type: EXC_CRASH (SIGKILL)\nTermination Reason: FRONTBOARD 2343432205 <RBSTerminateContext| scene-update watchdog>"),
                Report("ExcResource_Maps-2026-10-16-111530.ips", "Maps", CrashReportKind.Diagnostic, 82,
                    "Event: cpu usage\nAction taken: none\nCPU: 90 seconds cpu time over 104 seconds"),
                Report("panic-full-2026-10-12-042201.ips", "kernel", CrashReportKind.Panic, 185,
                    "panic(cpu 4 caller 0xfffffe0027a1c2e0): watchdog timeout: no checkins from watchdogd in 93 seconds"),
            ]
            :
            [
                Report("dropbox/data_app_crash/1760896440000", "com.example.notes", CrashReportKind.Crash, 0.6,
                    "Process: com.example.notes\nFlags: 0x38c8be46\nPackage: com.example.notes v42 (4.2)\n\njava.lang.NullPointerException: sample\n\tat com.example.notes.MainActivity.onCreate(MainActivity.kt:57)"),
                Report("dropbox/data_app_anr/1760820000000", "com.whatsapp", CrashReportKind.Hang, 21.8,
                    "Process: com.whatsapp\nSubject: Input dispatching timed out (5001ms)\n\n----- pid 7781 at 2026-10-19 00:13:20 -----"),
                Report("dropbox/SYSTEM_TOMBSTONE/1760700000000", "surfaceflinger", CrashReportKind.Tombstone, 55.1,
                    "*** *** *** *** *** *** *** *** *** *** *** *** *** *** *** ***\nBuild fingerprint: 'samsung/sample/16'\nsignal 6 (SIGABRT), code -1 (SI_QUEUE)"),
                Report("dropbox/system_server_crash/1760500000000", "system_server", CrashReportKind.Panic, 110.6,
                    "Process: system_server\njava.lang.IllegalStateException: sample watchdog"),
            ];
    }

    // ---- ringtones ----

    public IReadOnlyCollection<string> RingtoneExtensions => IsIphone ? [] : [".mp3", ".m4a", ".aac", ".ogg", ".wav", ".flac"];

    public async Task<RingtoneResult> AddRingtoneAsync(string fileName, Stream source, long length, bool setAsDefault, CancellationToken ct)
    {
        EnsureConnected();
        if (IsIphone)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "An iPhone takes ringtones only through a Finder or iTunes sync.");
        }

        await source.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
        var root = Roots.Values.FirstOrDefault(r => r.Root.Kind == FileRootKind.InternalStorage)
                   ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "The phone has no internal storage folder.");
        lock (Gate)
        {
            if (!root.Tree.Children!.TryGetValue("Ringtones", out var folder))
            {
                folder = FakeFileNode.Dir("Ringtones");
                root.Tree.Children["Ringtones"] = folder;
            }

            folder.Children![fileName] = FakeFileNode.File(fileName, length, DateTimeOffset.Now);
        }

        return new RingtoneResult("/sdcard/Ringtones/" + fileName, setAsDefault);
    }

    // ---- Wi-Fi sync ----

    public Task<bool?> GetWifiSyncAsync(CancellationToken ct)
    {
        EnsureConnected();
        if (!IsIphone)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "Only an iPhone has this switch.");
        }

        return Task.FromResult<bool?>(_wifiSync || Summary.Connection == ConnectionKind.Wifi);
    }

    public Task SetWifiSyncAsync(bool enabled, CancellationToken ct)
    {
        EnsureConnected();
        if (!IsIphone)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "Only an iPhone has this switch.");
        }

        _wifiSync = enabled;
        return Task.CompletedTask;
    }

    // ---- mirroring ----

    public Task<IScreenMirrorSession> StartMirrorAsync(MirrorOptions options, CancellationToken ct)
    {
        EnsureConnected();
        Require(DeviceCapability.ScreenMirror, "mirror its screen");
        throw new DeviceException(DeviceErrorCodes.NotSupported, "The sample phone has no screen to mirror; connect a real Android phone.");
    }
}
