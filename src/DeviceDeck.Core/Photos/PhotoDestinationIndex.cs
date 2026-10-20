using System.IO.Enumeration;
using System.Security.Cryptography;

namespace DeviceDeck.Core.Photos;

/// <summary>
/// The files already in a backup folder (all sub-folders), by size, so a photo that is there already — copied by an
/// earlier run, by another app, or under another name — is recognised: same size, then same SHA-256. Sums of PC files
/// are cached in the ledger by path, size and write time.
/// </summary>
internal sealed class PhotoDestinationIndex
{
    private readonly Dictionary<long, List<string>> _bySize = [];
    private readonly PhotoBackupLedger _ledger;
    private readonly Lock _gate = new();

    private PhotoDestinationIndex(string root, PhotoBackupLedger ledger)
    {
        Root = root;
        _ledger = ledger;
    }

    public string Root { get; }

    public DateTime BuiltUtc { get; private set; }

    public int FileCount { get; private set; }

    /// <summary>Walks the folder once (names and sizes only; nothing is read).</summary>
    public static PhotoDestinationIndex Build(string root, PhotoBackupLedger ledger)
    {
        var index = new PhotoDestinationIndex(root, ledger);
        if (Directory.Exists(root))
        {
            var files = new FileSystemEnumerable<(string Path, long Size)>(root,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.Length),
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && !e.FileName.EndsWith(".ddpart", StringComparison.OrdinalIgnoreCase),
            };
            foreach (var (path, size) in files)
            {
                index.AddPath(path, size);
            }
        }

        index.BuiltUtc = DateTime.UtcNow;
        return index;
    }

    /// <summary>A file in the folder with these bytes, or null. Files of another size are never opened.</summary>
    public async Task<string?> FindAsync(long size, byte[] sha256, CancellationToken ct)
    {
        string[] candidates;
        lock (_gate)
        {
            candidates = _bySize.TryGetValue(size, out var list) ? [.. list] : [];
        }

        foreach (var path in candidates)
        {
            var hash = await HashOfAsync(path, size, ct).ConfigureAwait(false);
            if (hash is not null && hash.AsSpan().SequenceEqual(sha256))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> holds exactly these bytes.</summary>
    public async Task<bool> SameAsync(string path, long size, byte[] sha256, CancellationToken ct)
    {
        var hash = await HashOfAsync(path, size, ct).ConfigureAwait(false);
        return hash is not null && hash.AsSpan().SequenceEqual(sha256);
    }

    /// <summary>Notes a file this backup just wrote, with its sum, so later photos of the run see it.</summary>
    public void Add(string path, long size, byte[] sha256)
    {
        AddPath(path, size);
        var info = new FileInfo(path);
        if (info.Exists)
        {
            _ledger.PutHash(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, sha256);
        }
    }

    private void AddPath(string path, long size)
    {
        lock (_gate)
        {
            if (!_bySize.TryGetValue(size, out var list))
            {
                _bySize[size] = list = [];
            }

            if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(path);
                FileCount++;
            }
        }
    }

    private async Task<byte[]?> HashOfAsync(string path, long size, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size)
        {
            return null;
        }

        var modified = info.LastWriteTimeUtc.Ticks;
        if (_ledger.GetHash(info.FullName, size, modified) is { } cached)
        {
            return cached;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            _ledger.PutHash(info.FullName, size, modified, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // locked or gone: not a match
        }
    }
}

/// <summary>Passes writes through to a file and sums them on the way (SHA-256), so a download is hashed without reading it back.</summary>
internal sealed class HashingWriteStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long Written { get; private set; }

    public byte[] Finish() => _hash.GetHashAndReset();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;

    public override long Position
    {
        get => Written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        _hash.AppendData(buffer);
        Written += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _hash.AppendData(buffer.Span);
        Written += buffer.Length;
    }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

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
