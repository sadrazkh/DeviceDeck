using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.PhotoDelete;

namespace DeviceDeck.Ios.Device;

// Round 4 (photodelete): deleting photos through Windows' camera connection, the way File Explorer does.
// The phone gets PhotosDelete only while that connection shows its camera roll as deletable.

public sealed partial class IosDevice : IPhotoDeleter
{
    private WpdPhotoDeleter? _photoDeleter;
    private volatile bool _photosDeletable;
    private int _photoDeleteCheckRunning;

    internal WpdPhotoDeleter PhotoDeleter => LazyInitializer.EnsureInitialized(ref _photoDeleter, () =>
        new WpdPhotoDeleter(_options.Wpd, Udid, new CameraRoll(this), new PhotoDeleteLog(_options.PhotoDeleteLogDirectory)));

    /// <summary>PhotosDelete while the last check said Windows can delete this phone's photos.</summary>
    private DeviceCapability PhotoDeleteCapability => _photosDeletable ? DeviceCapability.PhotosDelete : DeviceCapability.None;

    public async Task<PhotoDeleteSupport> GetDeleteSupportAsync(bool recheck, CancellationToken ct)
    {
        if (!recheck)
        {
            return PhotoDeleter.Current;
        }

        var support = await PhotoDeleter.CheckAsync(Summary.State, ct).ConfigureAwait(false);
        SetPhotosDeletable(support.CanDelete);
        return support;
    }

    public Task<IReadOnlyList<PhotoDeleteResult>> DeleteConfirmedAsync(IReadOnlyList<PhotoDeleteItem> items, IPhotoDeleteObserver? observer, CancellationToken ct)
    {
        if (!_photosDeletable)
        {
            var current = PhotoDeleter.Current;
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"Photos on this iPhone can't be deleted from the PC right now ({current.Reason}). {current.Detail}".Trim());
        }

        return UseAsync(_ => PhotoDeleter.DeleteAsync(items, observer, ct));
    }

    /// <summary>IPhotoLibrary.DeleteAsync: the ids at the sizes the library lists now; throws at the first one not deleted.</summary>
    internal async Task DeleteThroughWindowsAsync(IReadOnlyList<string> assetIds, CancellationToken ct)
    {
        if (!_photosDeletable)
        {
            await DeleteConfirmedAsync([], null, ct).ConfigureAwait(false); // throws why
        }

        var photos = (IosPhotoLibrary)Photos;
        var index = await photos.GetIndexAsync(refresh: false, ct).ConfigureAwait(false);
        var sizes = index.Assets.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Size, StringComparer.Ordinal);
        var ids = assetIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.FirstOrDefault(id => !sizes.ContainsKey(id)) is { } unknown)
        {
            throw new DeviceException(DeviceErrorCodes.NotFound, $"'{unknown}' is not a photo on this iPhone.");
        }

        // Without Photos.sqlite the index knows no sizes: the file's own size stands in.
        var unsized = ids.Where(id => sizes[id] == 0).ToList();
        var disk = unsized.Count == 0 ? [] : await photos.StatAsync(unsized, ct).ConfigureAwait(false);
        var items = ids.Select(id => new PhotoDeleteItem(id, sizes[id] > 0 ? sizes[id] : disk.GetValueOrDefault(id)?.Size ?? 0)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var results = await DeleteConfirmedAsync(items, null, ct).ConfigureAwait(false);
        if (results.FirstOrDefault(r => r.Outcome != PhotoDeleteOutcome.Deleted) is { } first)
        {
            var done = results.Count(r => r.Outcome == PhotoDeleteOutcome.Deleted);
            throw new DeviceException(first.Code ?? DeviceErrorCodes.Protocol,
                $"{first.FileName}: {first.Message} ({done} of {results.Count} deleted.)");
        }
    }

    /// <summary>Called by UpdateSummary when the state changed: a phone that just became Ready is checked again.</summary>
    partial void OnPhotoDeleteStateChanged(DeviceState before, DeviceState now)
    {
        if (now == DeviceState.Ready && before != DeviceState.Ready)
        {
            StartPhotoDeleteCheck();
        }
        else if (now != DeviceState.Ready)
        {
            _photosDeletable = false;
            PhotoDeleter.Reset(now == DeviceState.Locked ? PhotoDeleteBlock.Locked : PhotoDeleteBlock.Checking);
        }
    }

    /// <summary>
    /// Asks Windows in the background. Windows lists the phone as a camera a few seconds after usbmux does, and shows its
    /// storage only once it is unlocked and trusted, so "not a camera" and "locked" are asked again a few times.
    /// </summary>
    private void StartPhotoDeleteCheck()
    {
        if (Interlocked.Exchange(ref _photoDeleteCheckRunning, 1) == 1)
        {
            return;
        }

        var ct = _life.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var attempt = 1; attempt <= 5 && !ct.IsCancellationRequested; attempt++)
                {
                    var support = await PhotoDeleter.CheckAsync(Summary.State, ct).ConfigureAwait(false);
                    SetPhotosDeletable(support.CanDelete);
                    if (support.Reason is not (PhotoDeleteBlock.NotCamera or PhotoDeleteBlock.Locked) || Summary.State != DeviceState.Ready)
                    {
                        break;
                    }

                    await Task.Delay(_options.PhotoDeleteRetryInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is DeviceException or IOException or InvalidOperationException)
            {
                PhotoDeleter.Reset(PhotoDeleteBlock.NotCamera, ex.Message);
            }
            finally
            {
                Volatile.Write(ref _photoDeleteCheckRunning, 0);
            }
        }, CancellationToken.None);
    }

    private void SetPhotosDeletable(bool deletable)
    {
        if (_photosDeletable == deletable)
        {
            return;
        }

        _photosDeletable = deletable;
        UpdateSummary(Summary.State);
    }

    /// <summary>The phone's own side of a delete: AFC and the photo index.</summary>
    private sealed class CameraRoll(IosDevice device) : IPhoneCameraRoll
    {
        private IosPhotoLibrary Photos => (IosPhotoLibrary)device.Photos;

        public Task<PhotoIndex> GetIndexAsync(bool refresh, CancellationToken ct) => Photos.GetIndexAsync(refresh, ct);

        public PhotoIndex? CachedIndex => Photos.CachedIndex;

        public async Task<IReadOnlyDictionary<string, long?>> StatAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            var stats = await Photos.StatAsync(paths, ct).ConfigureAwait(false);
            return stats.ToDictionary(s => s.Key, s => s.Value is { IsDirectory: false } info ? info.Size : (long?)null, StringComparer.Ordinal);
        }

        public async Task<IReadOnlyList<string>> ListDcimAsync(CancellationToken ct) =>
            await device.UseMediaAfcAsync(afc => IosPhotoLibrary.ListDcimFilesAsync(afc, ct), ct).ConfigureAwait(false);

        public bool? ICloudPhotosHint(PhotoIndex? index) =>
            index?.Assets.Any(a => a.InCloudOnly) == true ? true
                : PhotoDelete.ICloudPhotosHint.Read(Path.Combine(Photos.CacheDirectory, "Photos.sqlite"));
    }
}
