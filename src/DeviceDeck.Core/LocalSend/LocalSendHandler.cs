using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;
using static DeviceDeck.Core.Transfers.Handlers.TransferPaths;

namespace DeviceDeck.Core.LocalSend;

/// <summary>
/// The localSend job: PC pictures and videos to the LocalSend app on the phone, which saves images and videos into
/// Photos (its default on iOS and Android) and anything else into its own folder.
/// <para>
/// Each run of the job is one LocalSend session: on the run's first item it asks once for every file the run still has
/// to send (/prepare-upload; the phone shows one Accept for all of them, or none with Quick Save), then each item uploads
/// its file with its token. A PIN, a decline or a busy phone ends the session: every remaining item fails with that code
/// at once, and the job's retry (with the PIN, see <see cref="SetPin"/>) asks again for the files that didn't go.
/// Stopping the run (pause, cancel, an unplugged phone) sends /cancel so the phone isn't left waiting.
/// </para>
/// </summary>
public sealed class LocalSendHandler : TransferHandler<LocalSendRequest>
{
    private const int MaxHashedBytes = 256 << 20;

    private readonly LocalSendIdentity _me;
    private readonly Func<string, IReadOnlyList<TransferItem>> _itemsOf;
    private readonly LocalSendTimeouts? _timeouts;
    private readonly ConcurrentDictionary<string, JobMemory> _jobs = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<TransferWorkContext, RunSession> _runs = new();

    // The session that has each phone's LocalSend now (by device id).
    private readonly ConcurrentDictionary<string, RunSession> _turns = new(StringComparer.Ordinal);

    /// <param name="itemsOf">A job's items as the queue last saved them (SqliteTransferStore.LoadItems): the files a new session asks for.</param>
    public LocalSendHandler(LocalSendIdentity me, Func<string, IReadOnlyList<TransferItem>> itemsOf, LocalSendTimeouts? timeouts = null)
    {
        _me = me;
        _itemsOf = itemsOf;
        _timeouts = timeouts;
    }

    public override TransferKind Kind => TransferKind.LocalSend;

    /// <summary>The run's phase changed (connecting, waiting for Accept, sending).</summary>
    public event EventHandler<string>? PhaseChanged;

    /// <summary>The PIN LocalSend shows on the phone, for the job's next run (kept in memory only).</summary>
    public void SetPin(string jobId, string? pin) => Memory(jobId).Pin = string.IsNullOrWhiteSpace(pin) ? null : pin.Trim();

    /// <summary>What the handler knows of a job beyond the queue: its live phase, who received it, the error that ended its session.</summary>
    public (LocalSendPhase? Phase, string? ReceiverAlias, bool PinGiven, TransferError? SessionError) Peek(string jobId) =>
        _jobs.TryGetValue(jobId, out var memory) ? (memory.Phase, memory.ReceiverAlias, memory.Pin is not null, memory.SessionError) : (null, null, false, null);

    /// <summary>Forgets a finished job's PIN and phase.</summary>
    public void Forget(string jobId) => _jobs.TryRemove(jobId, out _);

