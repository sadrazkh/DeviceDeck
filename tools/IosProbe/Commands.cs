using System.Buffers.Binary;
using System.Diagnostics;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;

namespace IosProbe;

internal static class Commands
{
    public static async Task<int> RunAsync(Cli cli, CancellationToken ct)
    {
        switch (cli.Command)
        {
            case "doctor":
                return await Doctor.RunAsync(ct);
            case "list":
                return await ListAsync(cli, ct);
            case "watch":
                return await WatchAsync(ct);
            // Round 3: LocalSend (they start the app's own hub and queue).
            case "localsend":
                return await LocalSendCommands.FindAsync(cli, ct);
            case "localsend-send":
                return await LocalSendCommands.SendAsync(cli, ct);
            // Round 4: WhatsApp in a backup on this PC (no phone needed; counts only).
            case "whatsapp":
                return await WhatsAppProbe.RunAsync(cli, ct);
            // Round 4: deleting photos through Windows' camera connection.
            case "wpd-devices":
                return await PhotoDeleteProbe.DevicesAsync(ct);
        }

        if (!DeviceCommands.TryGetValue(cli.Command, out var spec))
            throw new CliException($"Unknown command '{cli.Command}'.");
        if (cli.Positional.Count < spec.Args.Length)
            throw new CliException($"usage: iosprobe {cli.Command} {string.Join(' ', spec.Args.Select(a => $"<{a}>"))}");
        if (spec.NeedsYes && !cli.Has("yes"))
            throw new CliException($"'{cli.Command}' changes the phone; add --yes to confirm.");

        await using var provider = new IosDeviceProvider();
        var device = await ConnectAsync(provider, cli, ct);
        await spec.Run(device, cli, ct);
        return 0;
    }

    private sealed record Spec(string[] Args, Func<IosDevice, Cli, CancellationToken, Task> Run, bool NeedsYes = false);

