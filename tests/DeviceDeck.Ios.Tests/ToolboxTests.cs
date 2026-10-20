using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Tests.Fakes;
using DeviceDeck.Ios.Toolbox;

namespace DeviceDeck.Ios.Tests;

/// <summary>The iPhone's toolbox services: syslog_relay, crash reports and the Wi-Fi sync switch, parsers first.</summary>
public sealed class ToolboxTests : IAsyncLifetime
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-ios-toolbox-").FullName;
    private readonly FakeUsbmux _mux = new();
    private readonly IosDeviceProvider _provider;

    public ToolboxTests()
    {
        _provider = new IosDeviceProvider(_mux, new IosProviderOptions
        {
            ApplePairRecordDirectory = Path.Combine(_dir, "apple"),
            LocalPairRecordDirectory = Path.Combine(_dir, "local"),
            CacheDirectory = Path.Combine(_dir, "cache"),
            RetryInterval = TimeSpan.FromMilliseconds(40),
            ReconnectInterval = TimeSpan.FromMilliseconds(50),
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<IosDevice> ReadyAsync(FakePhone phone)
    {
        _mux.Attach(1, phone);
        await _provider.StartAsync(Ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (_provider.Find(Udid)?.Summary.State != DeviceState.Ready)
        {
            Assert.True(DateTime.UtcNow < deadline, "the fake iPhone never became ready");
            await Task.Delay(20);
        }
        return _provider.Find(Udid)!;
    }

    private FakePhone PairedPhone()
    {
        _mux.PairRecords[Udid] = TestPairing.StoredRecordXml();
        return new FakePhone(Udid) { ExpectedHostId = TestPairing.HostId };
    }

    // ---- parsers ----

    [Fact]
    public void Splitter_cuts_on_newline_nul_across_reads()
    {
        var splitter = new SyslogRelay.Splitter();
        Assert.Empty(splitter.Push("\0Oct 20 16:44:01 iPhone a[1] <Notice>: one\n"u8));
        var first = splitter.Push("\0Oct 20 16:44:02 iPhone b[2] <Error>: two\nstill two\n\0Oct"u8);
        Assert.Equal(["Oct 20 16:44:01 iPhone a[1] <Notice>: one", "Oct 20 16:44:02 iPhone b[2] <Error>: two\nstill two"],
            first.Select(m => Encoding.UTF8.GetString(m)));
        Assert.Empty(splitter.Push(" 20 16:44:03 iPhone c[3] <Debug>: three"u8));
        Assert.Equal("Oct 20 16:44:03 iPhone c[3] <Debug>: three", Encoding.UTF8.GetString(Assert.Single(splitter.Push("\n\0"u8))));
    }

    [Fact]
    public void Vis_escapes_become_utf8()
    {
        Assert.Equal("Sadra’s iPhone", SyslogRelay.Decode(Encoding.ASCII.GetBytes(@"Sadra\M-b\M^@\M^Ys iPhone")));
        Assert.Equal(@"C:\path", SyslogRelay.Decode(Encoding.ASCII.GetBytes(@"C:\\path")));
        Assert.Equal("tab\there", SyslogRelay.Decode(Encoding.ASCII.GetBytes(@"tab\^Ihere")));
        Assert.Equal("plain ascii", SyslogRelay.Decode("plain ascii"u8));
        Assert.Equal("عکس", SyslogRelay.Decode(Encoding.UTF8.GetBytes("عکس")));
    }

    [Fact]
    public void Syslog_lines_parse_into_entries()
    {
        var now = new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.FromHours(3.5));
        var e = SyslogRelay.Parse("Oct 20 16:44:01 Sadras-iPhone SpringBoard(FrontBoard)[61] <Notice>: Scene update", now);
        Assert.Equal((DeviceLogLevel.Notice, "SpringBoard", 61, "FrontBoard", "Scene update"), (e.Level, e.Process, e.Pid, e.Tag, e.Message));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 16, 44, 1, TimeSpan.FromHours(3.5)), e.Time);

        var kernel = SyslogRelay.Parse("Oct  2 09:05:07 iPhone kernel[0] <Error>: AppleBCMWLAN: panic?", now);
        Assert.Equal((DeviceLogLevel.Error, "kernel", 0, (string?)null), (kernel.Level, kernel.Process, kernel.Pid, kernel.Tag));
        Assert.Equal(2, kernel.Time!.Value.Day);

        var multi = SyslogRelay.Parse("Oct 20 16:44:01 iPhone backboardd[72] <Fault>: first\nsecond", now);
        Assert.Equal((DeviceLogLevel.Fault, "first\nsecond"), (multi.Level, multi.Message));

        var lastYear = SyslogRelay.Parse("Dec 31 23:59:59 iPhone x[1] <Info>: old", new DateTimeOffset(2027, 1, 1, 0, 0, 5, TimeSpan.Zero));
        Assert.Equal(2026, lastYear.Time!.Value.Year);

        var odd = SyslogRelay.Parse("=== not a syslog line ===", now);
        Assert.Equal((DeviceLogLevel.Info, "", (DateTimeOffset?)null, "=== not a syslog line ==="), (odd.Level, odd.Process, odd.Time, odd.Message));
    }

    [Theory]
    [InlineData("Instagram-2026-10-19-201102.ips", "Instagram", CrashReportKind.Crash)]
    [InlineData("JetsamEvent-2026-10-19-180544.ips", "JetsamEvent", CrashReportKind.Memory)]
    [InlineData("Retired/Telegram-2026-10-17-093012.ips.synced", "Telegram", CrashReportKind.Crash)]
    [InlineData("ExcUserFault_Photos-2026-10-16-111530.ips", "Photos", CrashReportKind.Diagnostic)]
    [InlineData("stacks+com.apple.mobilemail-2026-10-01-100000.ips", "com.apple.mobilemail", CrashReportKind.Hang)]
    [InlineData("Panics/panic-full-2026-10-12-042201.0002.ips", "kernel", CrashReportKind.Panic)]
    [InlineData("Analytics-2026-10-20-000000.ips.ca.synced", "Analytics", CrashReportKind.Diagnostic)]
    [InlineData("SiriSearchFeedback.log", "SiriSearchFeedback", CrashReportKind.Diagnostic)]
    public void Crash_report_names_tell_process_kind_and_time(string id, string process, CrashReportKind kind)
    {
        var modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var report = IosCrashReports.Describe(id, 1234, modified);
        Assert.Equal((id, process, kind, 1234L), (report.Id, report.Process, report.Kind, report.Size));
        Assert.Equal(id[(id.LastIndexOf('/') + 1)..], report.FileName);
        if (id.Contains("2026-10-19-201102", StringComparison.Ordinal))
        {
            Assert.Equal(new DateTime(2026, 10, 19, 20, 11, 2), report.Time!.Value.DateTime);
        }
        if (id.EndsWith(".log", StringComparison.Ordinal))
        {
            Assert.Equal(modified, report.Time);
        }
    }

    [Theory]
    [InlineData("{\"app_name\":\"Instagram\",\"bug_type\":\"309\",\"os_version\":\"iPhone OS 27.0\"}\n{...}", CrashReportKind.Crash, "Instagram")]
    [InlineData("{\"bug_type\":\"211\",\"timestamp\":\"2026-10-19\"}", CrashReportKind.Diagnostic, "WiFiLQMMetrics")]
    [InlineData("{\"bug_type\":\"298\",\"name\":\"JetsamEvent\"}\n", CrashReportKind.Memory, "JetsamEvent")]
    [InlineData("{\"bug_type\":\"288\",\"name\":\"SpringBoard\"}\n", CrashReportKind.Hang, "SpringBoard")]
    [InlineData("{\"bug_type\":\"210\"}\n", CrashReportKind.Panic, "WiFiLQMMetrics")]
    [InlineData("Incident Identifier: 1234\nCrashReporter Key: …", CrashReportKind.Crash, "WiFiLQMMetrics")]
    public void Ips_headers_say_what_a_report_is(string head, CrashReportKind kind, string process)
    {
        var guess = IosCrashReports.Describe("WiFiLQMMetrics-2026-10-19-171447.ips", 10, null);
        var refined = IosCrashReports.FromHeader(guess, Encoding.UTF8.GetBytes(head));
        Assert.Equal((kind, process), (refined.Kind, refined.Process));
    }

    [Fact]
    public void Resource_reports_are_diagnostics_of_their_app()
    {
        var report = IosCrashReports.Describe("WhatsApp.diskwrites_resource-2026-10-19-184226.ips", 1, null);
        Assert.Equal((CrashReportKind.Diagnostic, "WhatsApp"), (report.Kind, report.Process));
    }

    [Fact]
    public void Report_ids_cannot_leave_the_crash_folder()
    {
        Assert.Equal("/Retired/x.ips", IosCrashReports.PathOf("Retired/x.ips"));
        Assert.Equal(DeviceErrorCodes.PathNotFound, Assert.Throws<DeviceException>(() => IosCrashReports.PathOf("../Media/DCIM/x.jpg")).Code);
        Assert.Throws<DeviceException>(() => IosCrashReports.PathOf(@"a\b.ips"));
    }

    // ---- against the fake phone ----

    [Fact]
    public async Task Live_log_streams_until_cancelled()
    {
        var release = new TaskCompletionSource();
        var phone = PairedPhone();
        phone.ExtraService = async (service, s, ct) =>
        {
            if (service != SyslogRelay.ServiceName)
                return false;
            await s.WriteAsync("\0Oct 20 16:44:01 iPhone SpringBoard(FrontBoard)[61] <Notice>: one\n\0Oct 20 16:44:02 iPhone kernel[0] <Err"u8.ToArray(), ct);
            await s.FlushAsync(ct);
            await s.WriteAsync("or>: two\n\0"u8.ToArray(), ct);
            await s.FlushAsync(ct);
            await release.Task.WaitAsync(ct);
            return true;
        };
        var device = await ReadyAsync(phone);
        Assert.True(device.Summary.Capabilities.HasFlag(DeviceCapability.Syslog | DeviceCapability.CrashReports));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entries = new List<DeviceLogEntry>();
        await foreach (var entry in device.StreamLogAsync(cts.Token))
        {
            entries.Add(entry);
            if (entries.Count == 2)
            {
                await cts.CancelAsync();
            }
        }

        Assert.Equal([("SpringBoard", DeviceLogLevel.Notice, "one"), ("kernel", DeviceLogLevel.Error, "two")],
            entries.Select(e => (e.Process, e.Level, e.Message)));
        release.TrySetResult();
    }

    [Fact]
    public async Task Live_log_ends_with_disconnected_when_the_phone_closes_it()
    {
        var phone = PairedPhone();
        phone.ExtraService = async (service, s, ct) =>
        {
            if (service != SyslogRelay.ServiceName)
                return false;
            await s.WriteAsync("\0Oct 20 16:44:01 iPhone a[1] <Info>: last words\n\0"u8.ToArray(), ct);
            await s.FlushAsync(ct);
            return true; // the fake closes the stream
        };
        var device = await ReadyAsync(phone);
        var seen = new List<string>();
        var error = await Assert.ThrowsAsync<DeviceException>(async () =>
        {
            await foreach (var entry in device.StreamLogAsync(Ct))
            {
                seen.Add(entry.Message);
            }
        });
        Assert.Equal(DeviceErrorCodes.Disconnected, error.Code);
        Assert.Equal(["last words"], seen);
    }

    [Fact]
    public async Task Crash_reports_list_read_and_delete_through_the_copy_service()
    {
        var phone = PairedPhone();
        var crashes = new FakeAfcServer()
            .AddFile("/Instagram-2026-10-19-201102.ips", "Exception Type: EXC_BAD_ACCESS"u8.ToArray())
            .AddFile("/Retired/Telegram-2026-10-17-093012.ips", new byte[300])
            .AddFile("/JetsamEvent-2026-10-20-080000.ips", new byte[40])
            .AddFile("/WiFiLQMMetrics-2026-10-18-100000.ips", "{\"bug_type\":\"211\"}\n{}"u8.ToArray());
        var moverPinged = false;
        phone.ExtraService = async (service, s, ct) =>
        {
            switch (service)
            {
                case IosCrashReports.MoverService:
                    moverPinged = true;
                    await s.WriteAsync("ping\0"u8.ToArray(), ct);
                    await s.FlushAsync(ct);
                    return true;
                case IosCrashReports.CopyService:
                    await crashes.ServeAsync(s, ct);
                    return true;
                default:
                    return false;
            }
        };
        var device = await ReadyAsync(phone);

        var list = await device.ListCrashReportsAsync(Ct);
        Assert.True(moverPinged);
        Assert.True(list.CanDelete);
        Assert.Empty(list.Unreadable);
        Assert.Equal(["JetsamEvent-2026-10-20-080000.ips", "Instagram-2026-10-19-201102.ips", "WiFiLQMMetrics-2026-10-18-100000.ips", "Retired/Telegram-2026-10-17-093012.ips"],
            list.Reports.Select(r => r.Id));
        Assert.Equal(CrashReportKind.Diagnostic, list.Reports.Single(r => r.Process == "WiFiLQMMetrics").Kind); // its header says analytics
        Assert.Equal(300, list.Reports.Single(r => r.Process == "Telegram").Size);

        using var text = new MemoryStream();
        await device.ReadCrashReportAsync("Instagram-2026-10-19-201102.ips", text, Ct);
        Assert.Equal("Exception Type: EXC_BAD_ACCESS", Encoding.UTF8.GetString(text.ToArray()));

        await device.DeleteCrashReportsAsync(["Retired/Telegram-2026-10-17-093012.ips", "already-gone.ips"], Ct);
        Assert.False(crashes.Exists("/Retired/Telegram-2026-10-17-093012.ips"));
        Assert.True(crashes.Exists("/Instagram-2026-10-19-201102.ips"));

        await Assert.ThrowsAsync<DeviceException>(() => device.ReadCrashReportAsync("../etc/passwd", Stream.Null, Ct));
    }

    [Fact]
    public async Task Wifi_sync_switch_reads_and_writes_the_wireless_lockdown_domain()
    {
        var phone = PairedPhone();
        phone.Domains[IosDevice.WirelessLockdownDomain] = new PlistDict { [IosDevice.WifiConnectionsKey] = false };
        var device = await ReadyAsync(phone);

        Assert.False(await device.GetWifiSyncAsync(Ct));
        await device.SetWifiSyncAsync(true, Ct);
        Assert.True(await device.GetWifiSyncAsync(Ct));
        Assert.Contains($"lockdown:SetValue {IosDevice.WirelessLockdownDomain} {IosDevice.WifiConnectionsKey}", phone.Log);
    }
}
