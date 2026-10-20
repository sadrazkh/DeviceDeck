using System.Diagnostics;
using System.Net.Sockets;
using DeviceDeck.Android;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Apps;
using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Android.Photos;
using DeviceDeck.Core.Abstractions;

namespace AndroidProbe;

internal sealed class Commands(AndroidDeviceProvider provider, CommandLine cli, CancellationToken ct)
{
    public static void PrintHelp()
    {
        Console.WriteLine("""
            androidprobe — exercise DeviceDeck's Android provider against a real phone.

              doctor                          adb present? our server on port 5137? devices?
              list                            devices and their state
              info                            summary + details rows (serial, IMEI if readable, MAC, build)
              storage | battery
              ls [path] [--root internal|sd:XXXX-XXXX]
              pull <phone-path> <pc-file> [--root ...]
              push <pc-file> <phone-path> [--root ...]
              apps [--system]                 installed apps (labels read from the APKs)
              icon <package> <out.png>
              install <file.apk|.apks|.xapk|.apkm> [more split .apk files]
              uninstall <package>
              export <package> <folder>
              albums
              photos [--limit 50] [--offset 0] [--album all|videos|favorites|bucket:<id>]
              thumb <asset-id> <out.jpg> [--size 256]
              import <photo-or-video-file>     copies into DCIM/DeviceDeck and asks MediaStore to index it
              screenshot <out.png>
              reboot [system|recovery|bootloader] | shutdown
              capture <folder>                raw command outputs, for test fixtures (contains personal data)
              shell "<command>"               run one shell command (debugging)

            Options: --serial <id>  --adb <path to adb.exe>  --port <n> (default 5137)  --no-cache  --verbose
            """);
    }

    public async Task<int> RunAsync() => cli.Command switch
    {
        "doctor" => await DoctorAsync(),
        "list" => await ListAsync(),
        "info" => await InfoAsync(),
        "storage" => await StorageAsync(),
        "battery" => await BatteryAsync(),
        "ls" => await LsAsync(),
        "pull" => await PullAsync(),
        "push" => await PushAsync(),
        "apps" => await AppsAsync(),
        "icon" => await IconAsync(),
        "install" => await InstallAsync(),
        "uninstall" => await UninstallAsync(),
        "export" => await ExportAsync(),
        "albums" => await AlbumsAsync(),
        "photos" => await PhotosAsync(),
        "thumb" => await ThumbAsync(),
        "import" => await ImportAsync(),
        "screenshot" => await ScreenshotAsync(),
        "reboot" => await RebootAsync(),
        "shutdown" => await ShutdownAsync(),
        "capture" => await CaptureAsync(),
        "shell" => await ShellAsync(),
        _ => throw new UsageException($"Unknown command '{cli.Command}'."),
    };

    private async Task<int> DoctorAsync()
    {
        bool ok = true;
        Console.WriteLine($"adb.exe      : {provider.AdbPath ?? "NOT FOUND — run tools/fetch-platform-tools.ps1"}");
        if (provider.AdbPath == null)
        {
            return 1;
        }
        string? tools = await provider.Host.GetToolsVersionAsync(ct);
        Console.WriteLine($"platform-tools: {tools ?? "unknown"}");

        Console.WriteLine($"server port  : {provider.ServerPort} (ANDROID_ADB_SERVER_PORT; the default 5037 is left alone)");
        int? before = await provider.Host.GetServerVersionAsync(ct);
        var watch = Stopwatch.StartNew();
        try
        {
            await provider.Host.EnsureStartedAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"server       : FAILED to start — {ex.Message}");
            return 1;
        }
        int? version = await provider.Host.GetServerVersionAsync(ct);
        Console.WriteLine(before != null
            ? $"server       : already running, protocol {version}"
            : $"server       : started in {Format.Elapsed(watch)}, protocol {version}");

