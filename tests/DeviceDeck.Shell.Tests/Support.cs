using System.Runtime.InteropServices;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Fake;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;
using DeviceDeck.Shell.Desktop.DragOut;
using static DeviceDeck.Core.Fake.FakeFileNode;

namespace DeviceDeck.Shell.Tests;

/// <summary>A fake phone, the hub, the transfer queue with the DragOut handler, and a drag-out service, in a temp folder.</summary>
internal sealed class DragHost : IAsyncDisposable
{
    public const string PhoneId = "drag-phone";
    public const string DocsRoot = "app:org.videolan.vlc-ios";
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(3.5));

    private DragHost(string root, FakeDeviceProvider fake, DeviceHub hub, SqliteTransferStore store, TransferQueue queue, DragOutRegistry registry)
    {
        Root = root;
        Fake = fake;
        Hub = hub;
        Store = store;
        Queue = queue;
        Registry = registry;
        Service = new DragOutService(hub, queue, registry, Path.Combine(root, "staging"), action =>
        {
            ThreadPool.QueueUserWorkItem(_ => action());
            return true;
        }, 0, () => 1.0);
    }

    public string Root { get; }
    public FakeDeviceProvider Fake { get; }
    public DeviceHub Hub { get; }
    public SqliteTransferStore Store { get; }
    public TransferQueue Queue { get; }
    public DragOutRegistry Registry { get; }
    public DragOutService Service { get; }

    public IReadOnlyList<PhotoAsset> Photos { get; } = Phone().Assets;

    public static async Task<DragHost> StartAsync(long? bytesPerSecond = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeviceDeckShellTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fake = new FakeDeviceProvider(new FakeOptions
        {
            ArrivalDelay = TimeSpan.Zero,
            BytesPerSecond = bytesPerSecond,
            IndexDuration = TimeSpan.Zero,
            Devices = [Phone()],
        });
        var hub = new DeviceHub([fake]) { DriverPollInterval = TimeSpan.Zero };
        await hub.StartAsync(CancellationToken.None);
        var store = new SqliteTransferStore(Path.Combine(root, "transfers.db"));
        var registry = new DragOutRegistry();
        var queue = new TransferQueue(hub, store, [.. TransferHandlers.All(), new DragOutHandler(registry)], null,
            new TransferQueueOptions { RetryDelay = TimeSpan.FromMilliseconds(100) });
        queue.Start();
        return new DragHost(root, fake, hub, store, queue, registry);
    }

    private static FakeDeviceSpec Phone()
    {
        var assets = Enumerable.Range(0, 3).Select(i => new PhotoAsset(
            $"asset-{i}", i == 2 ? "IMG_0100.HEIC" : $"IMG_{100 + i:D4}.HEIC", PhotoKind.Photo, Base.AddDays(-i), 300_000 + i * 1000L,
            400, 300, null, false, false, null)).ToList();
        var docs = new FakeRoot(new FileRoot(DocsRoot, "VLC", FileRootKind.AppDocuments, "org.videolan.vlc-ios"), Dir("",
            File("Movie.mkv", 300_000, Base),
            Dir("Subtitles", File("Movie.srt", 2_000, Base), Dir("Old", File("Movie.old.srt", 1_500, Base)))));
        return new FakeDeviceSpec
        {
            Summary = new DeviceSummary(PhoneId, "Drag Phone", DevicePlatform.Ios, "Model1,1", "Drag Phone", "26.1", ConnectionKind.Usb,
                DeviceState.Ready, DeviceMode.Full, DeviceCapability.Info | DeviceCapability.PhotosRead | DeviceCapability.Files | DeviceCapability.AppDocuments),
            Storage = new StorageInfo([]),
            Battery = new BatteryInfo(80, false, 95, 100, 30, 4000, 3800),
            Details = new DeviceDetails([]),
            Assets = assets,
            Albums = [new FakeAlbum(new PhotoAlbum("all", "All", AlbumKind.All, assets.Count), [])],
            Roots = [docs],
            Apps = [],
        };
    }

    public async Task<byte[]> OriginalAsync(string assetId)
    {
        using var buffer = new MemoryStream();
        await Fake.Find(PhoneId)!.Photos.ReadOriginalAsync(assetId, buffer, null, CancellationToken.None);
        return buffer.ToArray();
    }

    public async Task<byte[]> PhoneFileAsync(string path)
    {
        await using var fs = await Fake.Find(PhoneId)!.OpenFileSystemAsync(DocsRoot, CancellationToken.None);
        using var buffer = new MemoryStream();
        await fs.ReadAsync(path, buffer, null, CancellationToken.None);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync();
        Store.Dispose();
        await Hub.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
        }
    }
}

/// <summary>Runs work on a fresh STA thread with OLE initialised, pumping messages while it waits (as a drop target's thread would).</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> work, int timeoutMs = 60_000)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Interop.OleInitialize(0);
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeoutMs))
        {
            Assert.Fail("The STA work did not finish.");
        }

        if (error is not null)
        {
            ExceptionDispatchInfoThrow(error);
        }

        return result;
    }

    /// <summary>Pumps this thread's messages until <paramref name="done"/> or the timeout.</summary>
    public static bool PumpUntil(Func<bool> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            while (Interop.PeekMessage(out var message, 0, 0, 0, 1))
            {
                Interop.TranslateMessage(ref message);
                Interop.DispatchMessage(ref message);
            }

            Thread.Sleep(10);
        }

        return true;
    }

    private static void ExceptionDispatchInfoThrow(Exception error) =>
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();

    private static class Interop
    {
        [DllImport("ole32.dll")]
        public static extern int OleInitialize(nint reserved);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PeekMessage(out Msg message, nint window, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TranslateMessage(ref Msg message);

        [DllImport("user32.dll")]
        public static extern nint DispatchMessage(ref Msg message);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }
}
