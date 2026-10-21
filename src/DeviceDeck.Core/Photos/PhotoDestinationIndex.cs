using System.IO.Enumeration;
using System.Security.Cryptography;

namespace DeviceDeck.Core.Photos;

/// <summary>
/// The files already in a backup folder (all sub-folders), by size, so a photo that is there already — copied by an
/// earlier run, by another app, or under another name — is recognised: same size, then same SHA-256. Sums of PC files
/// are cached in the ledger by path, size and write time; sums found or written during a run wait in memory until the
/// handler saves them with its batch (<see cref="TakeUnsavedHashes"/>). Built once per run of a job.
/// </summary>
internal sealed class PhotoDestinationIndex
{
    private readonly Dictionary<long, List<string>> _bySize = [];
    private readonly Dictionary<string, (long Size, long Modified, byte[] Sha256)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, long Size, long Modified, byte[] Sha256, long? OriginSize, byte[]? OriginSample)> _unsaved = [];
    private readonly Dictionary<string, (long Size, long Modified, byte[] Sample)> _samples = new(StringComparer.OrdinalIgnoreCase);
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
    /// <param name="origin">A JPEG made from a HEIC: the HEIC's size and sample (the next backup finds the JPEG by them).</param>
    public void Add(string path, long size, byte[] sha256, (long Size, byte[] Sample)? origin = null)
    {
        AddPath(path, size);
        var info = new FileInfo(path);
        if (info.Exists)
        {
            Remember(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks, sha256, origin);
        }
    }

    /// <summary>True when the folder has a file of this size whose name, without a clash number, is <paramref name="stem"/>.</summary>
    public bool HasNamesake(long size, string stem)
    {
        lock (_gate)
        {
            return _bySize.TryGetValue(size, out var list) && list.Any(p => SameStem(p, stem));
        }
    }

    /// <summary>A file of this size and name stem whose sample is <paramref name="sample"/>: the photo is there, nothing needs copying.</summary>
    public async Task<string?> FindBySampleAsync(long size, byte[] sample, string stem, CancellationToken ct)
    {
        string[] candidates;
        lock (_gate)
        {
            candidates = _bySize.TryGetValue(size, out var list) ? list.Where(p => SameStem(p, stem)).ToArray() : [];
        }

        foreach (var path in candidates)
        {
            if (await SampleOfAsync(path, size, ct).ConfigureAwait(false) is { } found && found.AsSpan().SequenceEqual(sample))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>A JPEG this PC made from a HEIC with this size and sample, still as written then and with the photo's name.</summary>
    public string? FindConverted(long originSize, byte[] originSample, string stem)
    {
        foreach (var (path, size, modified, sample) in _ledger.FindByOrigin(originSize))
        {
            if (!SameStem(path, stem) || !sample.AsSpan().SequenceEqual(originSample) || !path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var file = new FileInfo(path);
            if (file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == modified)
            {
                return file.FullName;
            }
        }

        return null;
    }

    private static bool SameStem(string path, string stem) =>
        string.Equals(PhotoBackupSample.Stem(Path.GetFileName(path)), stem, StringComparison.OrdinalIgnoreCase);

    private async Task<byte[]?> SampleOfAsync(string path, long size, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != size)
        {
            return null;
        }

        var modified = info.LastWriteTimeUtc.Ticks;
        lock (_gate)
        {
            if (_samples.TryGetValue(info.FullName, out var known) && known.Size == size && known.Modified == modified)
            {
                return known.Sample;
            }
        }

        var sample = await PhotoBackupSample.OfFileAsync(path, size, ct).ConfigureAwait(false);
        if (sample is not null)
        {
            lock (_gate)
            {
                _samples[info.FullName] = (size, modified, sample);
            }
        }

        return sample;
    }

    /// <summary>The sums learnt since the last call, for the ledger.</summary>
    public IReadOnlyList<(string Path, long Size, long Modified, byte[] Sha256, long? OriginSize, byte[]? OriginSample)> TakeUnsavedHashes()
    {
        lock (_gate)
        {
            var taken = _unsaved.ToArray();
            _unsaved.Clear();
            return taken;
        }
    }

    private void Remember(string fullPath, long size, long modified, byte[] sha256, (long Size, byte[] Sample)? origin = null)
    {
        lock (_gate)
        {
            _hashes[fullPath] = (size, modified, sha256);
            _unsaved.Add((fullPath, size, modified, sha256, origin?.Size, origin?.Sample));
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
        lock (_gate)
        {
            if (_hashes.TryGetValue(info.FullName, out var known) && known.Size == size && known.Modified == modified)
            {
                return known.Sha256;
            }
        }

        if (_ledger.GetHash(info.FullName, size, modified) is { } cached)
        {
            return cached;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            Remember(info.FullName, size, modified, hash);
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
