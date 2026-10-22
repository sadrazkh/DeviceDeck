using DeviceDeck.Android.Adb;
using DeviceDeck.Android.PlatformTools;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Android.Usb;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Tests;

/// <summary>
/// The Drivers card's Android row (v0.6): status while the private server starts (v0.5.3 said "Stopped" with adb running),
/// Start / Restart, where adb.exe is looked for, and downloading Google's platform-tools from a local fake server.
/// </summary>
public sealed class DriversTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private static CancellationToken None => CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", "drivers-" + Guid.NewGuid().ToString("N"));

    public DriversTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return path;
    }

    // ---------- Status while starting: the v0.5.3 report ----------

    [Fact]
    public async Task While_the_server_is_starting_the_row_says_starting_never_stopped_and_it_turns_ready_by_itself()
    {
        await using var server = new FakeAdbServer { Running = false };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new AndroidProviderOptions
        {
            AdbPath = Path.Combine(_root, "no-such-adb.exe"),
            ServerPort = server.Port,
            CacheDirectory = null,
            UsbScan = () => [],
            // `adb start-server` that takes its time, like a cold start with antivirus scanning adb.exe.
            ServerStarter = async ct =>
            {
                starting.TrySetResult();
                await gate.Task.WaitAsync(ct);
                server.Running = true;
            },
        };
        await using var provider = new AndroidDeviceProvider(options);
        int changes = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DriverStatusChanged += async (_, _) =>
        {
            Interlocked.Increment(ref changes);
            if ((await provider.GetDriverStatusAsync(None)).State == DriverState.Ready)
            {
                ready.TrySetResult();
            }
        };

        // Asked before the app started the provider: grey "not started", not an error.
        var before = await provider.GetDriverStatusAsync(None);
        Assert.Equal((DriverState.Idle, DriverReasons.AdbNotStarted), (before.State, before.Reason));

        var start = provider.StartAsync(None);
        await starting.Task.WaitAsync(Wait);

        // The page's first drivers.status lands here in v0.5.3: it said Stopped and kept it.
        var during = await provider.GetDriverStatusAsync(None);
        Assert.Equal((DriverState.Starting, DriverReasons.AdbStarting), (during.State, during.Reason));

        gate.SetResult();
        await start.WaitAsync(Wait);
        await ready.Task.WaitAsync(Wait);
        Assert.Equal(DriverState.Ready, (await provider.GetDriverStatusAsync(None)).State);
        Assert.True(Volatile.Read(ref changes) >= 1);
    }

    [Fact]
    public async Task Through_the_hub_a_page_that_asked_during_the_start_is_told_ready_without_waiting_for_a_poll()
    {
        // The owner's v0.5.3 case end to end: the window loads, the page asks drivers.status while the hub is still
        // starting the providers (adb's start-server takes a second or more), and nothing ever told it otherwise.
        await using var server = new FakeAdbServer { Running = false };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new AndroidProviderOptions
        {
            AdbPath = Path.Combine(_root, "no-such-adb.exe"),
            ServerPort = server.Port,
            CacheDirectory = null,
            UsbScan = () => [],
            ServerStarter = async ct =>
            {
                starting.TrySetResult();
                await gate.Task.WaitAsync(ct);
                server.Running = true;
            },
        };
        // No periodic poll at all: the provider's own signal has to carry the news.
        await using var hub = new Core.Hub.DeviceHub([new AndroidDeviceProvider(options)]) { DriverPollInterval = TimeSpan.Zero, DriverCheckDelay = TimeSpan.Zero };
        var published = new TaskCompletionSource<IReadOnlyList<DriverStatus>>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.DriversChanged += (_, drivers) =>
        {
            if (drivers.Single().State == DriverState.Ready)
            {
                published.TrySetResult(drivers);
            }
        };

        var start = hub.StartAsync(None);
        await starting.Task.WaitAsync(Wait);
        var page = await hub.GetDriverStatusAsync(None); // the page's first (and in v0.5.3 only) question
        Assert.Equal(DriverState.Starting, page.Single().State);

        gate.SetResult();
        await start.WaitAsync(Wait);
        var told = await published.Task.WaitAsync(Wait);
        Assert.Equal("adb protocol 41", told.Single().Version);
    }

    [Fact]
    public async Task A_server_that_wont_start_is_a_problem_with_adbs_words_and_start_tries_again()
    {
        await using var server = new FakeAdbServer { Running = false };
        bool fail = true;
        var options = new AndroidProviderOptions
        {
            AdbPath = Path.Combine(_root, "no-such-adb.exe"),
            ServerPort = server.Port,
            CacheDirectory = null,
            UsbScan = () => [],
            ServerStarter = _ =>
            {
                if (Volatile.Read(ref fail))
                {
                    throw new InvalidOperationException("could not install *smartsocket* listener: cannot bind to 127.0.0.1:5137");
                }
                server.Running = true;
                return Task.CompletedTask;
            },
        };
        await using var provider = new AndroidDeviceProvider(options);
        await provider.StartAsync(None);

        var failed = await provider.GetDriverStatusAsync(None);
        Assert.Equal((DriverState.Error, DriverReasons.AdbFailed), (failed.State, failed.Reason));
        Assert.Contains("cannot bind", failed.Detail, StringComparison.Ordinal);

        // Restart while it still can't: the card gets adb's reason.
        var refused = await Assert.ThrowsAsync<DeviceException>(() => provider.StartDriverAsync(None));
        Assert.Contains("cannot bind", refused.Message, StringComparison.Ordinal);

        // The port is free now: Restart works and tracking resumes at once.
        Volatile.Write(ref fail, false);
        await provider.StartDriverAsync(None);
        var status = await provider.GetDriverStatusAsync(None);
        Assert.Equal(DriverState.Ready, status.State);
        Assert.Null(status.Reason);
    }

    [Fact]
    public async Task Without_adb_the_row_is_missing_and_start_says_to_download_first()
    {
        int port;
        await using (var gone = new FakeAdbServer())
        {
            port = gone.Port;
        }
        var options = new AndroidProviderOptions
        {
            ServerPort = port,
            CacheDirectory = null,
            BaseDirectory = Folder("app"),
            ToolsDirectory = Folder("tools"),
            EnvironmentAdbPath = () => null,
            UsbScan = () => [],
        };
        await using var provider = new AndroidDeviceProvider(options);
        await provider.StartAsync(None);
        var status = await provider.GetDriverStatusAsync(None);
        Assert.Equal((DriverState.Missing, DriverReasons.AdbMissing), (status.State, status.Reason));
        Assert.Null(status.Source);
        var refused = await Assert.ThrowsAsync<DeviceException>(() => provider.StartDriverAsync(None));
        Assert.Equal(DeviceErrorCodes.DriverMissing, refused.Code);
    }

    // ---------- Where adb.exe is looked for ----------

    [Fact]
    public void Adb_is_looked_for_bundled_first_then_downloaded_then_the_variable()
    {
        string app = Folder(Path.Combine("pc", "DeviceDeck.App", "current"));
        string tools = Folder("tools");
        string environment = Touch(Path.Combine(_root, "elsewhere", "adb.exe"));

        Assert.Equal(new AdbLocation(environment, DriverSources.Environment), AdbServerHost.Locate(null, app, tools, environment));

        string downloaded = Touch(Path.Combine(tools, "platform-tools", "adb.exe"));
        Assert.Equal(new AdbLocation(downloaded, DriverSources.Downloaded), AdbServerHost.Locate(null, app, tools, environment));

        string bundled = Touch(Path.Combine(app, "platform-tools", "adb.exe"));
        Assert.Equal(new AdbLocation(bundled, DriverSources.Bundled), AdbServerHost.Locate(null, app, tools, environment));

        // A path given in code beats them all, and isn't swapped for another when its file is missing.
        Assert.Equal(new AdbLocation(environment, DriverSources.Configured), AdbServerHost.Locate(environment, app, tools, null));
        Assert.Equal(new AdbLocation(null, DriverSources.Configured), AdbServerHost.Locate(Path.Combine(_root, "nope.exe"), app, tools, environment));

        // DEVICEDECK_ADB_PATH to a file that isn't there counts as nothing.
        Assert.Equal(new AdbLocation(null, null), AdbServerHost.Locate(null, Folder("empty-app"), Folder("empty-tools"), Path.Combine(_root, "nope.exe")));
    }

    [Fact]
    public async Task The_tools_version_is_read_once_per_adb()
    {
        string adb = Touch(Path.Combine(Folder("tools-version"), "adb.exe"));
        string properties = Path.Combine(Path.GetDirectoryName(adb)!, "source.properties");
        await File.WriteAllTextAsync(properties, "Pkg.Revision=37.0.1\n");
        var host = new AdbServerHost(adb, 1, source: DriverSources.Downloaded);
        Assert.Equal("37.0.1", await host.GetToolsVersionCachedAsync(None));
        await File.WriteAllTextAsync(properties, "Pkg.Revision=99.0.0\n");
        Assert.Equal("37.0.1", await host.GetToolsVersionCachedAsync(None));
    }

    // ---------- Downloading Google's platform-tools (a local fake server; never the internet) ----------

    private PlatformToolsInstaller Installer(FakeHttpServer server, string tools, string? signer = AuthenticodeSigner.Google, string? version = "37.0.1") =>
        new(tools, server.Url, _ => signer, (_, _) => Task.FromResult(version));

    private static void AssertNothingLeftBut(string tools, params string[] names) =>
        Assert.Equal(names.Order(), Directory.EnumerateFileSystemEntries(tools).Select(Path.GetFileName).Order());

    [Fact]
    public async Task A_good_download_reports_progress_and_is_installed_only_when_complete_and_checked()
    {
        var zip = PlatformToolsZips.Good();
        await using var server = new FakeHttpServer(zip);
        string tools = Folder("tools");
        var progress = new List<(long Received, long? Total)>();
        var stages = new List<DriverDownloadState>();
        var checkedFiles = new List<string>();
        var installer = new PlatformToolsInstaller(tools, server.Url,
            path =>
            {
                // Checked in the staging folder, before anything is installed.
                Assert.False(Directory.Exists(Path.Combine(tools, "platform-tools")));
                Assert.Contains("platform-tools.new", path, StringComparison.Ordinal);
                checkedFiles.Add(Path.GetFileName(path));
                return AuthenticodeSigner.Google;
            },
            (adb, _) =>
            {
                Assert.True(File.Exists(adb));
                return Task.FromResult<string?>("37.0.1");
            });

        string version = await installer.InstallAsync((r, t) => progress.Add((r, t)), stages.Add, None);

        Assert.Equal("37.0.1", version);
        Assert.Equal([DriverDownloadState.Verifying, DriverDownloadState.Installing], stages);
        Assert.Equal(["adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll"], checkedFiles);
        Assert.True(progress.Count >= 3, $"only {progress.Count} progress reports");
        Assert.All(progress, p => Assert.Equal(zip.Length, p.Total));
        Assert.Equal(progress.Select(p => p.Received).Order(), progress.Select(p => p.Received));
        Assert.Equal(zip.Length, progress[^1].Received);
        Assert.True(File.Exists(Path.Combine(tools, "platform-tools", "adb.exe")));
        Assert.True(File.Exists(Path.Combine(tools, "platform-tools", "AdbWinUsbApi.dll")));
        AssertNothingLeftBut(tools, "platform-tools");
    }

    [Fact]
    public async Task A_cancelled_download_leaves_nothing_behind()
    {
        await using var server = new FakeHttpServer(PlatformToolsZips.Good()) { StallAfter = 64 * 1024 };
        string tools = Folder("tools");
        using var cancel = new CancellationTokenSource();
        var install = Installer(server, tools).InstallAsync((_, _) => { }, _ => { }, cancel.Token);

        await server.Stalled.Task.WaitAsync(Wait);
        await Until(() => File.Exists(Path.Combine(tools, "platform-tools.zip.part"))); // the client got the first bytes
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install.WaitAsync(Wait));
        AssertNothingLeftBut(tools);
    }

    [Fact]
    public async Task A_download_that_isnt_a_zip_is_corrupt_and_nothing_is_installed()
    {
        await using var server = new FakeHttpServer(PlatformToolsZips.Noise(50_000, 7));
        string tools = Folder("tools");
        var failure = await Assert.ThrowsAsync<PlatformToolsException>(() => Installer(server, tools).InstallAsync((_, _) => { }, _ => { }, None));
        Assert.Equal(DriverDownloadErrors.Corrupt, failure.Code);
        AssertNothingLeftBut(tools);
    }

    [Theory]
    [InlineData("platform-tools/AdbWinUsbApi.dll")]
    [InlineData("platform-tools/adb.exe")]
    public async Task A_zip_without_adbs_files_is_refused(string missing)
    {
        await using var server = new FakeHttpServer(PlatformToolsZips.Without(missing));
        string tools = Folder("tools");
        var failure = await Assert.ThrowsAsync<PlatformToolsException>(() => Installer(server, tools).InstallAsync((_, _) => { }, _ => { }, None));
        Assert.Equal(DriverDownloadErrors.Invalid, failure.Code);
        Assert.Contains(Path.GetFileName(missing), failure.Message, StringComparison.Ordinal);
        AssertNothingLeftBut(tools);
    }

    [Fact]
    public async Task A_zip_with_a_file_outside_platform_tools_is_refused()
    {
        var evil = new Dictionary<string, byte[]>
        {
            ["platform-tools/adb.exe"] = [1],
            ["platform-tools/AdbWinApi.dll"] = [2],
            ["platform-tools/AdbWinUsbApi.dll"] = [3],
            ["platform-tools/../../evil.cmd"] = [4],
        };
        await using var server = new FakeHttpServer(PlatformToolsZips.Build(evil));
        string tools = Folder("tools");
        var failure = await Assert.ThrowsAsync<PlatformToolsException>(() => Installer(server, tools).InstallAsync((_, _) => { }, _ => { }, None));
        Assert.Equal(DriverDownloadErrors.Invalid, failure.Code);
        Assert.False(File.Exists(Path.Combine(_root, "evil.cmd")));
        AssertNothingLeftBut(tools);
    }

    [Theory]
    [InlineData(null, "37.0.1", "isn't signed by Google")]
    [InlineData("Contoso Ltd", "37.0.1", "signed by Contoso Ltd")]
    [InlineData(AuthenticodeSigner.Google, null, "didn't say its version")]
    public async Task Files_not_signed_by_google_or_an_adb_that_doesnt_run_are_refused(string? signer, string? version, string message)
    {
        await using var server = new FakeHttpServer(PlatformToolsZips.Good());
        string tools = Folder("tools");
        var failure = await Assert.ThrowsAsync<PlatformToolsException>(() =>
            Installer(server, tools, signer, version).InstallAsync((_, _) => { }, _ => { }, None));
        Assert.Equal(DriverDownloadErrors.Unverified, failure.Code);
        Assert.Contains(message, failure.Message, StringComparison.Ordinal);
        AssertNothingLeftBut(tools);
    }

    [Fact]
    public async Task A_failed_download_keeps_the_tools_installed_before()
    {
        string tools = Folder("tools");
        string before = Touch(Path.Combine(tools, "platform-tools", "adb.exe"));
        await using var server = new FakeHttpServer(PlatformToolsZips.Good()) { CutAfter = 30_000 };
        var failure = await Assert.ThrowsAsync<PlatformToolsException>(() => Installer(server, tools).InstallAsync((_, _) => { }, _ => { }, None));
        Assert.Equal(DriverDownloadErrors.Network, failure.Code);
        Assert.True(File.Exists(before));
        AssertNothingLeftBut(tools, "platform-tools");
    }

    [Fact]
    public async Task An_http_error_is_a_network_failure()
    {
        await using var server = new FakeHttpServer([]) { Status = 404 };
        var failure = await Assert.ThrowsAsync<PlatformToolsException>(() => Installer(server, Folder("tools")).InstallAsync((_, _) => { }, _ => { }, None));
        Assert.Equal(DriverDownloadErrors.Network, failure.Code);
        Assert.Contains("404", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_tools_are_downloaded_installed_and_the_private_server_starts_from_them()
    {
        await using var http = new FakeHttpServer(PlatformToolsZips.Good()) { StallAfter = 64 * 1024 };
        await using var adb = new FakeAdbServer { Running = false };
        string tools = Folder("tools");
        string? startedWith = null;
        var options = new AndroidProviderOptions
        {
            ServerPort = adb.Port,
            CacheDirectory = null,
            BaseDirectory = Folder("app"),
            ToolsDirectory = tools,
            EnvironmentAdbPath = () => null,
            PlatformToolsSource = http.Url,
            SignerOf = _ => AuthenticodeSigner.Google,
            ToolsVersionOf = (_, _) => Task.FromResult<string?>("37.0.1"),
            PathServerStarter = (path, _) =>
            {
                startedWith = path;
                adb.Running = true;
                return Task.CompletedTask;
            },
            UsbScan = () => [],
        };
        await using var provider = new AndroidDeviceProvider(options);
        await provider.StartAsync(None);
        Assert.Equal(DriverState.Missing, (await provider.GetDriverStatusAsync(None)).State);

        var updates = new List<DriverDownloadStatus>();
        var done = new TaskCompletionSource<DriverDownloadStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DownloadChanged += (_, s) =>
        {
            lock (updates)
            {
                updates.Add(s);
            }
            if (s.State is DriverDownloadState.Done or DriverDownloadState.Failed or DriverDownloadState.Cancelled)
            {
                done.TrySetResult(s);
            }
        };
        provider.DriverStatusChanged += async (_, _) =>
        {
            if ((await provider.GetDriverStatusAsync(None)).State == DriverState.Ready)
            {
                ready.TrySetResult();
            }
        };

        var first = provider.StartDownload();
        Assert.Equal(DriverDownloadState.Downloading, first.State);
        await http.Stalled.Task.WaitAsync(Wait);
        Assert.Equal(DriverDownloadState.Downloading, provider.StartDownload().State); // a second click joins it: one request
        http.Release.TrySetResult();

        var final = await done.Task.WaitAsync(Wait);
        Assert.Equal((DriverDownloadState.Done, "37.0.1"), (final.State, final.Version));
        await ready.Task.WaitAsync(Wait);

        var status = await provider.GetDriverStatusAsync(None);
        Assert.Equal((DriverState.Ready, DriverSources.Downloaded), (status.State, status.Source));
        Assert.Equal(Path.Combine(tools, "platform-tools", "adb.exe"), startedWith);
        Assert.Equal(1, http.Requests);
        lock (updates)
        {
            Assert.Contains(updates, u => u.State == DriverDownloadState.Verifying);
            Assert.Contains(updates, u => u.State == DriverDownloadState.Installing);
            var received = updates.Where(u => u.State == DriverDownloadState.Downloading).Select(u => u.ReceivedBytes).ToList();
            Assert.Equal(received.Order(), received);
        }
    }

    [Fact]
    public async Task Cancel_stops_the_providers_download_and_it_can_start_again()
    {
        await using var http = new FakeHttpServer(PlatformToolsZips.Good()) { StallAfter = 64 * 1024 };
        string tools = Folder("tools");
        var options = new AndroidProviderOptions
        {
            ServerPort = 1,
            CacheDirectory = null,
            BaseDirectory = Folder("app"),
            ToolsDirectory = tools,
            EnvironmentAdbPath = () => null,
            PlatformToolsSource = http.Url,
            SignerOf = _ => AuthenticodeSigner.Google,
            ToolsVersionOf = (_, _) => Task.FromResult<string?>("37.0.1"),
            UsbScan = () => [],
        };
        await using var provider = new AndroidDeviceProvider(options);
        var cancelled = new TaskCompletionSource<DriverDownloadStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.DownloadChanged += (_, s) =>
        {
            if (s.State == DriverDownloadState.Cancelled)
            {
                cancelled.TrySetResult(s);
            }
        };

        provider.StartDownload();
        await http.Stalled.Task.WaitAsync(Wait);
        provider.CancelDownload();
        Assert.Equal(DriverDownloadState.Cancelled, (await cancelled.Task.WaitAsync(Wait)).State);
        Assert.Equal(DriverDownloadState.Cancelled, provider.DownloadStatus.State);
        Assert.Empty(Directory.EnumerateFileSystemEntries(tools));

        http.StallAfter = null;
        Assert.Equal(DriverDownloadState.Downloading, provider.StartDownload().State);
    }

    // ---------- Phones the card helps with ----------

    [Fact]
    public async Task Unauthorized_and_offline_phones_are_hints_on_the_row()
    {
        await using var server = new FakeAdbServer();
        await server.SetDevicesAsync(new FakeDevice("R58N1", "unauthorized", 1), new FakeDevice("emulator-5554", "offline", 2));
        var options = new AndroidProviderOptions
        {
            AdbPath = Path.Combine(_root, "no-such-adb.exe"),
            ServerPort = server.Port,
            CacheDirectory = null,
            UsbScan = () => [],
        };
        await using var provider = new AndroidDeviceProvider(options);
        await provider.StartAsync(None);
        var status = await provider.GetDriverStatusAsync(None);
        Assert.Equal(DriverState.Ready, status.State);
        Assert.Equal(
            [DriverPhoneProblems.Unauthorized, DriverPhoneProblems.Offline],
            status.Phones!.Select(p => p.Problem));
    }

    [Fact]
    public async Task A_phone_windows_sees_without_usb_debugging_is_a_hint_and_apple_devices_are_not()
    {
        await using var server = new FakeAdbServer();
        var options = new AndroidProviderOptions
        {
            AdbPath = Path.Combine(_root, "no-such-adb.exe"),
            ServerPort = server.Port,
            CacheDirectory = null,
            UsbScan = () =>
            [
                Node(@"USB\VID_04E8&PID_6860\R58N12345", null, "USB Composite Device"),
                Node(@"USB\VID_04E8&PID_6860&MI_00\6&1&0&0000", @"USB\VID_04E8&PID_6860\R58N12345", "Galaxy S23", @"USB\Class_06&SubClass_01&Prot_01"),
                Node(@"USB\VID_05AC&PID_12A8\00008140001601C02178801C", null, "Apple Mobile Device USB Composite Device"),
                Node(@"USB\VID_05AC&PID_12A8&MI_00\7&2&0&0000", @"USB\VID_05AC&PID_12A8\00008140001601C02178801C", "Apple iPhone", @"USB\Class_06&SubClass_01&Prot_01"),
            ],
        };
        await using var provider = new AndroidDeviceProvider(options);
        await provider.StartAsync(None);
        var hint = Assert.Single((await provider.GetDriverStatusAsync(None)).Phones!);
        Assert.Equal(new DriverPhoneHint("Galaxy S23", DriverPhoneProblems.UsbDebuggingOff, "samsung"), hint);
    }

    [Fact]
    public void Google_signs_the_bundled_adb_and_an_unsigned_file_has_no_signer()
    {
        Assert.Null(AuthenticodeSigner.OrganizationOf(Touch(Path.Combine(_root, "unsigned", "adb.exe"))));
        Assert.Null(AuthenticodeSigner.OrganizationOf(Path.Combine(_root, "missing.exe")));

        // The platform-tools fetched into third_party (a developer's checkout, CI): really signed by Google LLC.
        var bundled = AdbServerHost.Locate(null, AppContext.BaseDirectory, null, null);
        if (OperatingSystem.IsWindows() && bundled.Path is { } adb)
        {
            Assert.Equal(AuthenticodeSigner.Google, AuthenticodeSigner.OrganizationOf(adb));
            Assert.Equal(AuthenticodeSigner.Google, AuthenticodeSigner.OrganizationOf(Path.Combine(Path.GetDirectoryName(adb)!, "AdbWinUsbApi.dll")));
        }
    }

    [Fact]
    public void Windows_usb_list_can_be_read_without_changing_anything()
    {
        // Read-only on this PC: Configuration Manager's list of present USB devices.
        var nodes = WindowsUsbScan.Scan();
        Assert.All(nodes, n => Assert.StartsWith(@"USB\", n.InstanceId, StringComparison.OrdinalIgnoreCase));
    }

    internal static UsbNode Node(string id, string? parent, string? name, params string[] compatible) =>
        new(id, parent ?? @"USB\ROOT_HUB30\5&1&0&0", name, compatible, false);
}