        Console.WriteLine($"port 5037    : {(IsListening(5037) ? "another adb server is running there (fine; we don't use it)" : "free")}");

        await using (var connection = await AdbConnection.ConnectAsync(provider.Host.EndPoint, ct))
        {
            await connection.SendRequestAsync("host:devices-l", ct);
            string text = await connection.ReadLengthPrefixedStringAsync(ct) ?? "";
            var devices = TrackDevicesParser.Parse(text);
            if (devices.Count == 0)
            {
                Console.WriteLine("devices      : none — plug a phone in with USB debugging on (Settings > Developer options)");
            }
            foreach (var device in devices)
            {
                string transport = TrackDevicesParser.IsWifiSerial(device.Serial) ? "Wi-Fi" : "USB";
                Console.WriteLine($"device       : {device.Serial}  {device.State}  {transport}  model={device.Model ?? "?"}");
                if (device.State == "unauthorized")
                {
                    Console.WriteLine("               unlock the phone and tap Allow on \"Allow USB debugging?\"");
                    ok = false;
                }
            }
        }

        var status = await provider.GetDriverStatusAsync(ct);
        Console.WriteLine($"driver status: {status.State}{(status.Detail != null ? " — " + status.Detail : "")}");
        return ok && status.State == DriverState.Ready ? 0 : 1;
    }

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<int> ListAsync()
    {
        await StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(2), ct); // let getprop finish for phones that are already authorised
        if (provider.Devices.Count == 0)
        {
            Console.WriteLine("No devices.");
        }
        foreach (var device in provider.Devices)
        {
            PrintSummary(device.Summary);
        }
        return 0;
    }

