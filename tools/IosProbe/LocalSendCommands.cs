using System.Net;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.LocalSend;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Ios;

namespace IosProbe;

/// <summary>
/// Round 3: LocalSend on the iPhone. "localsend" looks for it (over the cable, and on Wi-Fi with --wifi);
/// "localsend-send" sends files through the app's own transfer queue and LocalSend job, printing each phase and
/// each file's result — the same path as "Add to iPhone › Straight into Photos".
/// </summary>
internal static class LocalSendCommands
{
    public static async Task<int> FindAsync(Cli cli, CancellationToken ct)
    {
        await using var session = await OpenAsync(cli, ip: null, ct);
        var found = await LookAsync(session, cli.Has("wifi"), ct);
        Print(found);
        return found.Usb == LocalSendUsbState.Found || found.Wifi.Count > 0 ? 0 : 2;
    }

    public static async Task<int> SendAsync(Cli cli, CancellationToken ct)
    {
        if (cli.Positional.Count == 0)
            throw new CliException("usage: iosprobe localsend-send <file>... --yes [--wifi] [--ip <address>] [--pin <PIN>]");
        if (!cli.Has("yes"))
            throw new CliException("'localsend-send' adds the files to the iPhone's Photos; add --yes to confirm.");
        var files = cli.Positional.Select(Path.GetFullPath).ToList();
        foreach (var file in files.Where(f => !File.Exists(f)))
            throw new CliException($"{file} isn't there.");

        var ip = cli.Option("ip");
        if (ip is not null && !IPAddress.TryParse(ip, out _))
            throw new CliException("--ip must be an IP address.");
        await using var session = await OpenAsync(cli, ip, ct);
        var found = await LookAsync(session, cli.Has("wifi") || ip is not null, ct);
        Print(found);
        var receiver = ip is not null
            ? found.Wifi.FirstOrDefault(r => r.Address == ip)
            : cli.Has("wifi")
                ? found.Wifi.FirstOrDefault(r => r.Likely) ?? (found.Wifi.Count == 1 ? found.Wifi[0] : null)
                : found.UsbReceiver;
        if (receiver is null)
        {
            Console.Error.WriteLine(cli.Has("wifi") && found.Wifi.Count > 1
                ? "More than one LocalSend on Wi-Fi and none looks like this iPhone; pick one with --ip <address>."
                : "LocalSend isn't answering. Open it on the iPhone, keep it in front, and run this again.");
            return 2;
        }

        var service = session.Service;
        var queue = session.Queue;
        var deviceId = session.DeviceId;
        service.Updated += (_, s) =>
            Console.WriteLine($"  phase: {s.Phase}{(s.Error is { } e ? $"  [{e.Code}] {e.Message}" : "")}");
        var lastDone = -1;
        queue.JobUpdated += (_, j) =>
        {
            if (j.Kind == TransferKind.LocalSend && j.DoneItems != lastDone && j.State == TransferState.Running)
            {
                lastDone = j.DoneItems;
                Console.WriteLine($"  sending: {j.DoneItems} of {j.TotalItems} done, {j.DoneBytes:N0} of {j.TotalBytes:N0} bytes");
            }
        };

        Console.WriteLine($"Sending {files.Count} file(s) to «{receiver.Alias}» over {(receiver.Route == LocalSendRoute.Usb ? "USB" : "Wi-Fi " + receiver.Address)}.");
        Console.WriteLine("On the iPhone: keep LocalSend open in front and tap Accept (unless Quick Save is on).");
        var job = await service.SendAsync(deviceId, receiver.ToTarget(), files, ct);
        var final = await WaitAsync(queue, job.Id, ct);
        if (final.State == TransferState.Failed && final.Error?.Code is LocalSendErrors.PinRequired && cli.Option("pin") is { } pin)
        {
            Console.WriteLine("  LocalSend asks for a PIN; sending again with --pin.");
            service.Retry(job.Id, pin);
            final = await WaitAsync(queue, job.Id, ct);
        }

        var status = service.GetStatus(job.Id);
        Console.WriteLine($"Result: {final.State}, {final.DoneItems} of {final.TotalItems} sent{(final.Error is { } error ? $", [{error.Code}] {error.Message}" : "")}");
        foreach (var file in status.Files ?? [])
            Console.WriteLine($"  {file.State,-8} {file.Name}{(file.Error is { } fe ? $"  [{fe.Code}] {fe.Message}" : "")}");
        return final.State == TransferState.Done && final.FailedItems == 0 ? 0 : 2;
    }

