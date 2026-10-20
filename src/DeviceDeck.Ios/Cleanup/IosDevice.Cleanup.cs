using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.Device;

// Clean-up (round 2): hashing camera-roll files for the duplicate search. Read-only: files are opened
// read-only over one AFC connection and only as much is read as the pass needs (the first 64 KiB, or all).
// An iPhone has no app-cache trim and its file-sharing apps are walked by the clean-up service itself.
public sealed partial class IosDevice : IMediaHashSource
{
    public Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct) =>
        UseAsync<IReadOnlyDictionary<string, string>>(async c =>
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            long done = 0;
            await using var afc = await c.StartAfcAsync(ct).ConfigureAwait(false);
            foreach (var assetId in assetIds)
            {
                ct.ThrowIfCancellationRequested();
                if (CameraRollPath(assetId) is not { } path)
                    continue;
                var before = done;
                try
                {
                    var (hash, read) = await HashFileAsync(afc, path, prefixBytes, b => progress?.Report(before + b), ct).ConfigureAwait(false);
                    hashes[assetId] = hash;
                    done += read;
                }
                catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PathNotFound or DeviceErrorCodes.PermissionDenied)
                {
                    // Gone since the index was read, or not ours to read: left out.
                }
                progress?.Report(done);
            }
            return hashes;
        });

    /// <summary>SHA-256 of the file, or of its first <paramref name="limit"/> bytes.</summary>
    internal static async Task<(string Hash, long Read)> HashFileAsync(AfcClient afc, string path, long? limit, Action<long>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var handle = await afc.OpenAsync(path, AfcFileMode.ReadOnly, ct).ConfigureAwait(false);
        long read = 0;
        try
        {
            while (limit is not { } max || read < max)
            {
                var want = limit is { } l ? (int)Math.Min(AfcClient.ReadChunk, l - read) : AfcClient.ReadChunk;
                var chunk = await afc.ReadAsync(handle, want, ct).ConfigureAwait(false);
                if (chunk.Length == 0)
                    break;
                hash.AppendData(chunk);
                read += chunk.Length;
                progress?.Invoke(read);
            }
        }
        finally
        {
            try
            {
                await afc.CloseAsync(handle, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeviceException or IOException)
            {
                // The file is read; a failed close only leaks a handle the phone drops with the connection.
            }
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), read);
    }

    /// <summary>The Media-volume path of a camera-roll asset id ("DCIM/100APPLE/IMG_0001.HEIC"), or null when it isn't one.</summary>
    private static string? CameraRollPath(string assetId)
    {
        var id = (assetId ?? "").Replace('\\', '/').TrimStart('/');
        return id.StartsWith("DCIM/", StringComparison.OrdinalIgnoreCase) && !id.Split('/').Any(p => p is ".." or "." or "")
            ? "/" + id
            : null;
    }
}
