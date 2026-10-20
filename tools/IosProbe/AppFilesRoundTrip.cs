using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.AppFiles;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;
using DeviceDeck.Ios.Device;

namespace IosProbe;

/// <summary>
/// appfiles-roundtrip: the app-files feature on a real iPhone, through the same transfer queue and handlers as the
/// app. Copies a fresh test folder into an app's Documents, asks about the clash once, measures it, copies it back,
/// compares every byte, deletes only that folder and checks the app's Documents are exactly as before.
/// </summary>
internal static class AppFilesRoundTrip
{
    private const double MB = 1_000_000;

    public static async Task RunAsync(IosDevice device, string bundleId, int bigMegabytes, CancellationToken ct)
    {
        var root = IosDevice.AppRootPrefix + bundleId;
        var work = Directory.CreateTempSubdirectory("devicedeck-roundtrip-").FullName;
        var testName = $"DeviceDeck round trip {DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            var clock = Stopwatch.StartNew();
            var overview = await device.GetAppFilesAsync(ct);
            Console.WriteLine($"App files: {overview.Entries.Count} apps share files ({clock.ElapsedMilliseconds} ms): {string.Join(", ", overview.Entries.Select(e => e.Name))}");
            if (overview.Entries.All(e => e.Folders.All(f => f.RootKey != root)))
                throw new DeviceException(DeviceErrorCodes.NotFound, $"{bundleId} isn't installed or doesn't share files.");

            // What a click in the browser costs: open the root and list it, three times in a row.
            var listings = new List<long>();
            for (var i = 0; i < 3; i++)
            {
                clock.Restart();
                await using (var fs = await device.OpenFileSystemAsync(root, ct))
                {
                    await foreach (var _ in fs.ListAsync("/", ct))
                    {
                    }
                }
                listings.Add(clock.ElapsedMilliseconds);
            }
            Console.WriteLine($"Listing:  {string.Join(" ms, ", listings)} ms (open + list of the top folder, 3 times)");

            var before = await SnapshotAsync(device, root, ct);
            clock.Restart();
            var usage = await device.MeasureAsync(root, ["/"], ct);
            Console.WriteLine($"Before: {usage.Files} files, {usage.Folders} folders, {usage.Bytes / MB:N1} MB (measured in {clock.ElapsedMilliseconds} ms)");
            await using (var fs = await device.OpenFileSystemAsync(root, ct))
            {
                if (await fs.StatAsync("/" + testName, ct) is not null)
                    throw new InvalidOperationException($"'{testName}' is already in the app; nothing was changed.");
            }

            // The PC folder: a large file, a nested folder, an empty folder, Persian names.
            var source = Directory.CreateDirectory(Path.Combine(work, "pc", testName)).FullName;
            var big = Path.Combine(source, "big film.mkv");
            await WriteRandomAsync(big, (long)bigMegabytes * 1024 * 1024, ct);
            Directory.CreateDirectory(Path.Combine(source, "notes", "empty folder"));
            await File.WriteAllTextAsync(Path.Combine(source, "notes", "readme.txt"), "DeviceDeck round trip — آزمایش کپی\n", Encoding.UTF8, ct);
            await WriteRandomAsync(Path.Combine(source, "notes", "data.bin"), 1_048_583, ct);
            await File.WriteAllTextAsync(Path.Combine(source, "فیلم کوتاه.srt"), "1\n00:00:01,000 --> 00:00:02,000\nسلام\n", Encoding.UTF8, ct);
            var totalBytes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

            await using var hub = new DeviceHub([new OneDevice(device)]) { DriverPollInterval = TimeSpan.Zero };
            await hub.StartAsync(ct);
            using var store = new SqliteTransferStore(Path.Combine(work, "transfers.db"));
            await using var queue = new TransferQueue(hub, store, TransferHandlers.All());
            queue.Start();

            // In.
            clock.Restart();
            var copyIn = await queue.EnqueueAsync(device.Udid, TransferKind.CopyToDevice, new CopyToDeviceRequest(root, [source], "/"), ConflictRule.Ask, ct);
            var copyInJob = await WaitAsync(queue, copyIn.Id, TransferState.Done, ct);
            var inSeconds = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"Copy in:  {copyInJob.TotalItems} files, {totalBytes / MB:N1} MB in {inSeconds:N1} s = {totalBytes / MB / inSeconds:N1} MB/s  ({copyIn.Title})");

            // Again: every file clashes; the question comes once, Skip + apply to all answers the rest.
            var decisions = 0;
            queue.JobUpdated += (_, job) =>
            {
                if (job.State == TransferState.NeedsDecision)
                    Interlocked.Increment(ref decisions);
            };
            var again = await queue.EnqueueAsync(device.Udid, TransferKind.CopyToDevice, new CopyToDeviceRequest(root, [source], "/"), ConflictRule.Ask, ct);
            await WaitAsync(queue, again.Id, TransferState.NeedsDecision, ct);
            queue.ResolveConflict(again.Id, ConflictChoice.Skip, applyToAll: true);
            await WaitAsync(queue, again.Id, TransferState.Done, ct);
            Console.WriteLine($"Clash:    asked {decisions} time(s) for {copyInJob.TotalItems} clashing files; Skip + apply to all wrote nothing.");

