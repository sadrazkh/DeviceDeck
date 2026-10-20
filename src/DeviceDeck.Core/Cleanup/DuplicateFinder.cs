using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Cleanup;

/// <summary>
/// Exact duplicates in a photo library. Only files of the same size can be equal, so only those are hashed:
/// first the opening bytes (when the phone can hash a prefix), then whole files for what still matches.
/// Two assets are duplicates when size and whole-file hash agree.
/// </summary>
internal static class DuplicateFinder
{
    public sealed record Result(IReadOnlyList<DuplicateGroup> Groups, int HashedCount, long HashedBytes, int Unreadable);

    /// <param name="progress">(bytes hashed, bytes to hash); the total grows when the whole-file pass is planned.</param>
    public static async Task<Result> FindAsync(
        IDevice device,
        IReadOnlyList<PhotoAsset> assets,
        long prefixBytes,
        int maxGroups,
        Action<long, long>? progress,
        CancellationToken ct)
    {
        var sameSize = assets
            .Where(a => !a.InCloudOnly && a.Size > 0)
            .GroupBy(a => a.Size)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();
        if (sameSize.Count == 0)
        {
            return new Result([], 0, 0, 0);
        }

        if (device is IMediaHashSource source)
        {
            try
            {
                return await OnPhoneAsync(source, sameSize, prefixBytes, maxGroups, progress, ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code == DeviceErrorCodes.NotSupported)
            {
                // This phone can't hash after all: fall back to reading the files.
            }
        }

        // No hashing on the phone: read whole originals over USB and hash them here.
        var hashedIds = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        var all = await WholeAsync(new PcHasher(device.Photos), sameSize, 0, progress, hashedIds, unreadable, ct).ConfigureAwait(false);
        return Finish(all.Groups, maxGroups, hashedIds.Count, all.Bytes, unreadable.Count);
    }

    private static async Task<Result> OnPhoneAsync(
        IMediaHashSource source,
        List<List<PhotoAsset>> sameSize,
        long prefixBytes,
        int maxGroups,
        Action<long, long>? progress,
        CancellationToken ct)
    {
        var hashedIds = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        long hashedBytes = 0;

        // Pass 1: the first prefixBytes of each file (small files whole).
        long total = sameSize.Sum(g => g.Sum(a => Math.Min(a.Size, prefixBytes)));
        var ids = sameSize.SelectMany(g => g).Select(a => a.Id).ToList();
        var prefix = await HashAsync(source, ids, prefixBytes, b => progress?.Invoke(b, total), ct).ConfigureAwait(false);
        foreach (var id in ids.Where(id => !prefix.ContainsKey(id)))
        {
            unreadable.Add(id);
        }

        var candidates = sameSize
            .SelectMany(g => g.Where(a => prefix.ContainsKey(a.Id)).GroupBy(a => prefix[a.Id], StringComparer.Ordinal))
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();

        // Files no bigger than the prefix are already hashed whole.
        var settled = candidates.Where(g => g[0].Size <= prefixBytes).ToList();
        var groups = settled.Select(g => (Hash: prefix[g[0].Id], Assets: g)).ToList();
        foreach (var asset in settled.SelectMany(g => g))
        {
            hashedIds.Add(asset.Id);
            hashedBytes += asset.Size;
        }

        // Pass 2: whole files, only where the openings matched.
        var open = candidates.Where(g => g[0].Size > prefixBytes).ToList();
        var whole = await WholeAsync(source, open, total, progress, hashedIds, unreadable, ct).ConfigureAwait(false);
        groups.AddRange(whole.Groups);
        return Finish(groups, maxGroups, hashedIds.Count, hashedBytes + whole.Bytes, unreadable.Count);
    }

    private static async Task<(List<(string Hash, List<PhotoAsset> Assets)> Groups, long Bytes)> WholeAsync(
        IMediaHashSource source,
        List<List<PhotoAsset>> candidates,
        long done,
        Action<long, long>? progress,
        HashSet<string> hashedIds,
        HashSet<string> unreadable,
        CancellationToken ct)
    {
        var groups = new List<(string, List<PhotoAsset>)>();
        if (candidates.Count == 0)
        {
            return (groups, 0);
        }

        long total = done + candidates.Sum(g => g.Sum(a => a.Size));
        var ids = candidates.SelectMany(g => g).Select(a => a.Id).ToList();
        var full = await HashAsync(source, ids, null, b => progress?.Invoke(done + b, total), ct).ConfigureAwait(false);
        long bytes = 0;
        foreach (var group in candidates)
        {
            foreach (var asset in group)
            {
                if (full.ContainsKey(asset.Id))
                {
                    hashedIds.Add(asset.Id);
                    bytes += asset.Size;
                }
                else
                {
                    unreadable.Add(asset.Id);
                }
            }

            groups.AddRange(group
                .Where(a => full.ContainsKey(a.Id))
                .GroupBy(a => full[a.Id], StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => (g.Key, g.ToList())));
        }

        progress?.Invoke(total, total);
        return (groups, bytes);
    }

    private static Task<IReadOnlyDictionary<string, string>> HashAsync(IMediaHashSource source, List<string> ids, long? prefix, Action<long> progress, CancellationToken ct) =>
        source.HashAssetsAsync(ids, prefix, new InlineProgress(progress), ct);

    private static Result Finish(List<(string Hash, List<PhotoAsset> Assets)> groups, int maxGroups, int hashedCount, long hashedBytes, int unreadable)
    {
        var ordered = groups
            .Select(g => new DuplicateGroup(
                g.Hash,
                g.Assets[0].Size,
                g.Assets.OrderBy(a => a.TakenAt ?? DateTimeOffset.MaxValue).ThenBy(a => a.Id, StringComparer.Ordinal).ToList()))
            .OrderByDescending(g => g.ReclaimableBytes)
            .ThenBy(g => g.Hash, StringComparer.Ordinal)
            .Take(maxGroups)
            .ToList();
        return new Result(ordered, hashedCount, hashedBytes, unreadable);
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

/// <summary>The fallback hasher: each original read over USB into SHA-256 on the PC. It can't read only a prefix.</summary>
internal sealed class PcHasher(IPhotoLibrary photos) : IMediaHashSource
{
    public async Task<IReadOnlyDictionary<string, string>> HashAssetsAsync(IReadOnlyList<string> assetIds, long? prefixBytes, IProgress<long>? progress, CancellationToken ct)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long done = 0;
        foreach (var id in assetIds)
        {
            ct.ThrowIfCancellationRequested();
            using var sink = new HashingStream();
            var before = done;
            try
            {
                await photos.ReadOriginalAsync(id, sink, new Relay(b => progress?.Report(before + b)), ct).ConfigureAwait(false);
            }
            catch (DeviceException ex) when (ex.Code is DeviceErrorCodes.PathNotFound or DeviceErrorCodes.PermissionDenied or DeviceErrorCodes.NotSupported or DeviceErrorCodes.NotFound)
            {
                continue; // gone since the listing, or not readable: left out
            }

            done += sink.Length;
            hashes[id] = sink.Finish();
        }

        return hashes;
    }

    private sealed class Relay(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}

/// <summary>A write-only stream that hashes what is written to it.</summary>
internal sealed class HashingStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public string Finish() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        _length += buffer.Length;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
