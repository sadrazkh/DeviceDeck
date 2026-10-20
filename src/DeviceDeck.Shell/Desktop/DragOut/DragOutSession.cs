using System.Collections.Concurrent;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Shell.Desktop.DragOut;

/// <summary>One entry of the virtual-file list File Explorer receives: a folder, or a file whose bytes come from the phone.</summary>
/// <param name="RelativePath">Backslash-separated, relative to the drop folder ("Subtitles\Movie.srt").</param>
/// <param name="Source">Asset id or device path; null for a folder.</param>
/// <param name="ItemIndex">The file's item in the DragOut transfer job; -1 for a folder.</param>
internal sealed record DragOutFile(string RelativePath, bool IsDirectory, long? Size, DateTimeOffset? Modified, string? Source, int ItemIndex)
{
    public string Name => RelativePath[(RelativePath.LastIndexOf('\\') + 1)..];
}

/// <summary>The request of a DragOut job, persisted with it. Items are the files only, in the order File Explorer reads them.</summary>
public sealed record DragOutRequest(string SessionId, string Source, string? Root, IReadOnlyList<DragOutRequestItem> Items);

public sealed record DragOutRequestItem(string Source, string Name, long Size);

/// <summary>The drags that are still alive, by session id; the DragOut handler finds its session here.</summary>
internal sealed class DragOutRegistry
{
    private readonly ConcurrentDictionary<string, DragOutSession> _sessions = new(StringComparer.Ordinal);

    public void Add(DragOutSession session) => _sessions[session.Id] = session;

    public DragOutSession? Find(string id) => _sessions.TryGetValue(id, out var session) ? session : null;

    public void Remove(DragOutSession session) => _sessions.TryRemove(new KeyValuePair<string, DragOutSession>(session.Id, session));

    public int Count => _sessions.Count;

    public IReadOnlyList<DragOutSession> All => [.. _sessions.Values];
}

/// <summary>
/// One drag of phone items into File Explorer. Explorer reads each file through a <see cref="VirtualFileStream"/>;
/// the bytes come from a DragOut job in the transfer queue, which copies each file into a staging file first.
/// The job starts when Explorer asks for the first file's contents (that is, on drop). Readers wait for bytes
/// that haven't arrived yet; a failed or cancelled item fails their read, so Explorer shows the error.
/// </summary>
internal sealed class DragOutSession
{
    private sealed class ItemState
    {
        public long Written;
        public bool Done;
        public string? Error;
    }

    private readonly object _gate = new();
    private readonly TransferQueue _queue;
    private readonly DragOutRegistry _registry;
    private readonly Func<IReadOnlyList<DragOutFile>> _resolve;
    private readonly Lazy<IReadOnlyList<DragOutFile>> _files;
    private ItemState[] _items = [];
    private string? _jobId;
    private bool _asyncStarted;
    private bool _operationEnded;
    private bool _jobFinished;
    private bool _dragEnded;
    private bool _closed;

