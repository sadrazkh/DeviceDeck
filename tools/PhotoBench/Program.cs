using System.Diagnostics;
using System.Globalization;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Ios;
using DeviceDeck.Ios.Protocol.Usbmux;

namespace PhotoBench;

/// <summary>
/// photobench: runs DeviceDeck's own photo backup (PhotoBackupService through the transfer queue) against an iPhone and
/// prints photos per minute, MB/s and the connections it opened. Reads from the phone only; writes only into --dest
/// and a state folder next to it. See README.md.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Run photobench --help for the options.");
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            if (!options.Fake)
            {
                return await RunAsync(options, null, cancel.Token);
            }

            using var timers = SimulatedLink.FineTimers();
            var results = new List<Result>();
            for (var run = 1; run <= options.Repeat; run++)
            {
                var label = options.Repeat > 1 ? $"run-{run}" : "run";
                var dest = Path.Combine(options.Dest!, label);
                var fake = FakeIphone.Create(options.Items, options.Seed, options.Link, Path.Combine(options.Dest!, label + ".photobench"),
                    hiddenEvery: options.Hidden is null ? 0 : 12);
                if (run == 1)
                {
                    Console.WriteLine(fake.Description);
                }

                var result = await BenchAsync(options with { Dest = dest }, fake, cancel.Token);
                results.Add(result);
                if (!options.Keep)
                {
                    DeleteBenchOutput(dest);
                }
            }

            if (results.Count > 1)
            {
                var median = results.OrderBy(r => r.Elapsed).ElementAt(results.Count / 2);
                Console.WriteLine($"median of {results.Count}: {median.Line}");
            }

            return results.All(r => r.Failed == 0) ? 0 : 1;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("Stopped.");
            return 130;
        }
    }

    private static async Task<int> RunAsync(Options options, FakeIphone? fake, CancellationToken ct)
    {
        var result = await BenchAsync(options, fake, ct);
        return result.Failed == 0 ? 0 : 1;
    }

    private sealed record Result(TimeSpan Elapsed, int Items, int Failed, string Line);

    private static async Task<Result> BenchAsync(Options options, FakeIphone? fake, CancellationToken ct)
    {
        var dest = Path.GetFullPath(options.Dest!);
        var state = dest.TrimEnd('\\', '/') + ".photobench";
        Directory.CreateDirectory(state);
        var counting = new CountingUsbmux(fake is null ? new TcpUsbmuxConnector() : fake.Usbmux);
        var provider = new IosDeviceProvider(counting, new IosProviderOptions
        {
            CacheDirectory = Path.Combine(state, "cache"),
            LocalPairRecordDirectory = fake is null ? new IosProviderOptions().LocalPairRecordDirectory : Path.Combine(state, "pair"),
            ApplePairRecordDirectory = fake is null ? null : Path.Combine(state, "apple"),
            // Never ask the phone to trust this PC from a benchmark: pair it in DeviceDeck first.
            AutoPair = fake is not null,
        });
        await using var hub = new DeviceHub([provider]) { DriverPollInterval = TimeSpan.Zero };
        var settings = new SettingsStore(Path.Combine(state, "settings.json"));
        using var backup = new PhotoBackupService(Path.Combine(state, "photo-backup.db"), hub, settings, transcoder: null);
        using var store = new SqliteTransferStore(Path.Combine(state, "transfers.db"));
        await using var queue = new TransferQueue(hub, store, [backup.Handler]);

        await hub.StartAsync(ct);
        var device = await WaitForIphoneAsync(hub, options.Udid, TimeSpan.FromSeconds(fake is null ? 60 : 20), ct);
        Console.WriteLine($"phone: {device.Name} ({device.Model}, iOS {device.OsVersion}) · {device.Id}");
        Console.WriteLine($"into: {dest}");
        queue.Start();
        backup.Attach(queue);
        var hidden = options.Hidden ?? PhotoBackupHidden.Exclude;
        backup.Configure(device.Id, dest, "yyyy/MM", convertHeic: false, includeHidden: null, range: options.Range, saveEdited: options.SaveEdited,
            hidden: hidden, hiddenLayout: options.HiddenLayout, hiddenFolder: options.HiddenFolder);
        var effective = backup.EffectiveOptions(hub.GetUsableDevice(device.Id)).Options;
        var hiddenRoot = PhotoBackupHiddenRules.HiddenRoot(dest, effective.Hidden, effective.HiddenLayout, effective.HiddenFolder);
        Console.WriteLine($"hidden: {effective.Hidden.ToString().ToLowerInvariant()}" + (effective.Hidden == PhotoBackupHidden.Exclude ? ""
            : hiddenRoot is null ? ", together with the others" : $", into {hiddenRoot}"));
        if (options.Edits)
        {
            await ReportEditsAsync(hub.GetUsableDevice(device.Id), ct);
        }

        var clock = Stopwatch.StartNew();
        var count = await backup.CountNewAsync(device.Id, ct);
        Console.WriteLine($"new: {count.NewCount:N0} ({count.NewPhotos:N0} photos, {count.NewVideos:N0} videos, {count.NewBytes / 1e9:0.00} GB known) · " +
            $"{count.OutOfRange:N0} before the range · {count.CloudOnly:N0} only in iCloud · {count.HiddenLeftOut:N0} hidden left out · " +
            $"{count.NewHidden:N0} hidden included · {count.NormalLeftOut:N0} not hidden left out · " +
            $"read the photo index in {clock.Elapsed.TotalSeconds:0.0} s");

        var connections = counting.Connections;
        clock.Restart();
        var done = new TaskCompletionSource<TransferJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? jobId = null;
        var lastPrint = Stopwatch.StartNew();
        var lastState = TransferState.Queued;
        queue.JobUpdated += (_, job) =>
        {
            if (job.Id != jobId)
            {
                return;
            }

            var previous = lastState;
            lastState = job.State;
            if (previous == job.State && job.State != TransferState.Running)
            {
                return;
            }

            if (job.State is TransferState.Done or TransferState.Failed or TransferState.Cancelled)
            {
                done.TrySetResult(job);
            }
            else if (job.State != TransferState.Running && job.State != previous)
            {
                // Waiting for the phone (a dropped connection is waited out and the photo tried again), paused, queued.
                Console.WriteLine($"  {clock.Elapsed.TotalSeconds,6:0.0} s  {job.State}{(job.Error is { } e ? $": {e.Message}" : "")}");
            }
            else if (lastPrint.Elapsed > TimeSpan.FromSeconds(5) && job.TotalItems > 0)
            {
                lastPrint.Restart();
                Console.WriteLine($"  {clock.Elapsed.TotalSeconds,6:0.0} s  {job.DoneItems:N0}/{job.TotalItems:N0} · {job.DoneBytes / 1e6 / Math.Max(clock.Elapsed.TotalSeconds, 0.001):0.0} MB/s");
            }
        };

        var started = await backup.StartAsync(device.Id, ct);
        jobId = started.Id;
        if (queue.Get(jobId) is { State: TransferState.Done or TransferState.Failed or TransferState.Cancelled } already)
        {
            done.TrySetResult(already);
        }

        var finished = await done.Task.WaitAsync(ct);
        var elapsed = clock.Elapsed;
        var opened = counting.Connections - connections;
        await WaitForRunAsync(backup, finished.Id, ct);
        var run = backup.Ledger.GetRun(finished.Id);
        var minutes = Math.Max(elapsed.TotalMinutes, 1e-9);
        var line = $"{finished.State.ToString().ToLowerInvariant()}: {finished.DoneItems:N0}/{finished.TotalItems:N0} items " +
            $"({run?.Copied ?? 0:N0} copied, {run?.Duplicates ?? 0:N0} already there, {finished.FailedItems:N0} failed), " +
            $"{finished.DoneBytes / 1e9:0.000} GB in {elapsed.TotalSeconds:0.0} s · {finished.DoneItems / minutes:0} photos/min · " +
            $"{finished.DoneBytes / 1e6 / Math.Max(elapsed.TotalSeconds, 1e-9):0.0} MB/s · {opened:N0} connections opened";
        Console.WriteLine(line);
        if (finished.Error is { } error)
        {
            Console.WriteLine($"  error: {error.Code}: {error.Message}");
        }

        var again = await backup.CountNewAsync(device.Id, ct);
        Console.WriteLine($"second look: {again.NewCount:N0} new");
        if (hiddenRoot is not null)
        {
            // Names only, read from the PC folder: how many files landed in the hidden tree and how many outside it.
            var inHidden = Directory.Exists(hiddenRoot) ? Directory.EnumerateFiles(hiddenRoot, "*", SearchOption.AllDirectories).Count() : 0;
            var all = Directory.Exists(dest) ? Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Count() : 0;
            Console.WriteLine($"files: {inHidden:N0} under {hiddenRoot}, {all - inHidden:N0} elsewhere in {dest}");
        }
        if (hub.TryGetDevice(device.Id, out var phone) && phone.Photos is DeviceDeck.Ios.Device.Photos.IosPhotoLibrary library)
        {
            Console.WriteLine($"originals: {library.OriginalConnectionsOpened} pooled AFC connections opened; reads ahead: {library.ReadsAhead}");
        }
        return new Result(elapsed, finished.TotalItems, finished.FailedItems + (finished.State == TransferState.Done ? 0 : 1), line);
    }

    /// <summary>
    /// --edits: what the backup would find for edited photos, read-only. How many Photos.sqlite marks as edited
    /// (ZHASADJUSTMENTS), and for up to 50 of them whether PhotoData/Mutations/…/Adjustments/FullSizeRender.* is there.
    /// </summary>
    private static async Task ReportEditsAsync(IDevice device, CancellationToken ct)
    {
        if (device.Photos is not IPhotoBackupSource source)
        {
            Console.WriteLine("edits: this phone's library can't say");
            return;
        }

        var assets = await source.GetBackupAssetsAsync(ct);
        var edited = assets.Where(a => a.Edited is not null && !a.InCloudOnly).ToList();
        Console.WriteLine($"edits: {edited.Count:N0} of {assets.Count:N0} assets edited according to Photos.sqlite");
        if (edited.Count == 0)
        {
            return;
        }

        var looked = await source.CompleteAsync(edited.Take(50).ToList(), ct);
        var found = looked.Where(a => a.Edited?.AssetId is not null).ToList();
        Console.WriteLine($"edits: rendered version found for {found.Count} of {looked.Count} looked at");
        foreach (var a in found.Take(5))
        {
            Console.WriteLine($"  found      {a.AssetId} -> {a.Edited!.AssetId} ({a.Edited.Size:N0} bytes)");
        }

        foreach (var a in looked.Where(a => a.Edited?.AssetId is null).Take(5))
        {
            Console.WriteLine($"  not found  {a.AssetId} (stamp {a.Edited!.Stamp})");
        }
    }

    private static async Task<DeviceSummary> WaitForIphoneAsync(DeviceHub hub, string? udid, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var phones = hub.ListDevices().Where(d => d.Platform == DevicePlatform.Ios && (udid is null || d.Id.Equals(udid, StringComparison.OrdinalIgnoreCase))).ToList();
            if (phones.FirstOrDefault(p => p.State == DeviceState.Ready) is { } ready)
            {
                return ready;
            }

            if (deadline.Elapsed > timeout)
            {
                var seen = phones.Count == 0 ? "no iPhone" : string.Join(", ", phones.Select(p => $"{p.Name}: {p.State}"));
                throw new InvalidOperationException($"No unlocked, trusted iPhone after {timeout.TotalSeconds:0} s ({seen}). Unlock it; pair it in DeviceDeck first.");
            }

            await Task.Delay(100, ct);
        }
    }

    private static async Task WaitForRunAsync(PhotoBackupService backup, string jobId, CancellationToken ct)
    {
        for (var i = 0; i < 100 && backup.Ledger.GetRun(jobId)?.State == PhotoBackupRunState.Running; i++)
        {
            await Task.Delay(50, ct);
        }
    }

    /// <summary>Fake mode only: the made-up photos and the state folder this run wrote (they're about a gigabyte).</summary>
    private static void DeleteBenchOutput(string dest)
    {
        foreach (var path in new[] { dest, dest.TrimEnd('\\', '/') + ".photobench" })
        {
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  could not remove {path}: {ex.Message}");
            }
        }
    }
}