            var inside = await device.MeasureAsync(root, ["/" + testName], ct);
            Console.WriteLine($"On phone: {inside.Files} files, {inside.Folders} folders, {inside.Bytes:N0} bytes (PC: {totalBytes:N0}).");
            Check(inside.Files == 4 && inside.Folders == 2 && inside.Bytes == totalBytes, "the folder on the phone doesn't match the PC folder");

            // Out, then byte for byte.
            var export = Directory.CreateDirectory(Path.Combine(work, "export")).FullName;
            clock.Restart();
            var copyOut = await queue.EnqueueAsync(device.Udid, TransferKind.CopyToPc, new CopyToPcRequest(root, ["/" + testName], export), ConflictRule.Ask, ct);
            await WaitAsync(queue, copyOut.Id, TransferState.Done, ct);
            var outSeconds = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"Copy out: {totalBytes / MB:N1} MB in {outSeconds:N1} s = {totalBytes / MB / outSeconds:N1} MB/s");
            var back = Path.Combine(export, testName);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var copy = Path.Combine(back, Path.GetRelativePath(source, file));
                var same = File.Exists(copy) && Convert.ToHexString(await HashAsync(file, ct)) == Convert.ToHexString(await HashAsync(copy, ct));
                Check(same, $"{Path.GetRelativePath(source, file)} came back different");
            }
            Check(Directory.Exists(Path.Combine(back, "notes", "empty folder")), "the empty folder didn't come back");
            Console.WriteLine("Compare:  every file came back identical (SHA-256), empty folder included.");

            // The whole app, when it is small enough to copy for a check.
            if (usage.Bytes + totalBytes < 1_000_000_000)
            {
                clock.Restart();
                var whole = await queue.EnqueueAsync(device.Udid, TransferKind.CopyToPc, new CopyToPcRequest(root, ["/"], export), ConflictRule.Ask, ct);
                var wholeJob = await WaitAsync(queue, whole.Id, TransferState.Done, ct);
                var folder = Path.Combine(export, await AppFilesHelpers.RootLabelAsync(device, root, ct) ?? bundleId);
                Console.WriteLine($"Whole app: '{whole.Title}' → {wholeJob.TotalItems} files, {wholeJob.TotalBytes / MB:N1} MB in {clock.Elapsed.TotalSeconds:N1} s, folder '{Path.GetFileName(folder)}' exists: {Directory.Exists(folder)}");
            }
            else
            {
                Console.WriteLine($"Whole app: skipped ({usage.Bytes / MB:N0} MB is more than this check copies).");
            }

            // Delete only what this run added.
            clock.Restart();
            await using (var fs = await device.OpenFileSystemAsync(root, ct))
                await fs.DeleteAsync("/" + testName, recursive: true, ct);
            Console.WriteLine($"Deleted:  /{testName} ({clock.ElapsedMilliseconds} ms)");
            var after = await SnapshotAsync(device, root, ct);
            Check(before.SequenceEqual(after), "the app's Documents are not as they were:\n  " + string.Join("\n  ", before.Except(after).Select(l => "- " + l).Concat(after.Except(before).Select(l => "+ " + l))));
            Console.WriteLine($"After:    {after.Count} entries, identical to before (paths, sizes, dates).");
            Console.WriteLine("Round trip OK.");
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException("Round trip failed: " + what + ".");
    }

    private static async Task<TransferJob> WaitAsync(TransferQueue queue, string jobId, TransferState state, CancellationToken ct)
    {
        while (true)
        {
            var job = queue.Get(jobId) ?? throw new InvalidOperationException("The job vanished.");
            if (job.State == state)
                return job;
            if (job.State is TransferState.Failed or TransferState.Cancelled)
                throw new InvalidOperationException($"The job ended {job.State}: {job.Error?.Code} {job.Error?.Message}");
            await Task.Delay(50, ct);
        }
    }

    /// <summary>Every entry under the root with size and date, sorted.</summary>
    private static async Task<List<string>> SnapshotAsync(IosDevice device, string root, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var fs = await device.OpenFileSystemAsync(root, ct);
        var pending = new Stack<string>(["/"]);
        while (pending.TryPop(out var folder))
        {
            await foreach (var entry in fs.ListAsync(folder, ct))
            {
                lines.Add($"{entry.Path} | {(entry.IsDirectory ? "dir" : entry.Size.ToString("N0"))} | {entry.Modified:O}");
                if (entry.IsDirectory)
                    pending.Push(entry.Path);
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static async Task WriteRandomAsync(string path, long length, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        await using var file = File.Create(path);
        for (long done = 0; done < length;)
        {
            var count = (int)Math.Min(buffer.Length, length - done);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
            await file.WriteAsync(buffer.AsMemory(0, count), ct);
            done += count;
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return await SHA256.HashDataAsync(file, ct);
    }

    /// <summary>The connected phone as a provider of its own, so a hub and a queue can drive it.</summary>
    private sealed class OneDevice(IosDevice device) : IDeviceProvider
    {
        public string Id => "ios";

        public IReadOnlyList<IDevice> Devices => [device];

        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged { add { } remove { } }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, null, null));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
