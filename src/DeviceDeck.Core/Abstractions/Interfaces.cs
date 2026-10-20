namespace DeviceDeck.Core.Abstractions;

// The seam between the host and each provider (iPhone, Android, MTP).
// Frozen for the parallel round: propose changes in your report instead of editing this file.
// A member the device cannot serve throws DeviceException(DeviceErrorCodes.NotSupported);
// the UI decides what to show from DeviceSummary.Capabilities, never by trying.

/// <summary>One per transport. The host starts every registered provider and merges their devices.</summary>
public interface IDeviceProvider : IAsyncDisposable
{
    /// <summary>"ios", "android" or "mtp".</summary>
    string Id { get; }

    /// <summary>Starts watching for devices. Returns once watching has begun; devices arrive through DeviceChanged.</summary>
    Task StartAsync(CancellationToken ct);

    IReadOnlyList<IDevice> Devices { get; }

    event EventHandler<DeviceChangedEventArgs>? DeviceChanged;

    /// <summary>Is the transport usable on this PC (Apple's usbmux service, the adb server)?</summary>
    Task<DriverStatus> GetDriverStatusAsync(CancellationToken ct);
}

public enum DeviceChange { Added, Changed, Removed }

public sealed class DeviceChangedEventArgs(DeviceChange change, DeviceSummary summary) : EventArgs
{
    public DeviceChange Change { get; } = change;
    public DeviceSummary Summary { get; } = summary;
}

public enum DriverState { Ready, Missing, Stopped, Error }

/// <param name="Detail">One plain sentence for the Drivers card when the state is not Ready.</param>
public sealed record DriverStatus(string ProviderId, DriverState State, string? Version, string? Detail);

public interface IDevice
{
    DeviceSummary Summary { get; }

    Task<StorageInfo> GetStorageAsync(CancellationToken ct);
    Task<BatteryInfo> GetBatteryAsync(CancellationToken ct);
    Task<DeviceDetails> GetDetailsAsync(CancellationToken ct);

    /// <summary>iPhone: "media" (AFC) plus one "app:&lt;bundleId&gt;" per file-sharing app. Android: "internal", "sd:&lt;id&gt;".</summary>
    Task<IReadOnlyList<FileRoot>> GetFileRootsAsync(CancellationToken ct);

    /// <summary>Opens one root; dispose it to release the service connection (AFC / house_arrest / sync).</summary>
    Task<IDeviceFileSystem> OpenFileSystemAsync(string rootKey, CancellationToken ct);

    IPhotoLibrary Photos { get; }
    IAppManager Apps { get; }
    IDeviceActions Actions { get; }
}

public enum FileRootKind { Media, AppDocuments, InternalStorage, SdCard }

public sealed record FileRoot(string Key, string Label, FileRootKind Kind, string? AppId = null);

public interface IDeviceFileSystem : IAsyncDisposable
{
    IAsyncEnumerable<FileEntry> ListAsync(string path, CancellationToken ct);

    /// <summary>Null when the path does not exist.</summary>
    Task<FileEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>Copies a file off the phone into <paramref name="destination"/>; progress reports bytes done.</summary>
    Task ReadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Writes <paramref name="source"/> to the phone, replacing what is there; progress reports bytes done.</summary>
    Task WriteAsync(string path, Stream source, long length, IProgress<long>? progress, CancellationToken ct);

    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, bool recursive, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);
}

public interface IPhotoLibrary
{
    IAsyncEnumerable<PhotoAlbum> GetAlbumsAsync(CancellationToken ct);

    /// <summary>Newest first. <paramref name="albumId"/> null = all.</summary>
    Task<Page<PhotoAsset>> GetAssetsAsync(string? albumId, int offset, int limit, CancellationToken ct);

    /// <summary>JPEG bytes, longest side close to <paramref name="size"/>; null when none can be made.</summary>
    Task<byte[]?> GetThumbnailAsync(string assetId, int size, CancellationToken ct);

    Task ReadOriginalAsync(string assetId, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Needs PhotosDelete.</summary>
    Task DeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct);

    /// <summary>Needs PhotosImport (Android only in v1).</summary>
    Task ImportAsync(string fileName, Stream source, long length, IProgress<long>? progress, CancellationToken ct);
}

public interface IAppManager
{
    Task<IReadOnlyList<AppInfo>> ListAsync(bool includeSystem, CancellationToken ct);

    /// <summary>PNG bytes, or null.</summary>
    Task<byte[]?> GetIconAsync(string appId, CancellationToken ct);

    /// <summary>One .ipa, or one or more .apk files of a single app (split APKs).</summary>
    Task InstallAsync(IReadOnlyList<string> localPaths, IProgress<InstallProgress>? progress, CancellationToken ct);

    Task UninstallAsync(string appId, CancellationToken ct);

    /// <summary>Android: writes the app's APK(s) into <paramref name="destinationFolder"/>; returns the files written.</summary>
    Task<IReadOnlyList<string>> ExportAsync(string appId, string destinationFolder, CancellationToken ct);
}

public interface IDeviceActions
{
    Task RebootAsync(RebootTarget target, CancellationToken ct);
    Task ShutdownAsync(CancellationToken ct);

    /// <summary>PNG bytes of the current screen.</summary>
    Task<byte[]> TakeScreenshotAsync(CancellationToken ct);
}
