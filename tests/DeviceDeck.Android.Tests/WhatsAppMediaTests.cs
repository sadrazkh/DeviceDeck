using System.Security.Cryptography;
using DeviceDeck.Android.Tests.Infrastructure;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;
using DeviceDeck.Core.WhatsApp;

namespace DeviceDeck.Android.Tests;

/// <summary>WhatsApp on Android through adb: what is there (never the chats themselves), and a copy of its folders through the queue.</summary>
public sealed class WhatsAppMediaTests : IDisposable
{
    private static readonly CancellationToken None = CancellationToken.None;
    private readonly string _dir = Directory.CreateTempSubdirectory("devicedeck-android-whatsapp-").FullName;

    private static async Task<(AndroidDevice Device, FakeAdbChannel Adb)> PhoneAsync()
    {
        FakeSharedStorage? storage = null;
        var (device, adb) = await DeviceTests.ReadyAsync("android-14-google-pixel-7", fake => storage = new FakeSharedStorage(fake));
        storage!
            .AddFile("/sdcard/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-20261020-WA0001.jpg", Bytes(30_000, 1))
            .AddFile("/sdcard/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Voice Notes/202642/PTT-20261020-WA0002.opus", Bytes(4_000, 2))
            .AddFile("/sdcard/Android/media/com.whatsapp/WhatsApp/Databases/msgstore.db.crypt14", Bytes(50_000, 3))
            .AddFile("/sdcard/Android/media/com.whatsapp/WhatsApp/Databases/msgstore-2026-10-19.1.db.crypt14", Bytes(49_000, 4))
            .AddFile("/sdcard/Android/media/com.whatsapp/WhatsApp/Backups/backup_settings.json.crypt1", Bytes(300, 5))
            .AddFile("/sdcard/Android/media/com.whatsapp/WhatsApp/.Shared/tmp.bin", Bytes(10, 6))
            // WhatsApp Business from before 2021 still at the top of the storage.
            .AddFile("/sdcard/WhatsApp Business/Media/WhatsApp Business Documents/Price list.pdf", Bytes(8_000, 7));
        return (device, adb);
    }

    [Fact]
    public async Task Says_what_whatsapp_keeps_on_the_phone_without_opening_it()
    {
        var (device, adb) = await PhoneAsync();

        var info = await WhatsAppAndroid.GetInfoAsync(device, None);

        Assert.Equal(["com.whatsapp", "com.whatsapp.w4b"], info.Apps.Select(a => a.Package));
        var whatsApp = info.Apps[0];
        Assert.Equal(("/Android/media/com.whatsapp/WhatsApp", true, 2, 34_000L), (whatsApp.Folder, whatsApp.HasFolder, whatsApp.MediaFiles, whatsApp.MediaBytes));
        Assert.Equal(["msgstore-2026-10-19.1.db.crypt14", "msgstore.db.crypt14"], whatsApp.ChatBackups.Select(b => b.Name).Order(StringComparer.Ordinal));
        var business = info.Apps[1];
        Assert.Equal(("/WhatsApp Business", true, (int?)null), (business.Folder, business.HasFolder, business.MediaFiles)); // the old place isn't measured
        Assert.DoesNotContain(adb.Commands, c => c.Contains("msgstore.db.crypt14", StringComparison.Ordinal) && c.Contains("cat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Copies_media_databases_and_backups_into_the_dated_folder()
    {
        var (device, adb) = await PhoneAsync();
        await using var hub = new DeviceHub([new OneDeviceProvider(device)]) { DriverPollInterval = TimeSpan.Zero };
        await hub.StartAsync(None);
        using var store = new SqliteTransferStore(Path.Combine(_dir, "transfers.db"));
        await using var queue = new TransferQueue(hub, store, TransferHandlers.All(), options: new TransferQueueOptions { RetryDelay = TimeSpan.FromMilliseconds(100) });
        queue.Start();
        var folder = WhatsAppAndroid.DatedFolder(_dir, device.Summary.Name, new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(Path.Combine(_dir, $"WhatsApp - {device.Summary.Name} - 2026-10-21"), folder);

        var job = await queue.EnqueueAsync(device.Summary.Id, TransferKind.WhatsAppMedia, new WhatsAppMediaRequest(folder), ConflictRule.Ask, None);
        Assert.Equal($"Backing up WhatsApp media from {device.Summary.Name} to {folder}", job.Title);
        await WaitForAsync(queue, job.Id, TransferState.Done);

        Assert.Equal(6, queue.Get(job.Id)!.DoneItems); // .Shared is WhatsApp's scratch space, not copied
        foreach (var (phone, pc) in new[]
        {
            ("/sdcard/Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Voice Notes/202642/PTT-20261020-WA0002.opus", @"WhatsApp\Media\WhatsApp Voice Notes\202642\PTT-20261020-WA0002.opus"),
            ("/sdcard/Android/media/com.whatsapp/WhatsApp/Databases/msgstore.db.crypt14", @"WhatsApp\Databases\msgstore.db.crypt14"),
            ("/sdcard/Android/media/com.whatsapp/WhatsApp/Backups/backup_settings.json.crypt1", @"WhatsApp\Backups\backup_settings.json.crypt1"),
            ("/sdcard/WhatsApp Business/Media/WhatsApp Business Documents/Price list.pdf", @"WhatsApp Business\Media\WhatsApp Business Documents\Price list.pdf"),
        })
        {
            Assert.Equal(SHA256.HashData(adb.Files[phone]), SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, pc))));
        }
        Assert.False(Directory.Exists(Path.Combine(folder, "WhatsApp", ".Shared")));
        Assert.Empty(Directory.EnumerateFiles(folder, "*.ddpart", SearchOption.AllDirectories));
    }

    private static async Task WaitForAsync(TransferQueue queue, string jobId, TransferState state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (queue.Get(jobId)?.State != state)
        {
            var job = queue.Get(jobId);
            Assert.False(job?.State is TransferState.Failed or TransferState.Cancelled, $"Job ended {job?.State}: {job?.Error?.Message}");
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {state}; job is {job?.State}.");
            await Task.Delay(20);
        }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class OneDeviceProvider(AndroidDevice device) : IDeviceProvider
    {
        public string Id => "android";

        public IReadOnlyList<IDevice> Devices => [device];

        public event EventHandler<DeviceChangedEventArgs>? DeviceChanged { add { } remove { } }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(Id, DriverState.Ready, "test", null));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