    private static async Task<TransferJob> WaitAsync(TransferQueue queue, string jobId, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var job = queue.Get(jobId)!;
                if (job.State is TransferState.Done or TransferState.Failed or TransferState.Cancelled)
                    return job;
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C: cancel the send, which tells LocalSend (/cancel), then report.
            queue.Cancel(jobId);
            for (var i = 0; i < 50 && queue.Get(jobId)!.State is not (TransferState.Cancelled or TransferState.Done or TransferState.Failed); i++)
                await Task.Delay(100, CancellationToken.None);
            throw;
        }
    }

    private static async Task<LocalSendDiscovery> LookAsync(Session session, bool wifi, CancellationToken ct)
    {
        Console.WriteLine($"Looking for LocalSend on {session.DeviceName} over USB{(wifi ? " and Wi-Fi" : "")}…");
        var found = await session.Service.DiscoverAsync(session.DeviceId, wifi, ct);
        while (wifi && found.WifiSearching)
        {
            await Task.Delay(500, ct);
            found = await session.Service.DiscoverAsync(session.DeviceId, wifi, ct);
        }

        return found;
    }

    private static void Print(LocalSendDiscovery found)
    {
        Console.WriteLine($"  USB: {found.Usb}{(found.UsbDetail is { } d ? $" ({d})" : "")}");
        if (found.UsbReceiver is { } usb)
            Console.WriteLine($"    «{usb.Alias}»  model {usb.DeviceModel ?? "?"}  {usb.Protocol}  fingerprint {usb.Fingerprint}");
        foreach (var r in found.Wifi)
            Console.WriteLine($"  Wi-Fi: «{r.Alias}»  {r.Address}:{r.Port}  model {r.DeviceModel ?? "?"}  type {r.DeviceType ?? "?"}  {r.Protocol}{(r.Likely ? "  ← this iPhone?" : "")}");
        if (found.WifiSearchedAt is not null && found.Wifi.Count == 0)
            Console.WriteLine("  Wi-Fi: nothing answered (same network? LocalSend open?)");
    }

    private sealed class Session(IosDeviceProvider provider, DeviceHub hub, SqliteTransferStore store, LocalSendService service, TransferQueue queue,
        string work, IDevice device) : IAsyncDisposable
    {
        public LocalSendService Service { get; } = service;
        public TransferQueue Queue { get; } = queue;
        public string DeviceId => device.Summary.Id;
        public string DeviceName => device.Summary.Name;

        public async ValueTask DisposeAsync()
        {
            await Queue.DisposeAsync();
            await Service.DisposeAsync();
            store.Dispose();
            await hub.DisposeAsync();
            await provider.DisposeAsync();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The app's own pieces around the connected iPhone: hub, a throwaway transfer queue and the LocalSend service.</summary>
    private static async Task<Session> OpenAsync(Cli cli, string? ip, CancellationToken ct)
    {
        var provider = new IosDeviceProvider();
        var hub = new DeviceHub([provider]);
        await hub.StartAsync(ct);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(cli.IntOption("timeout", 30));
        IDevice? device = null;
        while (device is null)
        {
            var udid = cli.Option("udid");
            var ready = hub.ListDevices().Where(d => d.State == DeviceState.Ready && (udid is null || d.Id.Equals(udid, StringComparison.OrdinalIgnoreCase))).ToList();
            if (ready.Count > 1 && udid is null)
                throw new CliException("More than one iPhone is connected; pick one with --udid: " + string.Join(", ", ready.Select(d => d.Id)));
            if (ready.Count == 1)
                device = hub.GetUsableDevice(ready[0].Id);
            else if (DateTime.UtcNow > deadline)
                throw new DeviceException(DeviceErrorCodes.NotFound, "No unlocked, trusted iPhone is connected.");
            else
                await Task.Delay(250, ct);
        }

        // The queue's database is tiny and thrown away afterwards; no photo copies are made on this PC.
        var work = Path.Combine(Path.GetTempPath(), "iosprobe-localsend-" + Guid.NewGuid().ToString("N"));
        var store = new SqliteTransferStore(Path.Combine(work, "transfers.db"));
        var finder = ip is null ? null : new LocalSendFinderOptions { Multicast = false, SweepAddresses = () => [IPAddress.Parse(ip)] };
        var service = new LocalSendService(hub, store.LoadItems, finder: finder);
        var queue = new TransferQueue(hub, store, [service.Handler]);
        queue.Start();
        service.Attach(queue);
        return new Session(provider, hub, store, service, queue, work, device);
    }
}
