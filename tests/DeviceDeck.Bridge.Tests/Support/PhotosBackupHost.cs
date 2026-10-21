using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Imaging;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Settings;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests.Support;

/// <summary>Hub, queue, settings, photo backup and bridge around <see cref="PhotosTestPhone"/>s, in a temp folder.</summary>
internal sealed class PhotosBackupHost : IAsyncDisposable
{
    private PhotosBackupHost(string root, PhotosTestProvider provider, IDeviceProvider[] providers, IImageTranscoder? transcoder, HeicConversion heic)
    {
        Root = root;
        Provider = provider;
        Hub = new DeviceHub(providers) { DriverPollInterval = TimeSpan.Zero };
        Settings = new SettingsStore(Path.Combine(root, "app", "settings.json"));
        Settings.Update(new System.Text.Json.Nodes.JsonObject { ["photoBackupFolder"] = Path.Combine(root, "pc", "Backup") });
        Transcoder = transcoder;
        Heic = heic;
        Store = new SqliteTransferStore(Path.Combine(root, "app", "transfers.db"));
        StartServices();
    }

    public string Root { get; }
    public PhotosTestProvider Provider { get; }
    public DeviceHub Hub { get; }
    public SettingsStore Settings { get; }
    public IImageTranscoder? Transcoder { get; }
    public HeicConversion Heic { get; }
    public SqliteTransferStore Store { get; private set; }
    public PhotoBackupService Backup { get; private set; } = null!;
    public TransferQueue Queue { get; private set; } = null!;
    public MemoryTransport Transport { get; private set; } = null!;
    public BridgeHost Bridge { get; private set; } = null!;

    public string DatabasePath => Path.Combine(Root, "app", "photo-backup.db");

    /// <summary>The destination drive as the backup sees it: plenty of room unless a test says otherwise.</summary>
    public Func<string, PcDiskSpace?> DiskSpace { get; set; } = _ => new PcDiskSpace(500_000_000_000, 1_000_000_000_000);

    /// <summary>Whether a folder's drive is there (a test unplugs it).</summary>
    public Func<string, bool> RootExists { get; set; } = _ => true;

    public static async Task<PhotosBackupHost> StartAsync(PhotosTestPhone phone, IImageTranscoder? transcoder = null,
        HeicConversion heic = HeicConversion.Available, params IDeviceProvider[] more)
    {
        var root = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", "photos-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var provider = new PhotosTestProvider(phone);
        var host = new PhotosBackupHost(root, provider, [provider, .. more], transcoder, heic);
        await host.Hub.StartAsync(CancellationToken.None);
        return host;
    }

    private void StartServices()
    {
        Backup = new PhotoBackupService(DatabasePath, Hub, Settings, Transcoder, Heic,
            diskSpace: folder => DiskSpace(folder), rootExists: folder => RootExists(folder));
        Queue = new TransferQueue(Hub, Store, [.. TransferHandlers.All(Transcoder), Backup.Handler], new CountingKeepAwake(), TestHost.FastQueue());
        Queue.Start();
        Backup.Attach(Queue);
        Transport = new MemoryTransport();
        Bridge = new BridgeHost(Transport, new BridgeServices
        {
            Hub = Hub,
            Transfers = Queue,
            Settings = Settings,
            Shell = new FakeShell(),
            Screenshots = new ScreenshotStore(),
            PhotoBackup = Backup,
        });
    }

    /// <summary>Closes the queue and the backup database and opens them again, as an app restart does.</summary>
    public async Task RestartAsync()
    {
        Bridge.Dispose();
        await Queue.DisposeAsync();
        Backup.Dispose();
        Store.Dispose();
        Store = new SqliteTransferStore(Path.Combine(Root, "app", "transfers.db"));
        StartServices();
    }

    public string Destination(PhotosTestPhone phone) => Path.Combine(Root, "pc", "Backup", phone.Summary.Name);

    /// <summary>Starts a backup and waits for it to finish.</summary>
    public async Task<TransferJob> BackUpAsync(PhotosTestPhone phone, TransferState until = TransferState.Done)
    {
        var job = await Backup.StartAsync(phone.Summary.Id, CancellationToken.None);
        await Wait.ForStateAsync(Queue, job.Id, until);
        await Wait.UntilAsync(() => Backup.Ledger.GetRun(job.Id)?.State != PhotoBackupRunState.Running || until is not (TransferState.Done or TransferState.Failed or TransferState.Cancelled),
            "the run to be closed");
        return Queue.Get(job.Id)!;
    }

    /// <summary>Every file under the phone's backup folder, relative, '/' separated, sorted.</summary>
    public string[] Files(PhotosTestPhone phone)
    {
        var root = Destination(phone);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    public async ValueTask DisposeAsync()
    {
        Bridge.Dispose();
        await Queue.DisposeAsync();
        Backup.Dispose();
        Store.Dispose();
        await Hub.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Turns anything into a small JPEG of a given size, or returns null like a PC without the HEIF codec.</summary>
internal sealed class FakeJpegTranscoder(bool works = true) : IImageTranscoder
{
    public int Calls { get; private set; }

    public async Task<byte[]?> ToJpegAsync(Stream source, int? maxSize, CancellationToken ct)
    {
        Calls++;
        using var copy = new MemoryStream();
        await source.CopyToAsync(copy, ct);
        await source.DisposeAsync(); // as the Windows Runtime wrapper (AsRandomAccessStream) does
        return works ? TestJpeg.Make(3024, 4032, encoderExif: true, payload: BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(copy.ToArray()))) : null;
    }
}