    protected override Task<string> DescribeAsync(IDevice device, LocalSendRequest request, CancellationToken ct)
    {
        RequireList(request.Files, "photo");
        foreach (var file in request.Files)
        {
            if (!Path.IsPathFullyQualified(file))
            {
                throw new ArgumentException("Each file must be a full path on this PC.");
            }
        }

        var target = request.Target ?? throw new ArgumentException("target is required.");
        if (target.Port is < 1 or > 65535)
        {
            throw new ArgumentException("target.port is out of range.");
        }

        if (target.Route == LocalSendRoute.Usb && device is not IDevicePortTunnel)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, $"{device.Summary.Name} can't be reached over its cable; use Wi-Fi.");
        }

        if (target.Route == LocalSendRoute.Wifi && !IPAddress.TryParse(target.Address, out _))
        {
            throw new ArgumentException("A Wi-Fi target needs the receiver's IP address.");
        }

        if (target.Https && string.IsNullOrWhiteSpace(target.Fingerprint))
        {
            throw new ArgumentException("An HTTPS target needs the fingerprint found for it.");
        }

        var what = request.Files.Count == 1 ? Path.GetFileName(request.Files[0].TrimEnd('\\', '/')) : Plural(request.Files.Count, "item", "items");
        return Task.FromResult($"Sending {what} to {device.Summary.Name} with LocalSend");
    }

    protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, LocalSendRequest request, CancellationToken ct)
    {
        var plans = new List<TransferItemPlan>();
        foreach (var local in request.Files)
        {
            var trimmed = local.TrimEnd('\\', '/');
            if (Directory.Exists(trimmed))
            {
                // A folder brings its pictures and videos (what LocalSend saves into Photos).
                foreach (var file in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories)
                             .Where(LocalSendMime.IsMedia)
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    plans.Add(Plan(file));
                }
            }
            else if (File.Exists(trimmed))
            {
                plans.Add(Plan(trimmed));
            }
            else
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{Path.GetFileName(trimmed)} isn't on this PC any more.", new FileNotFoundException(local));
            }
        }

        return Task.FromResult<IReadOnlyList<TransferItemPlan>>(plans);
    }

    private static TransferItemPlan Plan(string file)
    {
        var name = Path.GetFileName(file);
        return new TransferItemPlan(file, "localsend:" + name, name, new FileInfo(file).Length);
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, LocalSendRequest request, TransferItem item, IProgress<long> progress, CancellationToken ct)
    {
        var memory = Memory(context.JobId);
        var run = _runs.GetValue(context, c => new RunSession(this, c, request, memory));
        var session = await run.EnsureAsync(item, ct).ConfigureAwait(false);
        var fileId = FileId(item);
        if (!session.Asked.Contains(fileId))
        {
            // Missing from the PC when the session was asked for.
            throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{item.Name} isn't on this PC any more.");
        }

        string? token = null;
        if (session.Session is null || !session.Session.Files.TryGetValue(fileId, out token))
        {
            // 204 (the receiver needs nothing), or it left this file out (it already has it, say).
            run.Finished(fileId, failed: false);
            return TransferItemState.Skipped;
        }

        try
        {
            await using var source = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            item.Size = source.Length;
            Publish(memory, context.JobId, LocalSendPhase.Sending);
            await session.Client.UploadAsync(session.Session!.SessionId, fileId, token, source, source.Length, progress, ct).ConfigureAwait(false);
            run.Finished(fileId, failed: false);
            return TransferItemState.Done;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Pause or cancel: the token's registration tells the phone. The item runs again in the next session.
            throw;
        }
        catch (DeviceException ex) when (TransferQueue.IsDeviceUnavailable(ex))
        {
            await run.AbortAsync().ConfigureAwait(false);
            throw;
        }
        catch (DeviceException ex) when (LocalSendErrors.EndsSession(ex.Code))
        {
            run.Fail(ex);
            run.Finished(fileId, failed: true);
            throw;
        }
        catch (Exception)
        {
            run.Finished(fileId, failed: true);
            throw;
        }
    }

    private static string FileId(TransferItem item) => item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private JobMemory Memory(string jobId) => _jobs.GetOrAdd(jobId, _ => new JobMemory());

    private void Publish(JobMemory memory, string jobId, LocalSendPhase phase)
    {
        if (memory.Phase == phase)
        {
            return;
        }

        memory.Phase = phase;
        PhaseChanged?.Invoke(this, jobId);
    }

    private static async Task<LocalSendFile> DescribeFileAsync(TransferItem item, string fileId, CancellationToken ct)
    {
        var info = new FileInfo(item.Source);
        string? sha = null;
        if (info.Length <= MaxHashedBytes)
        {
            // LocalSend checks it and answers 422 when the bytes that arrived differ. A big video would hold up the
            // Accept question while it's read, and TLS already guards the bytes on the way, so those go without.
            await using var stream = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        return new LocalSendFile(fileId, item.Name, info.Length, LocalSendMime.Of(item.Name), sha, null,
            new LocalSendFileMetadata(new DateTimeOffset(info.LastWriteTimeUtc), new DateTimeOffset(info.LastAccessTimeUtc)));
    }

    /// <summary>A job's state outside the queue: its PIN, the last session (to cancel it), what the page shows.</summary>
    private sealed class JobMemory
    {
        public volatile string? Pin;
        public LocalSendPhase? Phase;
        public string? ReceiverAlias;
        public TransferError? SessionError;

        /// <summary>A session an earlier run left open (the phone went away mid-send); cancelled before asking again.</summary>
        public (LocalSendTarget Target, string SessionId)? Open;
    }

    private sealed record SessionState(LocalSendClient Client, LocalSendSession? Session, HashSet<string> Asked);

    /// <summary>One run of a job = one LocalSend session.</summary>
    private sealed class RunSession(LocalSendHandler owner, TransferWorkContext context, LocalSendRequest request, JobMemory memory)
    {
        private static readonly TimeSpan IdleTurn = TimeSpan.FromMinutes(5);

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Lock _gate = new();
        private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
        private readonly string _deviceId = context.Device.Summary.Id;
        private SessionState? _state;
        private DeviceException? _failure;
        private CancellationTokenRegistration _onStop;
        private bool _anyFailed;
        private bool _closed;
        private long _lastActivity = Environment.TickCount64;

        /// <summary>Done with the phone: closed, failed, or silent for so long that its run must have ended without telling.</summary>
        private bool IsOver
        {
            get
            {
                lock (_gate)
                {
                    return _closed || _failure is not null || Environment.TickCount64 - _lastActivity > IdleTurn.TotalMilliseconds;
                }
            }
        }

        public async Task<SessionState> EnsureAsync(TransferItem current, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            LocalSendClient? client = null;
            try
            {
                if (_failure is { } failure)
                {
                    throw new DeviceException(failure.Code, failure.Message, failure);
                }

                if (_state is { } existing)
                {
                    Touch();
                    return existing;
                }

                owner.Publish(memory, context.JobId, LocalSendPhase.Connecting);
                memory.SessionError = null;
                await TakeTurnAsync(ct).ConfigureAwait(false);
                await CancelLeftoverAsync(ct).ConfigureAwait(false);

                // Every file this run will send: the queue's saved items still to go, plus the one in hand.
                var pending = owner._itemsOf(context.JobId)
                    .Where(i => i.State is TransferItemState.Queued or TransferItemState.Running)
                    .Where(i => i.Index != current.Index)
                    .Append(current)
                    .OrderBy(i => i.Index)
                    .ToList();
                var files = new List<LocalSendFile>(pending.Count);
                foreach (var item in pending)
                {
                    if (File.Exists(item.Source))
                    {
                        files.Add(await DescribeFileAsync(item, FileId(item), ct).ConfigureAwait(false));
                    }
                }

                client = LocalSendIdentity.Connect(context.Device, request.Target, owner._timeouts);
                owner.Publish(memory, context.JobId, LocalSendPhase.WaitingForAccept);
                var asked = files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                var session = files.Count == 0 ? null : await client.PrepareUploadAsync(owner._me.Info(), files, memory.Pin, ct).ConfigureAwait(false);
                memory.ReceiverAlias = request.Target.Alias;
                _state = new SessionState(client, session, asked);
                Touch();
                if (session is not null)
                {
                    memory.Open = (request.Target, session.SessionId);
                    _onStop = ct.Register(() => _ = AbortAsync());
                }

                client = null;
                return _state;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException
                                       && !(ex is DeviceException gone && TransferQueue.IsDeviceUnavailable(gone)))
            {
                // Whatever stopped the session stops it for every file of this run: the phone is asked once per run.
                var failure = ex as DeviceException ?? new DeviceException(LocalSendErrors.Failed, ex.Message, ex);
                lock (_gate)
                {
                    _failure = failure;
                }

                memory.SessionError = new TransferError(failure.Code, failure.Message);
                if (ReferenceEquals(failure, ex))
                {
                    throw;
                }

                throw failure;
            }
            finally
            {
                client?.Dispose();
                if (_state is null)
                {
                    LeaveTurn();
                }

                _lock.Release();
            }
        }

        /// <summary>
        /// LocalSend takes one session at a time (409 to a second one). Another send to the same phone waits here for
        /// the one before it to finish, rather than failing as busy.
        /// </summary>
        private async Task TakeTurnAsync(CancellationToken ct)
        {
            while (true)
            {
                var holder = owner._turns.GetOrAdd(_deviceId, this);
                if (ReferenceEquals(holder, this) || (holder.IsOver && owner._turns.TryUpdate(_deviceId, this, holder)))
                {
                    return;
                }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }

        private void LeaveTurn() => owner._turns.TryRemove(new KeyValuePair<string, RunSession>(_deviceId, this));

        private void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

        /// <summary>An error that ends the session: the files after it fail the same way without a round trip.</summary>
        public void Fail(DeviceException ex)
        {
            lock (_gate)
            {
                _failure ??= ex;
                memory.SessionError = new TransferError(ex.Code, ex.Message);
            }
        }

        /// <summary>One file has its answer; when every file of the session has one the session is over.</summary>
        public void Finished(string fileId, bool failed)
        {
            SessionState? state;
            bool anyFailed;
            lock (_gate)
            {
                _lastActivity = Environment.TickCount64;
                _finished.Add(fileId);
                _anyFailed |= failed;
                anyFailed = _anyFailed;
                state = _state;
                if (state is null || _closed || (state.Session is not null && !state.Asked.All(_finished.Contains)))
                {
                    return;
                }

                _closed = true;
            }

            _onStop.Dispose();
            // A file that failed leaves the receiver waiting for it: end the session so the phone doesn't hang there.
            _ = CloseAsync(state, cancelOnPhone: anyFailed);
        }

        /// <summary>The run stopped (pause, cancel, a gone phone): tell the phone, then drop the connection.</summary>
        public Task AbortAsync()
        {
            SessionState? state;
            lock (_gate)
            {
                if (_closed)
                {
                    return Task.CompletedTask;
                }

                _closed = true;
                state = _state;
            }

            if (state is null)
            {
                LeaveTurn();
                return Task.CompletedTask;
            }

            return CloseAsync(state, cancelOnPhone: true);
        }

        private async Task CloseAsync(SessionState state, bool cancelOnPhone)
        {
            try
            {
                // Remembered until the phone has heard it's over: the next run cancels it first, or the phone would be busy.
                var over = !cancelOnPhone || state.Session is null
                    || await state.Client.CancelAsync(state.Session.SessionId, CancellationToken.None).ConfigureAwait(false);
                if (over && state.Session is { } done && memory.Open?.SessionId == done.SessionId)
                {
                    memory.Open = null;
                }
            }
            finally
            {
                state.Client.Dispose();
                LeaveTurn();
            }
        }

        private async Task CancelLeftoverAsync(CancellationToken ct)
        {
            if (memory.Open is not { } open)
            {
                return;
            }

            memory.Open = null;
            try
            {
                using var client = LocalSendIdentity.Connect(context.Device, open.Target, owner._timeouts);
                await client.CancelAsync(open.SessionId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DeviceException or ArgumentException)
            {
                // Best effort: the phone may have dropped that session itself.
            }
        }
    }
}