    private async Task<int> InfoAsync()
    {
        var device = await DeviceAsync();
        PrintSummary(device.Summary);
        var details = await device.GetDetailsAsync(ct);
        foreach (var row in details.Rows)
        {
            Console.WriteLine($"  {row.Label,-20} {row.Value}{(row.Sensitive ? "   (sensitive)" : "")}");
        }
        if (device.Profile is { } profile)
        {
            Console.WriteLine($"  {"SDK",-20} {profile.Sdk}");
            var colorProps = profile.Props.Where(p => p.Key.Contains("color", StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"  {"Colour props",-20} {(colorProps.Count == 0 ? "(none)" : string.Join(", ", colorProps.Select(p => $"{p.Key}={p.Value}")))}");
        }
        return 0;
    }

    private async Task<int> StorageAsync()
    {
        var device = await DeviceAsync();
        var storage = await device.GetStorageAsync(ct);
        foreach (var volume in storage.Volumes)
        {
            Console.WriteLine($"{volume.Label} [{volume.Key}]: {Format.Bytes(volume.TotalBytes - volume.FreeBytes)} used of {Format.Bytes(volume.TotalBytes)}, {Format.Bytes(volume.FreeBytes)} free");
            foreach (var category in volume.Categories)
            {
                Console.WriteLine($"  {category.Key,-8} {Format.Bytes(category.Bytes)}");
            }
            if (volume.Categories.Count == 0)
            {
                Console.WriteLine("  (no category sizes yet — the phone computes them about once a day while charging)");
            }
        }
        return 0;
    }

    private async Task<int> BatteryAsync()
    {
        var device = await DeviceAsync();
        var battery = await device.GetBatteryAsync(ct);
        Console.WriteLine($"level        : {battery.LevelPercent?.ToString() ?? "?"}%{(battery.IsCharging ? " (charging)" : "")}");
        Console.WriteLine($"health       : {(battery.HealthPercent is { } h ? h + "%" : "not reported")}");
        Console.WriteLine($"cycles       : {battery.CycleCount?.ToString() ?? "not reported"}");
        Console.WriteLine($"temperature  : {(battery.TemperatureCelsius is { } t ? $"{t:0.0} °C" : "?")}");
        Console.WriteLine($"capacity     : full {battery.FullChargeCapacityMah?.ToString() ?? "?"} mAh / design {battery.DesignCapacityMah?.ToString() ?? "?"} mAh");
        return 0;
    }

    private async Task<int> LsAsync()
    {
        var device = await DeviceAsync();
        await using var fs = await device.OpenFileSystemAsync(cli.Option("root") ?? "internal", ct);
        string path = cli.Arguments.Count > 0 ? cli.Arguments[0] : "/";
        int count = 0;
        await foreach (var entry in fs.ListAsync(path, ct))
        {
            count++;
            string size = entry.IsDirectory ? "<dir>" : Format.Bytes(entry.Size);
            Console.WriteLine($"{size,12}  {entry.Modified?.ToLocalTime():yyyy-MM-dd HH:mm}  {entry.Name}{(entry.IsDirectory ? "/" : "")}{(entry.ReadOnly ? "  (read-only)" : "")}");
        }
        Console.WriteLine($"{count} entries in {path}");
        return 0;
    }

    private async Task<int> PullAsync()
    {
        var device = await DeviceAsync();
        string remote = cli.Argument(0, "phone path");
        string local = cli.Argument(1, "PC file");
        await using var fs = await device.OpenFileSystemAsync(cli.Option("root") ?? "internal", ct);
        var stat = await fs.StatAsync(remote, ct) ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{remote} doesn't exist.");
        var watch = Stopwatch.StartNew();
        await using (var file = File.Create(local))
        {
            await fs.ReadAsync(remote, file, ProgressPrinter(stat.Size), ct);
        }
        Console.WriteLine();
        Console.WriteLine($"Pulled {Format.Bytes(new FileInfo(local).Length)} in {Format.Elapsed(watch)} -> {local}");
        return 0;
    }

    private async Task<int> PushAsync()
    {
        var device = await DeviceAsync();
        string local = cli.Argument(0, "PC file");
        string remote = cli.Argument(1, "phone path");
        await using var fs = await device.OpenFileSystemAsync(cli.Option("root") ?? "internal", ct);
        var watch = Stopwatch.StartNew();
        await using (var file = File.OpenRead(local))
        {
            await fs.WriteAsync(remote, file, file.Length, ProgressPrinter(file.Length), ct);
        }
        Console.WriteLine();
        Console.WriteLine($"Pushed {Format.Bytes(new FileInfo(local).Length)} in {Format.Elapsed(watch)} -> {remote}");
        var stat = await fs.StatAsync(remote, ct);
        Console.WriteLine(stat != null ? $"On the phone: {stat.Size} bytes, modified {stat.Modified}" : "Not found after push!");
        return 0;
    }

    private async Task<int> AppsAsync()
    {
        var device = await DeviceAsync();
        var watch = Stopwatch.StartNew();
        var apps = await device.Apps.ListAsync(cli.Flag("system"), ct);
        foreach (var app in apps)
        {
            Console.WriteLine($"{app.Name,-32} {app.Id,-45} {app.Version,-16} {Format.Bytes(app.AppBytes),10} {Format.Bytes(app.DataBytes),10}  {app.Source,-10} {app.InstalledAt?.ToString("yyyy-MM-dd") ?? ""}");
        }
        Console.WriteLine($"{apps.Count} apps in {Format.Elapsed(watch)} (labels not cached yet show the package name; run again to see them)");
        return 0;
    }

    private async Task<int> IconAsync()
    {
        var device = await DeviceAsync();
        string package = cli.Argument(0, "package name");
        string output = cli.Argument(1, "output .png");
        var watch = Stopwatch.StartNew();
        byte[]? png = await device.Apps.GetIconAsync(package, ct);
        if (png == null)
        {
            Console.WriteLine("No icon could be read from the APK.");
            return 1;
        }
        await File.WriteAllBytesAsync(output, png, ct);
        Console.WriteLine($"Wrote {png.Length} bytes in {Format.Elapsed(watch)} -> {output}");
        return 0;
    }

    private async Task<int> InstallAsync()
    {
        var device = await DeviceAsync();
        if (cli.Arguments.Count == 0)
        {
            throw new UsageException("Give the .apk / .apks / .xapk / .apkm file(s).");
        }
        var watch = Stopwatch.StartNew();
        string lastStage = "";
        var progress = new SyncProgress<InstallProgress>(p =>
        {
            if (p.Stage != lastStage)
            {
                if (lastStage.Length > 0) Console.WriteLine();
                lastStage = p.Stage;
            }
            Console.Write($"\r{p.Stage,-10} {(p.Percent is { } percent ? $"{percent,5:0.0}%" : "")}   ");
        });
        await device.Apps.InstallAsync(cli.Arguments.Select(Path.GetFullPath).ToList(), progress, ct);
        Console.WriteLine();
        Console.WriteLine($"Installed in {Format.Elapsed(watch)}.");
        return 0;
    }

    private async Task<int> UninstallAsync()
    {
        var device = await DeviceAsync();
        await device.Apps.UninstallAsync(cli.Argument(0, "package name"), ct);
        Console.WriteLine("Uninstalled.");
        return 0;
    }

    private async Task<int> ExportAsync()
    {
        var device = await DeviceAsync();
        var files = await device.Apps.ExportAsync(cli.Argument(0, "package name"), Path.GetFullPath(cli.Argument(1, "folder")), ct);
        foreach (string file in files)
        {
            Console.WriteLine($"{Format.Bytes(new FileInfo(file).Length),10}  {file}");
        }
        return 0;
    }

    private async Task<int> AlbumsAsync()
    {
        var device = await DeviceAsync();
        await foreach (var album in device.Photos.GetAlbumsAsync(ct))
        {
            Console.WriteLine($"{album.Count,7}  {album.Kind,-12} {album.Id,-28} {album.Name}");
        }
        return 0;
    }

    private async Task<int> PhotosAsync()
    {
        var device = await DeviceAsync();
        int limit = int.TryParse(cli.Option("limit"), out int l) ? l : 50;
        int offset = int.TryParse(cli.Option("offset"), out int o) ? o : 0;
        var watch = Stopwatch.StartNew();
        var page = await device.Photos.GetAssetsAsync(cli.Option("album"), offset, limit, ct);
        foreach (var asset in page.Items)
        {
            string dims = asset.Width is { } w && asset.Height is { } h ? $"{w}x{h}" : "?";
            string duration = asset.DurationSeconds is { } d ? $" {d:0.0}s" : "";
            Console.WriteLine($"{asset.Id,8}  {asset.TakenAt?.ToLocalTime():yyyy-MM-dd HH:mm}  {asset.Kind,-10} {dims,11}{duration}  {Format.Bytes(asset.Size),9}  {(asset.IsFavorite ? "★ " : "")}{asset.FileName}");
        }
        Console.WriteLine($"{page.Items.Count} of {page.Total} in {Format.Elapsed(watch)}");
        return 0;
    }

    private async Task<int> ThumbAsync()
    {
        var device = await DeviceAsync();
        int size = int.TryParse(cli.Option("size"), out int s) ? s : 256;
        var watch = Stopwatch.StartNew();
        byte[]? jpeg = await device.Photos.GetThumbnailAsync(cli.Argument(0, "asset id"), size, ct);
        if (jpeg == null)
        {
            Console.WriteLine("No thumbnail.");
            return 1;
        }
        string output = cli.Argument(1, "output .jpg");
        await File.WriteAllBytesAsync(output, jpeg, ct);
        Console.WriteLine($"Wrote {jpeg.Length} bytes in {Format.Elapsed(watch)} -> {output}");
        return 0;
    }

    private async Task<int> ImportAsync()
    {
        var device = await DeviceAsync();
        string local = cli.Argument(0, "photo or video file");
        await using var file = File.OpenRead(local);
        await device.Photos.ImportAsync(Path.GetFileName(local), file, file.Length, ProgressPrinter(file.Length), ct);
        Console.WriteLine();
        Console.WriteLine($"Imported into {AndroidPhotoLibrary.ImportFolder}.");
        return 0;
    }

    private async Task<int> ScreenshotAsync()
    {
        var device = await DeviceAsync();
        string output = cli.Argument(0, "output .png");
        var watch = Stopwatch.StartNew();
        byte[] png = await device.Actions.TakeScreenshotAsync(ct);
        await File.WriteAllBytesAsync(output, png, ct);
        Console.WriteLine($"Wrote {Format.Bytes(png.Length)} in {Format.Elapsed(watch)} -> {output}");
        return 0;
    }

    private async Task<int> RebootAsync()
    {
        var device = await DeviceAsync();
        var target = (cli.Arguments.Count > 0 ? cli.Arguments[0] : "system").ToLowerInvariant() switch
        {
            "system" => RebootTarget.System,
            "recovery" => RebootTarget.Recovery,
            "bootloader" => RebootTarget.Bootloader,
            var other => throw new UsageException($"Unknown reboot target '{other}'."),
        };
        await device.Actions.RebootAsync(target, ct);
        Console.WriteLine($"Rebooting to {target}.");
        return 0;
    }

    private async Task<int> ShutdownAsync()
    {
        var device = await DeviceAsync();
        await device.Actions.ShutdownAsync(ct);
        Console.WriteLine("Shutting down.");
        return 0;
    }

    private async Task<int> ShellAsync()
    {
        var device = await DeviceAsync();
        var result = await device.Channel.ShellAsync(cli.Argument(0, "command"), ct);
        Console.Write(result.Output);
        Console.Error.Write(result.Error);
        return result.ExitCode;
    }

    /// <summary>Saves every raw output the parsers read, named like the test fixtures.</summary>
    private async Task<int> CaptureAsync()
    {
        var device = await DeviceAsync();
        var profile = device.Profile!;
        string model = string.Concat(profile.Model.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        string folder = Path.GetFullPath(Path.Combine(cli.Argument(0, "folder"), $"android-{profile.AndroidVersion}-{model}".ToLowerInvariant()));
        Directory.CreateDirectory(folder);

        var commands = new (string File, string Command)[]
        {
            ("getprop.txt", "getprop"),
            ("settings-device-name.txt", "settings get global device_name"),
            ("date-z.txt", "date +%z"),
            ("df.txt", "df -k /data /storage/*"),
            ("ls-storage.txt", "ls -1 /storage"),
            ("sm-list-volumes.txt", "sm list-volumes public"),
            ("dumpsys-diskstats.txt", "dumpsys diskstats"),
            ("dumpsys-battery.txt", "dumpsys battery"),
            ("battery-sysfs.txt", BatteryParser.SysfsCommand),
            ("battery-sticky.txt", BatteryParser.StickyCycleCountCommand),
            ("details-script.txt", DetailsBuilder.Script()),
            ("iphonesubinfo.txt", "service call iphonesubinfo 1 s16 com.android.shell"),
            ("pm-list-packages.txt", AndroidAppManager.ListCommand),
            ("pm-list-packages-system.txt", "pm list packages -s"),
            ("pm-list-media-provider.txt", "pm list packages providers.media"),
            ("dumpsys-package.txt", "dumpsys package packages"),
            ("ls-la-sdcard.txt", "ls -la /sdcard/"),
            ("stat-sdcard.txt", FileListingParser.StatListCommand("'/sdcard/'")),
            ("ls-la-dcim-camera.txt", "ls -la /sdcard/DCIM/Camera/"),
            ("stat-dcim-camera.txt", FileListingParser.StatListCommand("'/sdcard/DCIM/Camera/'")),
            ("content-query-media.txt", MediaQuery.Command(profile.Sdk)),
        };
        foreach (var (file, command) in commands)
        {
            var result = await device.Channel.ShellAsync(command, ct);
            await File.WriteAllTextAsync(Path.Combine(folder, file), result.Output, ct);
            Console.WriteLine($"{file,-30} exit {result.ExitCode}, {result.Output.Length,8} chars{(result.Error.Length > 0 ? $"  stderr: {AdbErrors.FirstLine(result.Error)}" : "")}");
        }

        await using (var connection = await AdbConnection.ConnectAsync(provider.Host.EndPoint, ct))
        {
            await connection.SendRequestAsync("host:devices-l", ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "devices-l.txt"), await connection.ReadLengthPrefixedStringAsync(ct) ?? "", ct);
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "README.md"),
            $"Captured with `androidprobe capture` on {DateTime.Now:yyyy-MM-dd} from {profile.Manufacturer} {profile.Model}, Android {profile.AndroidVersion} (API {profile.Sdk}).\n" +
            "Contains personal data (serial, file names, installed apps): review before committing.\n", ct);
        Console.WriteLine($"Saved to {folder}");
        Console.WriteLine("These files hold personal data (serial, file names, apps). Review them before adding to tests/DeviceDeck.Android.Tests/Fixtures/captured/.");
        return 0;
    }

