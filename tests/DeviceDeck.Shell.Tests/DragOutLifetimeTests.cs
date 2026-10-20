using DeviceDeck.Bridge;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Shell.Desktop.DragOut;

namespace DeviceDeck.Shell.Tests;

/// <summary>When a drag's session (its staged files) may go, and when a waiting read gives up.</summary>
public sealed class DragOutLifetimeTests
{
    private static DragOutSession Session(DragHost host, TimeSpan? lingerLimit = null, TimeSpan? idleLimit = null)
    {
        var entries = host.Photos.Select(p => new DragOutEntry(p.Id, p.FileName, p.Size, false, p.TakenAt)).ToArray();
        var id = Guid.NewGuid().ToString("N");
        var session = new DragOutSession(id, DragHost.PhoneId, "photos", null, Path.Combine(host.Root, "staging", id), host.Queue, host.Registry,
            () => DragOutService.PhotoFiles(entries))
        {
            LingerAfterJob = TimeSpan.FromMilliseconds(50),
            LingerLimit = lingerLimit ?? TimeSpan.FromMinutes(5),
            IdleLimit = idleLimit ?? TimeSpan.FromMinutes(10),
        };
        host.Registry.Add(session);
        return session;
    }

    [Fact]
    public async Task A_drop_copying_on_its_own_thread_keeps_the_staged_files_until_Explorer_is_done()
    {
        await using var host = await DragHost.StartAsync();
        var session = Session(host);
        session.StartOperation();
        await Task.Run(session.EnsureJob);
        session.DragEnded(dropped: true);
        await Wait.UntilAsync(() => host.Queue.Get(session.JobId!)!.State == TransferState.Done, "the job to finish");

        // Many linger periods later File Explorer still hasn't said it is done: nothing goes.
        await Task.Delay(500);
        Assert.Same(session, host.Registry.Find(session.Id));
        Assert.True(File.Exists(session.StagingPath(0)));

        session.EndOperation(succeeded: true);
        await Wait.UntilAsync(() => host.Registry.Find(session.Id) is null && !Directory.Exists(session.StagingFolder), "the session to close");
    }

    [Fact]
    public async Task A_drop_that_never_says_it_is_done_is_closed_after_the_limit()
    {
        await using var host = await DragHost.StartAsync();
        var session = Session(host, lingerLimit: TimeSpan.FromMilliseconds(300));
        session.StartOperation();
        await Task.Run(session.EnsureJob);
        session.DragEnded(dropped: true);

        await Wait.UntilAsync(() => host.Registry.Find(session.Id) is null && !Directory.Exists(session.StagingFolder), "the session to close");
        Assert.Equal(TransferState.Done, host.Queue.Get(session.JobId!)!.State);
    }

    [Fact]
    public async Task An_async_drop_that_has_not_asked_for_contents_yet_stays_open_when_the_drag_returns()
    {
        await using var host = await DragHost.StartAsync();
        var session = Session(host);
        _ = session.Files;
        session.StartOperation();
        session.DragEnded(dropped: false); // an async target can report no effect yet

        Assert.Same(session, host.Registry.Find(session.Id));
        await Task.Run(session.EnsureJob);
        Assert.Equal(host.Photos[0].Size, await Task.Run(() => session.WaitFor(0, long.MaxValue)));
        session.EndOperation(succeeded: true);
        await Wait.UntilAsync(() => host.Registry.Find(session.Id) is null, "the session to close");
    }

    // The fake phone writes 256 KB at a time, then waits to keep its rate: at 10 KB/s the job copies the first chunk
    // of the first photo and then sits for 25 s, running but copying nothing. The tests move it on by hand.
    private const long Stalled = 10_000;

    [Fact]
    public async Task A_read_does_not_give_up_while_the_job_is_paused()
    {
        await using var host = await DragHost.StartAsync(bytesPerSecond: Stalled);
        var session = Session(host, idleLimit: TimeSpan.FromMilliseconds(300));
        await Task.Run(session.EnsureJob);
        host.Queue.Pause(session.JobId!);
        await Wait.UntilAsync(() => host.Queue.Get(session.JobId!)!.State == TransferState.Paused, "the job to pause");

        var read = Task.Run(() => session.WaitFor(2, host.Photos[2].Size));
        await Task.Delay(TimeSpan.FromSeconds(2)); // well past the idle limit with nothing copied, but paused
        Assert.False(read.IsCompleted);

        host.Queue.Cancel(session.JobId!);
        var error = await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("The copy was cancelled.", error.Message);
    }

    [Fact]
    public async Task A_read_waits_while_other_files_copy_and_gives_up_once_nothing_moves()
    {
        await using var host = await DragHost.StartAsync(bytesPerSecond: Stalled);
        var session = Session(host, idleLimit: TimeSpan.FromSeconds(1));
        await Task.Run(session.EnsureJob);
        await Wait.UntilAsync(() => host.Queue.Get(session.JobId!)!.State == TransferState.Running, "the job to run");

        // Nothing of item 2 for three idle limits, while bytes of item 0 keep coming: the read waits.
        var read = Task.Run(() => session.WaitFor(2, host.Photos[2].Size));
        for (var i = 1; i <= 30; i++)
        {
            session.Report(0, i * 1000);
            await Task.Delay(100);
        }

        Assert.False(read.IsCompleted);

        // Then nothing at all: the read gives up.
        var error = await Assert.ThrowsAsync<IOException>(() => read.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("The phone stopped sending this file.", error.Message);
        session.Close();
    }

    [Fact]
    public async Task Closing_the_session_closes_the_streams_File_Explorer_still_holds()
    {
        await using var host = await DragHost.StartAsync();
        var session = Session(host);
        await Task.Run(session.EnsureJob);
        await Wait.UntilAsync(() => host.Queue.Get(session.JobId!)!.State == TransferState.Done, "the job to finish");

        var stream = new VirtualFileStream(session, session.Files[0]);
        session.Track(stream);
        var buffer = new byte[1024];
        stream.Read(buffer, buffer.Length, 0); // opens the staged file
        Assert.Throws<IOException>(() => new FileStream(session.StagingPath(0), FileMode.Open, FileAccess.Read, FileShare.None).Dispose());

        session.Close();
        Assert.False(Directory.Exists(session.StagingFolder));
    }
}