    public DragOutSession(string id, string deviceId, string source, string? root, string stagingFolder,
        TransferQueue queue, DragOutRegistry registry, Func<IReadOnlyList<DragOutFile>> resolve)
    {
        Id = id;
        DeviceId = deviceId;
        Source = source;
        Root = root;
        StagingFolder = stagingFolder;
        _queue = queue;
        _registry = registry;
        _resolve = resolve;
        _files = new Lazy<IReadOnlyList<DragOutFile>>(ResolveFiles, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Id { get; }
    public string DeviceId { get; }
    public string Source { get; }
    public string? Root { get; }
    public string StagingFolder { get; }

    /// <summary>How long a read waits without a single new byte before it gives up (a phone that never came back).</summary>
    public TimeSpan IdleLimit { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>After the job ends, how long to wait for File Explorer's EndOperation before cleaning up anyway.</summary>
    public TimeSpan LingerAfterJob { get; init; } = TimeSpan.FromMinutes(2);

    public string? JobId
    {
        get
        {
            lock (_gate)
            {
                return _jobId;
            }
        }
    }

    /// <summary>Folders and files, folders before what is in them. Resolved once (a dragged phone folder is walked here).</summary>
    public IReadOnlyList<DragOutFile> Files => _files.Value;

    private IReadOnlyList<DragOutFile> ResolveFiles()
    {
        var files = _resolve();
        lock (_gate)
        {
            _items = files.Where(f => !f.IsDirectory).Select(_ => new ItemState()).ToArray();
        }

        return files;
    }

    public string StagingPath(int itemIndex) => Path.Combine(StagingFolder, $"{itemIndex}.part");

    /// <summary>Puts the copy in the transfer queue, once. Called when File Explorer first asks for a file's contents.</summary>
    public void EnsureJob()
    {
        var files = Files;
        lock (_gate)
        {
            if (_jobId is not null || _closed)
            {
                return;
            }
        }

        var request = new DragOutRequest(Id, Source, Root, files.Where(f => !f.IsDirectory)
            .OrderBy(f => f.ItemIndex)
            .Select(f => new DragOutRequestItem(f.Source!, f.Name, f.Size ?? 0))
            .ToArray());
        Directory.CreateDirectory(StagingFolder);
        var job = _queue.EnqueueAsync(DeviceId, TransferKind.DragOut, request, ConflictRule.Replace, CancellationToken.None).GetAwaiter().GetResult();
        lock (_gate)
        {
            _jobId ??= job.Id;
        }
    }

    // ---- writer side (the DragOut handler) ----

    public void Reset(int itemIndex) => Update(itemIndex, s =>
    {
        s.Written = 0;
        s.Done = false;
        s.Error = null;
    });

    public void Report(int itemIndex, long written) => Update(itemIndex, s => s.Written = written);

    public void Complete(int itemIndex, long written) => Update(itemIndex, s =>
    {
        s.Written = written;
        s.Done = true;
    });

    public void Fail(int itemIndex, string message) => Update(itemIndex, s => s.Error = message);

    private void Update(int itemIndex, Action<ItemState> change)
    {
        _ = Files;
        lock (_gate)
        {
            if (itemIndex >= 0 && itemIndex < _items.Length)
            {
                change(_items[itemIndex]);
                Monitor.PulseAll(_gate);
            }
        }
    }

    // ---- reader side (File Explorer, through VirtualFileStream) ----

    /// <summary>
    /// Waits until the item has <paramref name="end"/> bytes or is complete; returns the bytes there are.
    /// Throws <see cref="IOException"/> when the item failed, the drag ended, or nothing arrived for <see cref="IdleLimit"/>.
    /// </summary>
    public long WaitFor(int itemIndex, long end)
    {
        _ = Files;
        lock (_gate)
        {
            if (itemIndex < 0 || itemIndex >= _items.Length)
            {
                throw new IOException("No such file in this drag.");
            }

            var item = _items[itemIndex];
            var lastWritten = item.Written;
            var idleSince = Environment.TickCount64;
            while (true)
            {
                if (item.Error is { } error)
                {
                    throw new IOException(error);
                }

                if (item.Written >= end || item.Done)
                {
                    return item.Written;
                }

                if (_closed)
                {
                    throw new IOException("The copy to File Explorer was stopped.");
                }

                if (item.Written != lastWritten)
                {
                    lastWritten = item.Written;
                    idleSince = Environment.TickCount64;
                }
                else if (Environment.TickCount64 - idleSince > IdleLimit.TotalMilliseconds)
                {
                    throw new IOException("The phone stopped sending this file.");
                }

                Monitor.Wait(_gate, TimeSpan.FromSeconds(1));
            }
        }
    }

    // ---- lifetime ----

    /// <summary>File Explorer took the drop and copies on its own thread (IDataObjectAsyncCapability.StartOperation).</summary>
    public void StartOperation()
    {
        lock (_gate)
        {
            _asyncStarted = true;
        }
    }

    /// <summary>File Explorer is done (or gave up: <paramref name="succeeded"/> false cancels what is left of the job).</summary>
    public void EndOperation(bool succeeded)
    {
        string? cancel;
        lock (_gate)
        {
            _operationEnded = true;
            cancel = !succeeded && !_jobFinished ? _jobId : null;
        }

        if (cancel is not null)
        {
            TryCancel(cancel);
        }

        TryClose();
    }

    /// <summary>DoDragDrop returned. A drop that started no async copy is over (a target that reads synchronously has read).</summary>
    public void DragEnded(bool dropped)
    {
        lock (_gate)
        {
            _dragEnded = true;
            if (!dropped && _jobId is null)
            {
                _operationEnded = true;
            }
        }

        TryClose();
    }

    public void OnJobUpdated(TransferJob job)
    {
        lock (_gate)
        {
            if (job.Id != _jobId || job.State is not (TransferState.Done or TransferState.Failed or TransferState.Cancelled))
            {
                return;
            }

            _jobFinished = true;
            foreach (var item in _items.Where(i => !i.Done && i.Error is null))
            {
                item.Error = job.State == TransferState.Cancelled ? "The copy was cancelled." : job.Error?.Message ?? "The copy failed.";
            }

            Monitor.PulseAll(_gate);
        }

        TryClose();
        _ = Task.Delay(LingerAfterJob).ContinueWith(_ => Close(), TaskScheduler.Default);
    }

    private void TryClose()
    {
        bool close;
        lock (_gate)
        {
            var copyOver = _jobId is null ? _dragEnded : _jobFinished;
            var readerDone = _operationEnded || (_dragEnded && !_asyncStarted);
            close = copyOver && readerDone;
        }

        if (close)
        {
            Close();
        }
    }

    /// <summary>Stops readers, removes the staging folder and forgets the session.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Monitor.PulseAll(_gate);
        }

        _registry.Remove(this);
        try
        {
            if (Directory.Exists(StagingFolder))
            {
                Directory.Delete(StagingFolder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stream still open in File Explorer; the next start clears the staging root.
        }
    }

    private void TryCancel(string jobId)
    {
        try
        {
            _queue.Cancel(jobId);
        }
        catch (ArgumentException)
        {
            // Already cleared from the list.
        }
    }
}
