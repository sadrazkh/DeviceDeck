using System.Buffers.Binary;
using System.Text;
using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.Tests.Fakes;

/// <summary>An in-memory AFC file system served the way the phone's afcd answers.</summary>
internal sealed class FakeAfcServer
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal) { ["/"] = Entry.Dir() };
    private readonly Dictionary<ulong, OpenFile> _open = [];
    private ulong _nextHandle = 1;

    public List<AfcOperation> Operations { get; } = [];

    /// <summary>Paths that answer PermissionDenied.</summary>
    public HashSet<string> Forbidden { get; } = new(StringComparer.Ordinal);

    public int MaxReadSeen { get; private set; }

    /// <summary>Hang up (once) as soon as this many operations have been served in total, like a dying afcd.</summary>
    public int? HangUpAfterOperations { get; set; }

    /// <summary>Hang up (once) on the first FileRead of this path, like a connection dropping mid-copy.</summary>
    public string? HangUpWhileReading { get; set; }

    /// <summary>What FileLock (0x1B, the sync lock) answers; null = not supported, as before.</summary>
    public AfcStatus? LockReply { get; set; }

    public FakeAfcServer AddFile(string path, byte[] content, DateTimeOffset? modified = null)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _entries[path] = Entry.File(content, modified ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        }
        return this;
    }

    /// <summary>A file of <paramref name="length"/> bytes made up on the fly from <paramref name="seed"/> (PhotoBench's big libraries cost no memory).</summary>
    public FakeAfcServer AddGeneratedFile(string path, long length, ulong seed, DateTimeOffset? modified = null)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _entries[path] = Entry.Generated(length, seed, modified ?? new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        }
        return this;
    }

    /// <summary>The bytes of a generated file at <paramref name="offset"/>: the same seed and offset always give the same bytes.</summary>
    public static void Generate(ulong seed, long offset, Span<byte> into)
    {
        Span<byte> block = stackalloc byte[8];
        var position = offset;
        var done = 0;
        while (done < into.Length)
        {
            var index = (ulong)(position >> 3);
            var z = seed + (index + 1) * 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            BinaryPrimitives.WriteUInt64LittleEndian(block, z ^ (z >> 31));
            var from = (int)(position & 7);
            var n = Math.Min(8 - from, into.Length - done);
            block.Slice(from, n).CopyTo(into.Slice(done, n));
            done += n;
            position += n;
        }
    }

    public FakeAfcServer AddDir(string path)
    {
        lock (_gate)
        {
            EnsureParents(path);
            _entries[path] = Entry.Dir();
        }
        return this;
    }

    public byte[]? FileContent(string path)
    {
        lock (_gate)
            return _entries.TryGetValue(path, out var e) && !e.IsDir ? e.Read(0, (int)e.Length) : null;
    }

    public bool Exists(string path)
    {
        lock (_gate)
            return _entries.ContainsKey(path);
    }

    public async Task ServeAsync(Stream s, CancellationToken ct)
    {
        await using var _ = s;
        var header = new byte[AfcPacket.HeaderSize];
        while (true)
        {
            try
            {
                await s.ReadExactlyAsync(header, ct);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            var (entire, thisLength, number, op) = AfcPacket.DecodeHeader(header);
            var headerData = new byte[thisLength - AfcPacket.HeaderSize];
            await s.ReadExactlyAsync(headerData, ct);
            var payload = new byte[entire - thisLength];
            await s.ReadExactlyAsync(payload, ct);

            AfcPacket reply;
            lock (_gate)
            {
                if (HangUpAfterOperations is { } limit && Operations.Count >= limit)
                {
                    HangUpAfterOperations = null;
                    return;
                }
                if (op == AfcOperation.FileRead && HangUpWhileReading is { } hangPath
                    && _open.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(headerData), out var reading) && reading.Path == hangPath)
                {
                    HangUpWhileReading = null;
                    return;
                }
                Operations.Add(op);
                reply = Handle(op, number, headerData, payload);
            }
            await s.WriteAsync(reply.Encode(), ct);
            await s.FlushAsync(ct);
        }
    }

    private AfcPacket Handle(AfcOperation op, ulong number, byte[] h, byte[] payload)
    {
        AfcPacket Status(AfcStatus st) => new(AfcOperation.Status, number, AfcPacket.U64((ulong)st), []);
        AfcPacket Data(byte[] data) => new(AfcOperation.Data, number, [], data);
        string PathAt(int offset) => Encoding.UTF8.GetString(h.AsSpan(offset)).TrimEnd('\0');

        switch (op)
        {
            case AfcOperation.ReadDir:
            {
                var path = Normalize(PathAt(0));
                if (Forbidden.Contains(path)) return Status(AfcStatus.PermissionDenied);
                if (!_entries.TryGetValue(path, out var e)) return Status(AfcStatus.ObjectNotFound);
                if (!e.IsDir) return Status(AfcStatus.InvalidArgument);
                var names = new List<string> { ".", ".." };
                names.AddRange(Children(path).Select(c => c[(c.LastIndexOf('/') + 1)..]));
                return Data(Encoding.UTF8.GetBytes(string.Join('\0', names) + "\0"));
            }
            case AfcOperation.GetFileInfo:
            {
                var path = Normalize(PathAt(0));
                if (!_entries.TryGetValue(path, out var e)) return Status(AfcStatus.ObjectNotFound);
                var ns = (e.Modified - DateTimeOffset.UnixEpoch).Ticks * 100;
                var pairs = new[]
                {
                    "st_size", (e.IsDir ? 64 : e.Length).ToString(),
                    "st_blocks", "8",
                    "st_nlink", "1",
                    "st_ifmt", e.IsDir ? "S_IFDIR" : "S_IFREG",
                    "st_mtime", ns.ToString(),
                    "st_birthtime", ns.ToString(),
                };
                return Data(Encoding.UTF8.GetBytes(string.Join('\0', pairs) + "\0"));
            }
            case AfcOperation.GetDeviceInfo:
                return Data(Encoding.UTF8.GetBytes("Model\0iPhone17,3\0FSTotalBytes\0256000000000\0FSFreeBytes\0100000000000\0FSBlockSize\04096\0"));
            case AfcOperation.MakeDir:
            {
                var path = Normalize(PathAt(0));
                if (!_entries.ContainsKey(Parent(path))) return Status(AfcStatus.ObjectNotFound);
                if (_entries.TryGetValue(path, out var e) && !e.IsDir) return Status(AfcStatus.ObjectExists);
                _entries[path] = Entry.Dir();
                return Status(AfcStatus.Success);
            }
            case AfcOperation.RemovePath:
            {
                var path = Normalize(PathAt(0));
                if (!_entries.ContainsKey(path)) return Status(AfcStatus.ObjectNotFound);
                if (Children(path).Any()) return Status(AfcStatus.DirNotEmpty);
                _entries.Remove(path);
                return Status(AfcStatus.Success);
            }
            case AfcOperation.RemovePathAndContents:
            {
                var path = Normalize(PathAt(0));
                if (!_entries.ContainsKey(path)) return Status(AfcStatus.ObjectNotFound);
                foreach (var key in _entries.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                    _entries.Remove(key);
                return Status(AfcStatus.Success);
            }
            case AfcOperation.RenamePath:
            {
                var parts = Encoding.UTF8.GetString(h).Split('\0');
                var from = Normalize(parts[0]);
                var to = Normalize(parts[1]);
                if (!_entries.ContainsKey(from)) return Status(AfcStatus.ObjectNotFound);
                foreach (var key in _entries.Keys.Where(k => k == from || k.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
                {
                    var e = _entries[key];
                    _entries.Remove(key);
                    _entries[to + key[from.Length..]] = e;
                }
                return Status(AfcStatus.Success);
            }
            case AfcOperation.FileOpen:
            {
                var mode = (AfcFileMode)BinaryPrimitives.ReadUInt64LittleEndian(h);
                var path = Normalize(PathAt(8));
                if (Forbidden.Contains(path)) return Status(AfcStatus.PermissionDenied);
                if (mode == AfcFileMode.ReadOnly)
                {
                    if (!_entries.TryGetValue(path, out var e)) return Status(AfcStatus.ObjectNotFound);
                    if (e.IsDir) return Status(AfcStatus.ObjectIsDir);
                }
                else
                {
                    if (!_entries.ContainsKey(Parent(path))) return Status(AfcStatus.ObjectNotFound);
                    _entries[path] = Entry.File([], DateTimeOffset.UtcNow);
                }
                var handle = _nextHandle++;
                _open[handle] = new OpenFile(path);
                return new AfcPacket(AfcOperation.FileOpenResult, number, AfcPacket.U64(handle), []);
            }
            case AfcOperation.FileRead:
            {
                var handle = BinaryPrimitives.ReadUInt64LittleEndian(h);
                var count = (int)BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8));
                MaxReadSeen = Math.Max(MaxReadSeen, count);
                if (!_open.TryGetValue(handle, out var f)) return Status(AfcStatus.InvalidArgument);
                var entry = _entries[f.Path];
                var n = (int)Math.Max(0, Math.Min(count, entry.Length - f.Position));
                var chunk = entry.Read(f.Position, n);
                f.Position += n;
                return Data(chunk);
            }
            case AfcOperation.FileWrite:
            {
                var handle = BinaryPrimitives.ReadUInt64LittleEndian(h);
                if (!_open.TryGetValue(handle, out var f)) return Status(AfcStatus.InvalidArgument);
                _entries[f.Path].Content.AddRange(payload);
                return Status(AfcStatus.Success);
            }
            case AfcOperation.FileClose:
                _open.Remove(BinaryPrimitives.ReadUInt64LittleEndian(h));
                return Status(AfcStatus.Success);
            case (AfcOperation)0x1B when LockReply is { } lockReply:
                return Status(lockReply);
            default:
                return Status(AfcStatus.OpNotSupported);
        }
    }

    private IEnumerable<string> Children(string dir)
    {
        var prefix = dir == "/" ? "/" : dir + "/";
        return _entries.Keys
            .Where(k => k != dir && k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) < 0)
            .OrderBy(k => k, StringComparer.Ordinal);
    }

    private void EnsureParents(string path)
    {
        var parent = Parent(path);
        while (!_entries.ContainsKey(parent))
        {
            _entries[parent] = Entry.Dir();
            parent = Parent(parent);
        }
    }

    private static string Normalize(string path)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private sealed class Entry
    {
        public bool IsDir { get; private init; }
        public List<byte> Content { get; private init; } = [];
        public DateTimeOffset Modified { get; private init; }
        private long? GeneratedLength { get; init; }
        private ulong Seed { get; init; }

        public long Length => GeneratedLength ?? Content.Count;

        public byte[] Read(long offset, int count)
        {
            var bytes = new byte[count];
            if (GeneratedLength is not null)
                Generate(Seed, offset, bytes);
            else
                Content.CopyTo((int)offset, bytes, 0, count);
            return bytes;
        }

        public static Entry Dir() => new() { IsDir = true, Modified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        public static Entry File(byte[] content, DateTimeOffset modified) => new() { Content = [.. content], Modified = modified };

        public static Entry Generated(long length, ulong seed, DateTimeOffset modified) => new() { GeneratedLength = length, Seed = seed, Modified = modified };
    }

    private sealed class OpenFile(string path)
    {
        public string Path { get; } = path;
        public long Position { get; set; }
    }
}