    private async Task StartAsync()
    {
        await provider.StartAsync(ct);
        var status = await provider.GetDriverStatusAsync(ct);
        if (status.State != DriverState.Ready)
        {
            throw new DeviceException(DeviceErrorCodes.DriverMissing, status.Detail ?? $"adb isn't ready ({status.State}).");
        }
    }

    /// <summary>The phone named by --serial, or the only one; waits while it connects or awaits "Allow USB debugging?".</summary>
    private async Task<AndroidDevice> DeviceAsync()
    {
        await StartAsync();
        string? serial = cli.Option("serial");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        bool hinted = false;
        while (true)
        {
            var devices = provider.Devices.Cast<AndroidDevice>().ToList();
            var candidates = serial != null ? devices.Where(d => d.Summary.Id == serial).ToList() : devices;
            if (candidates.Count > 1)
            {
                throw new UsageException("More than one phone is connected; pick one with --serial: " + string.Join(", ", devices.Select(d => d.Summary.Id)));
            }
            var device = candidates.FirstOrDefault();
            if (device?.Summary.State == DeviceState.Ready)
            {
                return device;
            }
            if (device?.Summary.State == DeviceState.Unauthorized && !hinted)
            {
                Console.Error.WriteLine("Waiting: unlock the phone and tap Allow on \"Allow USB debugging?\"...");
                hinted = true;
            }
            if (DateTime.UtcNow > deadline || (device == null && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(50)))
            {
                throw device == null
                    ? new DeviceException(DeviceErrorCodes.NotFound, serial != null ? $"No phone with serial {serial}." : "No phone connected. Run 'androidprobe doctor'.")
                    : new DeviceException(device.Summary.State == DeviceState.Unauthorized ? DeviceErrorCodes.Unauthorized : DeviceErrorCodes.Disconnected,
                        $"The phone stayed {device.Summary.State}.");
            }
            await Task.Delay(250, ct);
        }
    }

    private static void PrintSummary(DeviceSummary s)
    {
        Console.WriteLine($"{s.Id}  {s.State}  {s.Connection}  {s.Name}  [{s.Model}{(s.MarketingName != null ? " / " + s.MarketingName : "")}]  Android {s.OsVersion}{(s.ColorHex != null ? "  colour " + s.ColorHex : "")}");
        Console.WriteLine($"  capabilities: {s.Capabilities}");
    }

    private static IProgress<long> ProgressPrinter(long total) => new SyncProgress<long>(done =>
        Console.Write(total > 0 ? $"\r{done * 100.0 / total,5:0.0}%  {Format.Bytes(done)} / {Format.Bytes(total)}   " : $"\r{Format.Bytes(done)}   "));

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