    /// <summary>Commands that need a Ready phone, with their required arguments (checked before connecting).</summary>
    private static readonly Dictionary<string, Spec> DeviceCommands = new(StringComparer.Ordinal)
    {
        ["info"] = new([], InfoAsync),
        ["localsend-raw"] = new([], (d, c, ct) => LocalSendRaw.RunAsync(d, c.IntOption("port", 53317), c.IntOption("wait-ms", 3000), c.Option("ip"), ct)),
        ["storage"] = new([], (d, _, ct) => StorageAsync(d, ct)),
        ["battery"] = new([], (d, _, ct) => BatteryAsync(d, ct)),
        ["roots"] = new([], (d, _, ct) => RootsAsync(d, ct)),
        ["ls"] = new(["root", "path"], (d, c, ct) => ListFolderAsync(d, c.Positional[0], c.Positional[1], ct)),
        ["docs"] = new(["bundleId"], (d, c, ct) => ListFolderAsync(d, IosDevice.AppRootPrefix + c.Positional[0], c.OptionalArg(1) ?? "/", ct)),
        ["pull"] = new(["root", "path", "dest"], (d, c, ct) => PullAsync(d, c.Positional[0], c.Positional[1], c.Positional[2], ct)),
        ["push"] = new(["root", "local-file", "path"], (d, c, ct) => PushAsync(d, c.Positional[0], c.Positional[1], c.Positional[2], ct)),
        ["mkdir"] = new(["root", "path"], async (d, c, ct) =>
        {
            await using var fs = await d.OpenFileSystemAsync(c.Positional[0], ct);
            await fs.CreateDirectoryAsync(c.Positional[1], ct);
            Console.WriteLine($"Created {c.Positional[0]}:{c.Positional[1]}");
        }),
        ["rm"] = new(["root", "path"], async (d, c, ct) =>
        {
            await using var fs = await d.OpenFileSystemAsync(c.Positional[0], ct);
            var entry = await fs.StatAsync(c.Positional[1], ct)
                        ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{c.Positional[0]}:{c.Positional[1]} does not exist.");
            if (entry.IsDirectory && !c.Has("recursive"))
                throw new CliException("That is a folder; add --recursive to delete it and everything in it.");
            await fs.DeleteAsync(c.Positional[1], c.Has("recursive"), ct);
            var gone = await fs.StatAsync(c.Positional[1], ct) is null;
            Console.WriteLine(gone ? $"Deleted {c.Positional[0]}:{c.Positional[1]}" : "The phone still lists it after the delete.");
        }, NeedsYes: true),
        ["appfiles"] = new([], async (d, _, ct) =>
        {
            foreach (var entry in (await d.GetAppFilesAsync(ct)).Entries)
                Console.WriteLine($"  {entry.Name} ({entry.Id}): {string.Join(", ", entry.Folders.Select(f => f.RootKey))}");
        }),
        ["appfiles-roundtrip"] = new(["bundleId"], (d, c, ct) => AppFilesRoundTrip.RunAsync(d, c.Positional[0], c.IntOption("size-mb", 64), ct), NeedsYes: true),
        ["apps"] = new([], (d, c, ct) => AppsAsync(d, c.Has("system"), ct)),
        ["icon"] = new(["bundleId", "out.png"], (d, c, ct) => IconAsync(d, c.Positional[0], c.Positional[1], ct)),
        ["install"] = new(["app.ipa"], (d, c, ct) => InstallAsync(d, c.Positional[0], ct)),
        ["uninstall"] = new(["bundleId"], (d, c, ct) => UninstallAsync(d, c.Positional[0], ct), NeedsYes: true),
        ["photos"] = new([], (d, c, ct) => PhotosAsync(d, c.IntOption("limit", 50), c.Option("album"), ct)),
        ["cleanup"] = new([], CleanupProbe.RunAsync),
        ["wpd"] = new([], PhotoDeleteProbe.WpdAsync),
        ["photodelete"] = new([], PhotoDeleteProbe.PhotoDeleteAsync),
        ["wpd-scan"] = new([], WpdScan.RunAsync),
        ["values"] = new(["domain"], async (d, c, ct) =>
        {
            var domain = c.Positional[0] == "-" ? null : c.Positional[0];
            if (c.Option("key") is { } key)
            {
                var value = await d.GetLockdownValueAsync(domain, key, ct);
                if (value is DeviceDeck.Ios.Plist.PlistDict dict)
                    Out.Plist(dict);
                else
                    Console.WriteLine($"  {key}: {(value is null ? "(missing)" : value is byte[] b ? $"<{b.Length} bytes>" : value)}");
                return;
            }
            Out.Plist(await d.GetLockdownValuesAsync(domain, ct));
        }),
        ["bench-storage"] = new([], (d, c, ct) => BenchStorage.RunAsync(d, c.Has("browse"), ct)),
        ["bench-overview"] = new([], (d, _, ct) => BenchStorage.OverviewAsync(d, ct)),
        ["afcinfo"] = new([], async (d, _, ct) =>
        {
            foreach (var (k, v) in (await d.GetAfcDeviceInfoAsync(ct)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {k}: {v}");
        }),
        ["carrier"] = new([], async (d, _, ct) =>
        {
            // Bundle ids and network codes only; ICCID and IMSI are left out.
            var values = await d.GetLockdownValuesAsync(null, ct);
            foreach (var entry in values.GetArray("CarrierBundleInfoArray")?.OfType<DeviceDeck.Ios.Plist.PlistDict>() ?? [])
            {
                Console.WriteLine("  SIM:");
                foreach (var key in entry.Keys.Where(k => k is not ("IntegratedCircuitCardIdentity" or "InternationalMobileSubscriberIdentity")).Order())
                    Console.WriteLine($"    {key}: {entry[key]}");
            }
            Console.WriteLine($"  Mapped: {LockdownMapping.Carrier(values) ?? "(none)"}");
        }),
        ["diag"] = new(["request"], async (d, c, ct) => Out.Plist(await d.GetDiagnosticsAsync(c.Positional[0], ct)
            ?? throw new DeviceException(DeviceErrorCodes.NotFound, "The phone returned no diagnostics for that request."))),
        ["ioreg"] = new(["entry-class"], async (d, c, ct) => Out.Plist(await d.GetIORegistryAsync(
                c.Positional[0] == "-" ? null : c.Positional[0], ct, c.Option("name"), c.Option("plane"))
            ?? throw new DeviceException(DeviceErrorCodes.NotFound, "No IORegistry entry of that class."))),
        ["thumb"] = new(["assetId", "out.jpg"], (d, c, ct) => ThumbAsync(d, c.Positional[0], c.Positional[1], c.IntOption("size", 256), ct)),
        ["photo-pull"] = new(["assetId", "dest"], (d, c, ct) => PhotoPullAsync(d, c.Positional[0], c.Positional[1], ct)),
        ["syslog"] = new([], (d, c, ct) => ToolboxCommands.SyslogAsync(d, c.IntOption("seconds", 5), c.IntOption("show", 5), ct)),
        ["crashes"] = new([], (d, c, ct) => ToolboxCommands.CrashesAsync(d, c.IntOption("limit", 10), ct)),
        ["wifisync"] = new([], (d, c, ct) => ToolboxCommands.WifiSyncAsync(d, c.OptionalArg(0), c.Has("yes"), ct)),
        ["reboot"] = new([], async (d, _, ct) =>
        {
            await d.Actions.RebootAsync(RebootTarget.System, ct);
            Console.WriteLine("Restarting the iPhone.");
        }, NeedsYes: true),
        ["shutdown"] = new([], async (d, _, ct) =>
        {
            await d.Actions.ShutdownAsync(ct);
            Console.WriteLine("Shutting the iPhone down.");
        }, NeedsYes: true),
    };

    /// <summary>Starts the provider and waits for the chosen phone to be Ready, telling the user what the phone needs.</summary>
    private static async Task<IosDevice> ConnectAsync(IosDeviceProvider provider, Cli cli, CancellationToken ct)
    {
        await provider.StartAsync(ct);
        var udid = cli.Option("udid");
        var deadline = DateTime.UtcNow.AddSeconds(cli.IntOption("timeout", 30));
        DeviceState? shown = null;
        while (true)
        {
            var devices = provider.Devices.OfType<IosDevice>().ToList();
            IosDevice? device = udid is not null
                ? devices.FirstOrDefault(d => d.Udid.Equals(udid, StringComparison.OrdinalIgnoreCase))
                : devices.Count == 1 ? devices[0] : null;
            if (udid is null && devices.Count > 1)
                throw new CliException("More than one iPhone is connected; pick one with --udid: " + string.Join(", ", devices.Select(d => d.Udid)));

            if (device is not null)
            {
                var state = device.Summary.State;
                if (state == DeviceState.Ready)
                    return device;
                if (state != shown)
                {
                    Console.Error.WriteLine(state switch
                    {
                        DeviceState.AwaitingTrust => $"{device.Summary.Name}: unlock the iPhone and tap Trust (enter the passcode if it asks)…",
                        DeviceState.Locked => $"{device.Summary.Name}: the iPhone is locked; unlock it…",
                        _ => $"Connecting to {device.Udid}…",
                    });
                    shown = state;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                if (device is null)
                    throw provider.LastListenError as DeviceException
                          ?? new DeviceException(DeviceErrorCodes.NotFound, udid is null ? "No iPhone is connected." : $"No iPhone with UDID {udid} is connected.");
                throw device.Summary.State switch
                {
                    DeviceState.AwaitingTrust => new DeviceException(DeviceErrorCodes.TrustRequired, "The iPhone did not trust this PC in time."),
                    DeviceState.Locked => new DeviceException(DeviceErrorCodes.Locked, "The iPhone stayed locked."),
                    _ => device.LastError as DeviceException
                         ?? new DeviceException(DeviceErrorCodes.Protocol, "The iPhone did not finish connecting.", device.LastError),
                };
            }
            await Task.Delay(200, ct);
        }
    }

    private static async Task<int> ListAsync(Cli cli, CancellationToken ct)
    {
        await using var provider = new IosDeviceProvider();
        await provider.StartAsync(ct);
        // Give each phone a moment to finish its handshake.
        var settle = DateTime.UtcNow.AddSeconds(cli.IntOption("timeout", 5));
        while (DateTime.UtcNow < settle && provider.Devices.Any(d => d.Summary.State == DeviceState.Connecting))
            await Task.Delay(200, ct);

        if (provider.LastListenError is { } listenError)
            throw listenError as DeviceException ?? new DeviceException(DeviceErrorCodes.DriverMissing, listenError.Message, listenError);
        var devices = provider.Devices.OfType<IosDevice>().ToList();
        if (devices.Count == 0)
        {
            Console.WriteLine("No iPhone connected. (usbmux is answering; plug a phone in and unlock it.)");
            return 0;
        }
        Out.Table(["UDID", "Name", "Model", "iOS", "Link", "State"],
            devices.Select(d => new[]
            {
                d.Udid, d.Summary.Name, d.Summary.MarketingName ?? d.Summary.Model, d.Summary.OsVersion,
                d.Summary.Connection.ToString(), d.Summary.State + (d.LastError is { } e ? $" ({e.Message})" : ""),
            }));
        return 0;
    }

    private static async Task<int> WatchAsync(CancellationToken ct)
    {
        await using var provider = new IosDeviceProvider();
        provider.DeviceChanged += (_, e) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {e.Change,-8} {e.Summary.Id}  {e.Summary.Name}  {e.Summary.State}  {e.Summary.Connection}  [{e.Summary.Capabilities}]");
        await provider.StartAsync(ct);
        Console.WriteLine("Watching for iPhones; Ctrl+C to stop.");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
        return 0;
    }

    private static async Task InfoAsync(IosDevice device, Cli cli, CancellationToken ct)
    {
        var s = device.Summary;
        Out.Pairs([
            ("Name", s.Name), ("Model", $"{s.MarketingName ?? "?"} ({s.Model})"), ("iOS", s.OsVersion),
            ("Connection", s.Connection.ToString()), ("State", s.State.ToString()), ("Colour", s.ColorHex ?? "-"),
            ("Capabilities", s.Capabilities.ToString()),
        ]);
        Console.WriteLine();
        var details = await device.GetDetailsAsync(ct);
        Out.Pairs(details.Rows.Select(r => (r.Label, r.Sensitive && !cli.Has("reveal") ? Out.Mask(r.Value) : r.Value)));
    }

    private static async Task StorageAsync(IosDevice device, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var info = await device.GetStorageAsync(ct);
        Console.Error.WriteLine($"(storage: {sw.Elapsed.TotalSeconds:0.0} s)");
        foreach (var v in info.Volumes)
        {
            Console.WriteLine($"{v.Label}: {Out.Bytes(v.TotalBytes)} total, {Out.Bytes(v.FreeBytes)} free, {Out.Bytes(v.TotalBytes - v.FreeBytes)} used");
            Out.Table(["Category", "Size", "Share"],
                v.Categories.Select(c => new[] { c.Key, Out.Bytes(c.Bytes), v.TotalBytes > 0 ? $"{100.0 * c.Bytes / v.TotalBytes:0.0}%" : "-" }));
        }
    }

    private static async Task BatteryAsync(IosDevice device, CancellationToken ct)
    {
        var b = await device.GetBatteryAsync(ct);
        Out.Pairs([
            ("Level", b.LevelPercent is { } l ? $"{l}%" : "-"),
            ("Charging", b.IsCharging ? "yes" : "no"),
            ("Health", b.HealthPercent is { } h ? $"{h}%" : "-"),
            ("Cycle count", b.CycleCount?.ToString() ?? "-"),
            ("Design capacity", b.DesignCapacityMah is { } d ? $"{d} mAh" : "-"),
            ("Full charge capacity", b.FullChargeCapacityMah is { } f ? $"{f} mAh" : "-"),
            ("Temperature", b.TemperatureCelsius is { } t ? $"{t:0.0} °C" : "-"),
        ]);
    }

    private static async Task RootsAsync(IosDevice device, CancellationToken ct)
    {
        var roots = await device.GetFileRootsAsync(ct);
        Out.Table(["Root", "Label", "Kind"], roots.Select(r => new[] { r.Key, r.Label, r.Kind.ToString() }));
    }

    private static async Task ListFolderAsync(IosDevice device, string root, string path, CancellationToken ct)
    {
        await using var fs = await device.OpenFileSystemAsync(root, ct);
        var rows = new List<string[]>();
        long total = 0;
        await foreach (var e in fs.ListAsync(path, ct))
        {
            total += e.Size;
            rows.Add([e.IsDirectory ? "d" : "-", e.IsDirectory ? "" : Out.Bytes(e.Size), e.Modified?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "", e.Name]);
        }
        Out.Table(["", "Size", "Modified", "Name"], rows);
        Console.WriteLine($"{rows.Count} item(s), {Out.Bytes(total)} in files, in {root}:{path}");
    }

    private static async Task PullAsync(IosDevice device, string root, string path, string dest, CancellationToken ct)
    {
        await using var fs = await device.OpenFileSystemAsync(root, ct);
        var entry = await fs.StatAsync(path, ct) ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{root}:{path} does not exist.");
        if (entry.IsDirectory)
            throw new CliException("pull copies one file; that path is a folder.");
        var target = Directory.Exists(dest) ? Path.Combine(dest, entry.Name) : dest;
        var sw = Stopwatch.StartNew();
        await using (var file = File.Create(target))
            await fs.ReadAsync(path, file, Out.Progress(entry.Size), ct);
        Out.EndProgress();
        Console.WriteLine($"Copied {Out.Bytes(entry.Size)} to {target} in {sw.Elapsed.TotalSeconds:0.0} s ({Out.Rate(entry.Size, sw.Elapsed)}).");
    }

    private static async Task PushAsync(IosDevice device, string root, string local, string path, CancellationToken ct)
    {
        if (!File.Exists(local))
            throw new CliException($"'{local}' does not exist.");
        await using var fs = await device.OpenFileSystemAsync(root, ct);
        if (await fs.StatAsync(path, ct) is { IsDirectory: true })
            path = path.TrimEnd('/') + "/" + Path.GetFileName(local);
        await using var file = File.OpenRead(local);
        var sw = Stopwatch.StartNew();
        await fs.WriteAsync(path, file, file.Length, Out.Progress(file.Length), ct);
        Out.EndProgress();
        Console.WriteLine($"Copied {Out.Bytes(file.Length)} to {root}:{path} in {sw.Elapsed.TotalSeconds:0.0} s ({Out.Rate(file.Length, sw.Elapsed)}).");
    }

    private static async Task AppsAsync(IosDevice device, bool includeSystem, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var apps = await device.Apps.ListAsync(includeSystem, ct);
        Out.Table(["Name", "Bundle id", "Version", "Source", "App", "Data", "Files"],
            apps.Select(a => new[] { a.Name, a.Id, a.Version, a.Source.ToString(), Out.Bytes(a.AppBytes), Out.Bytes(a.DataBytes), a.FileSharing ? "yes" : "" }));
        Console.WriteLine($"{apps.Count} app(s), {Out.Bytes(apps.Sum(a => a.AppBytes + a.DataBytes))} in total, listed in {sw.Elapsed.TotalSeconds:0.0} s.");
    }

    private static async Task IconAsync(IosDevice device, string bundleId, string output, CancellationToken ct)
    {
        var png = await device.Apps.GetIconAsync(bundleId, ct)
                  ?? throw new DeviceException(DeviceErrorCodes.NotFound, $"SpringBoard has no icon for '{bundleId}'.");
        await File.WriteAllBytesAsync(output, png, ct);
        var size = png.Length > 24 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G'
            ? $"{BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))}×{BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))} px, "
            : "";
        Console.WriteLine($"Saved {output} ({size}{Out.Bytes(png.Length)}).");
    }

    private static async Task InstallAsync(IosDevice device, string ipa, CancellationToken ct)
    {
        string? lastStage = null;
        await device.Apps.InstallAsync([Path.GetFullPath(ipa)], new InlineProgress<InstallProgress>(p =>
        {
            if (p.Stage != lastStage)
            {
                if (lastStage is not null)
                    Console.Error.WriteLine();
                lastStage = p.Stage;
            }
            Console.Error.Write($"\r  {p.Stage,-28} {(p.Percent is { } pct ? $"{pct,5:0.0}%" : "")}");
        }), ct);
        Console.Error.WriteLine();
        Console.WriteLine($"Installed {Path.GetFileName(ipa)}.");
    }

    private static async Task UninstallAsync(IosDevice device, string bundleId, CancellationToken ct)
    {
        await device.Apps.UninstallAsync(bundleId, ct);
        Console.WriteLine($"Removed {bundleId}.");
    }

    private static async Task PhotosAsync(IosDevice device, int limit, string? album, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var library = (IosPhotoLibrary)device.Photos;
        var index = await library.GetIndexAsync(refresh: true, ct);
        Console.WriteLine(index.Mode == PhotoIndexMode.Database
            ? $"Index: Photos.sqlite ({index.SchemaVariant} schema; {index.Note}), {index.Assets.Count - index.HiddenCount} assets + {index.HiddenCount} hidden, built in {sw.Elapsed.TotalSeconds:0.0} s."
            : $"Index: DCIM listing only ({index.FallbackReason}), {index.Assets.Count} assets, built in {sw.Elapsed.TotalSeconds:0.0} s.");
        Console.WriteLine();
        Out.Table(["Album id", "Name", "Kind", "Count"], index.Albums.Select(a => new[] { a.Id, a.Name, a.Kind.ToString(), a.Count.ToString() }));
        Console.WriteLine();
        var page = await device.Photos.GetAssetsAsync(album, 0, limit, ct);
        Out.Table(["Taken", "Kind", "Size", "Pixels", "Flags", "Asset id"],
            page.Items.Select(a => new[]
            {
                a.TakenAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "-",
                a.Kind.ToString(),
                a.InCloudOnly ? "iCloud" : Out.Bytes(a.Size),
                a.Width is { } w && a.Height is { } h ? $"{w}×{h}" : "",
                string.Join(' ', new[] { a.IsFavorite ? "fav" : null, a.DurationSeconds is { } d ? $"{d:0.#}s" : null, a.LivePairId is not null ? "live:" + Path.GetFileName(a.LivePairId) : null }.Where(x => x is not null)),
                a.Id,
            }));
        Console.WriteLine($"Showing {page.Items.Count} of {page.Total} in '{album ?? PhotoIndex.AllAlbum}'.");
    }

    private static async Task ThumbAsync(IosDevice device, string assetId, string output, int size, CancellationToken ct)
    {
        var jpeg = await device.Photos.GetThumbnailAsync(assetId, size, ct);
        if (jpeg is null)
        {
            Console.WriteLine("The phone has no thumbnail file for that asset (GetThumbnailAsync returned null).");
            return;
        }
        await File.WriteAllBytesAsync(output, jpeg, ct);
        var dims = JpegInfo.Size(jpeg);
        Console.WriteLine($"Saved {output} ({(dims is { } d ? $"{d.Width}×{d.Height} px, " : "")}{Out.Bytes(jpeg.Length)}).");
    }

    private static async Task PhotoPullAsync(IosDevice device, string assetId, string dest, CancellationToken ct)
    {
        var target = Directory.Exists(dest) ? Path.Combine(dest, Path.GetFileName(assetId)) : dest;
        var sw = Stopwatch.StartNew();
        long size;
        await using (var file = File.Create(target))
        {
            await device.Photos.ReadOriginalAsync(assetId, file, Out.Progress(null), ct);
            size = file.Length;
        }
        Out.EndProgress();
        Console.WriteLine($"Copied {Out.Bytes(size)} to {target} in {sw.Elapsed.TotalSeconds:0.0} s ({Out.Rate(size, sw.Elapsed)}).");
    }
}

/// <summary>Reports on the calling thread (Progress&lt;T&gt; would post and reorder).</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
