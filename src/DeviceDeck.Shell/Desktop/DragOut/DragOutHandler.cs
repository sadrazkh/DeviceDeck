using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Shell.Desktop.DragOut;

/// <summary>
/// The DragOut transfer kind: copies each dragged file off the phone into its session's staging file while
/// File Explorer reads it from there. Everything else (pause, a pulled cable, retries, the drawer) is the queue's.
/// A job whose drag is gone (the app restarted) fails its items: Explorer's side of it is gone too.
/// </summary>
internal sealed class DragOutHandler(DragOutRegistry registry) : TransferHandler<DragOutRequest>
{
    public const string InternalError = "internal";

    public override TransferKind Kind => TransferKind.DragOut;

    protected override Task<string> DescribeAsync(IDevice device, DragOutRequest request, CancellationToken ct)
    {
        if (request.Items.Count == 0)
        {
            throw new ArgumentException("There is nothing to copy.");
        }

        var what = request.Items.Count == 1
            ? request.Items[0].Name
            : request.Source == "photos" ? $"{request.Items.Count:N0} photos" : $"{request.Items.Count:N0} files";
        return Task.FromResult($"Copying {what} to File Explorer");
    }

    protected override Task<IReadOnlyList<TransferItemPlan>> PlanAsync(TransferWorkContext context, DragOutRequest request, CancellationToken ct)
    {
        var session = registry.Find(request.SessionId);
        IReadOnlyList<TransferItemPlan> plans = request.Items
            .Select((item, i) => new TransferItemPlan(item.Source, session?.StagingPath(i) ?? "", item.Name, item.Size))
            .ToArray();
        return Task.FromResult(plans);
    }

    protected override async Task<TransferItemState> RunItemAsync(TransferWorkContext context, DragOutRequest request, TransferItem item,
        IProgress<long> progress, CancellationToken ct)
    {
        var session = registry.Find(request.SessionId)
            ?? throw new DeviceException(InternalError, "The drag to File Explorer has ended, so this copy can't go on.");

        session.Reset(item.Index);
        long written = 0;
        try
        {
            var path = session.StagingPath(item.Index);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous))
            {
                var sink = new ReportingStream(file, count =>
                {
                    written = count;
                    session.Report(item.Index, count);
                });
                if (request.Source == "photos")
                {
                    await context.Device.Photos.ReadOriginalAsync(item.Source, sink, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    var fs = await context.GetFileSystemAsync(request.Root ?? "", ct).ConfigureAwait(false);
                    await fs.ReadAsync(item.Source, sink, progress, ct).ConfigureAwait(false);
                }
            }

            if (item.Size > 0 && written != item.Size)
            {
                throw new DeviceException(DeviceErrorCodes.Protocol,
                    $"The phone sent {written:N0} bytes of {item.Name} instead of {item.Size:N0}.");
            }

            session.Complete(item.Index, written);
            return TransferItemState.Done;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && !IsDeviceUnavailable(ex))
        {
            // Pause, a pulled cable and app exit leave the reader waiting: the item starts over when the job runs again.
            session.Fail(item.Index, ex is DeviceException d ? d.Message : $"{item.Name} couldn't be copied.");
            throw;
        }
    }

    private static bool IsDeviceUnavailable(Exception ex) => ex is DeviceException
    {
        Code: DeviceErrorCodes.Disconnected or DeviceErrorCodes.NotFound or DeviceErrorCodes.Locked
            or DeviceErrorCodes.TrustRequired or DeviceErrorCodes.Unauthorized,
    };
}

/// <summary>Writes straight through (the file is unbuffered) and reports the running total, so readers see bytes at once.</summary>
internal sealed class ReportingStream(Stream inner, Action<long> report) : Stream
{
    private long _written;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _written;

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        _written += buffer.Length;
        report(_written);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _written += buffer.Length;
        report(_written);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