/// <summary>Counts every connection to usbmuxd: a lockdown session, a service, a listener.</summary>
internal sealed class CountingUsbmux(IUsbmuxConnector inner) : IUsbmuxConnector
{
    private long _connections;

    public long Connections => Interlocked.Read(ref _connections);

    public Task<UsbmuxClient> ConnectAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _connections);
        return inner.ConnectAsync(ct);
    }
}

internal sealed record Options(
    bool Help,
    bool Fake,
    string? Dest,
    PhotoBackupRange Range,
    string? Udid,
    int Items,
    int Seed,
    int Repeat,
    bool Keep,
    FakeIphone.Link Link,
    bool Edits = false,
    bool SaveEdited = false,
    PhotoBackupHidden? Hidden = null,
    PhotoBackupHiddenLayout? HiddenLayout = null,
    string? HiddenFolder = null)
{
    public const string Usage = """
        photobench — times DeviceDeck's photo backup.

          photobench --since 30d --dest E:\DeviceDeck-test\<name>      a connected iPhone (unlocked, already trusted)
          photobench --fake [--items 240] [--repeat 3]                  a fake iPhone over a simulated USB link

        --since 30d | 2026-09-01 | all   which photos, by the date taken (required with a real phone)
        --dest <folder>                  where the copies go; state (ledger, queue, Photos.sqlite copy) goes next to it in <folder>.photobench
                                         (fake: default E:\DeviceDeck-test\photobench-fake; removed after each run unless --keep)
        --udid <udid>                    which iPhone, when more than one is connected
        fake only:
        --items <n>                      photos and videos on the fake phone (default 240; Live Photos add a video file)
        --seed <n>                       the fake library (default 1)
        --repeat <n>                     runs, each into a fresh folder; prints the median
        --mbps <n>  --rtt-ms <n>  --connect-ms <n>  --session-ms <n>
                                         the simulated link: shared bandwidth (34), round trip (1), opening a usbmux
                                         connection (5) and a lockdown session's TLS handshake (40)
        --keep                           keep the fake run's files
        --edits                          also say, read-only, how many photos Photos.sqlite marks as edited and whether
                                         their rendered versions are where the backup looks
        --save-edited                    back up edited versions too ("IMG_1234 (edited).jpg"), as the panel's switch does
        --hidden exclude|separate|together|only
                                         the Hidden album, as the panel's choice: left out (the default), backed up into a
                                         folder of their own (dated inside), backed up with the others, or backed up alone
                                         (into their own folder unless --hidden-layout together). Fake mode: every 12th
                                         photo is hidden when --hidden is given
        --hidden-layout separate|together  where hidden photos go with --hidden only
        --hidden-folder <name>           the hidden photos' folder under --dest (default Hidden)

        It only reads from the phone. Never point --dest at C: (the copies are real files).
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options(false, false, null, PhotoBackupRange.Everything, null, 240, 1, 1, false, new FakeIphone.Link(34, 1, 5, 40));
        var since = (string?)null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            double Number() => double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
                ? v : throw new ArgumentException($"{args[i]} needs a number.");
            switch (args[i])
            {
                case "-h" or "--help" or "/?": o = o with { Help = true }; break;
                case "--fake": o = o with { Fake = true }; break;
                case "--dest": o = o with { Dest = Next() }; break;
                case "--since": since = Next(); break;
                case "--udid": o = o with { Udid = Next() }; break;
                case "--items": o = o with { Items = Math.Max(1, (int)Number()) }; break;
                case "--seed": o = o with { Seed = (int)Number() }; break;
                case "--repeat": o = o with { Repeat = Math.Max(1, (int)Number()) }; break;
                case "--keep": o = o with { Keep = true }; break;
                case "--edits": o = o with { Edits = true }; break;
                case "--save-edited": o = o with { SaveEdited = true }; break;
                case "--hidden": o = ParseHidden(o, Next()); break;
                case "--hidden-layout": o = o with { HiddenLayout = ParseLayout(Next()) }; break;
                case "--hidden-folder": o = o with { HiddenFolder = PhotoBackupHiddenRules.ValidateFolder(Next()) }; break;
                case "--mbps": o = o with { Link = o.Link with { MegabytesPerSecond = Math.Max(0.1, Number()) } }; break;
                case "--rtt-ms": o = o with { Link = o.Link with { RoundTripMs = Number() } }; break;
                case "--connect-ms": o = o with { Link = o.Link with { ConnectMs = Number() } }; break;
                case "--session-ms": o = o with { Link = o.Link with { SessionMs = Number() } }; break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (o.Help)
        {
            return o;
        }

        o = o with { Range = ParseRange(since) ?? PhotoBackupRange.Everything };
        if (o.Fake)
        {
            return o with { Dest = o.Dest ?? @"E:\DeviceDeck-test\photobench-fake" };
        }

        if (since is null)
        {
            throw new ArgumentException("With a real phone, say which photos: --since 30d (the last 30 days), --since 2026-09-01 or --since all.");
        }

        if (o.Dest is null)
        {
            throw new ArgumentException("--dest is required with a real phone (a folder under E:\\DeviceDeck-test\\).");
        }

        if (Path.GetPathRoot(Path.GetFullPath(o.Dest))?.StartsWith("C:", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ArgumentException("--dest is on C:, which is short of space. Use a folder on another drive, e.g. E:\\DeviceDeck-test\\bench.");
        }

        return o;
    }

    /// <summary>--hidden: the panel's combinations. "separate" and "together" take the Hidden album as well as the others.</summary>
    private static Options ParseHidden(Options o, string text) => text.ToLowerInvariant() switch
    {
        "exclude" => o with { Hidden = PhotoBackupHidden.Exclude },
        "separate" => o with { Hidden = PhotoBackupHidden.Include, HiddenLayout = PhotoBackupHiddenLayout.Separate },
        "together" => o with { Hidden = PhotoBackupHidden.Include, HiddenLayout = PhotoBackupHiddenLayout.Together },
        "only" => o with { Hidden = PhotoBackupHidden.Only, HiddenLayout = o.HiddenLayout ?? PhotoBackupHiddenLayout.Separate },
        _ => throw new ArgumentException($"--hidden takes exclude, separate, together or only; not '{text}'."),
    };

    private static PhotoBackupHiddenLayout ParseLayout(string text) => text.ToLowerInvariant() switch
    {
        "separate" => PhotoBackupHiddenLayout.Separate,
        "together" => PhotoBackupHiddenLayout.Together,
        _ => throw new ArgumentException($"--hidden-layout takes separate or together; not '{text}'."),
    };

    private static PhotoBackupRange? ParseRange(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return PhotoBackupRange.Everything;
        }

        if (text.EndsWith('d') && int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var days))
        {
            return PhotoBackupRange.LastDays(days).Validated();
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return PhotoBackupRange.From(day);
        }

        throw new ArgumentException($"--since takes 30d, a date like 2026-09-01, or all; not '{text}'.");
    }
}
