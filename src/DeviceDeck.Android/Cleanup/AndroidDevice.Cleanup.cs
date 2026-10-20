using DeviceDeck.Android.Cleanup;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android;

// The clean-up interfaces (round 2), kept out of AndroidDevice.cs; the work is in Cleanup/AndroidCleanup.cs.
internal sealed partial class AndroidDevice : IMediaHashSource, IAppFileScanner, IAppCacheCleaner
{
    private AndroidCleanup? _cleanup;

    private AndroidCleanup Cleanup
    {
        get
        {
            RequireProfile();
            return _cleanup ??= new AndroidCleanup(Channel, () => RequireProfile().Sdk);
        }
    }

    public Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct) =>
        Cleanup.HashAssetsAsync(assetIds, prefixBytes, progress, ct);

    public Task<AppFileScan> FindLargeAppFilesAsync(long minBytes, int limit, CancellationToken ct) =>
        Cleanup.FindLargeAppFilesAsync(minBytes, limit, ct);

    public Task<AppCacheReport> GetCachesAsync(CancellationToken ct) => Cleanup.GetCachesAsync(ct);

    public Task<long> TrimCachesAsync(CancellationToken ct) => Cleanup.TrimCachesAsync(ct);
}
