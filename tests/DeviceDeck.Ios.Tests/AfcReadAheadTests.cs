using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Tests.Fakes;

namespace DeviceDeck.Ios.Tests;

/// <summary>Reading a file with FileReads in flight ahead of their answers, and the pool of AFC connections for originals.</summary>
public sealed class AfcReadAheadTests
{
    private const int Chunk = AfcClient.ReadChunk;
    private static CancellationToken Ct => TestContext.Timeout();

    private static AfcClient Connect(FakeAfcServer server)
    {
        var (client, serverStream) = DuplexStream.CreatePair();
        _ = Task.Run(() => server.ServeAsync(serverStream, CancellationToken.None));
        return new AfcClient(client);
    }

    private static byte[] Expected(long length, ulong seed)
    {
        var bytes = new byte[length];
        FakeAfcServer.Generate(seed, 0, bytes);
        return bytes;
    }

    [Theory]
    [InlineData(0L, 1)]
    [InlineData(0L, 2)]
    [InlineData(1L, 2)]
    [InlineData(Chunk - 1L, 2)]
    [InlineData(Chunk * 1L, 2)]
    [InlineData(Chunk + 1L, 2)]
    [InlineData(Chunk * 5L / 2, 1)]
    [InlineData(Chunk * 5L / 2, 2)]
    [InlineData(Chunk * 5L / 2, 3)]
    public async Task Every_byte_arrives_in_order_and_the_read_ahead_stops_at_the_end(long length, int readAhead)
    {
        var server = new FakeAfcServer().AddGeneratedFile("/DCIM/100APPLE/IMG_0001.MOV", length, 7);
        await using var afc = Connect(server);
        using var copy = new MemoryStream();
        var reported = new List<long>();

        await afc.ReadFileAsync("/DCIM/100APPLE/IMG_0001.MOV", copy, new Progress(reported), Ct, readAhead);

        Assert.Equal(Expected(length, 7), copy.ToArray());
        var chunks = (int)((length + Chunk - 1) / Chunk);
        // readAhead requests up front, then one more per chunk that came back with data.
        Assert.Equal(readAhead + chunks, server.Operations.Count(o => o == AfcOperation.FileRead));
        Assert.Equal(chunks == 0 ? [] : Enumerable.Range(1, chunks).Select(i => Math.Min((long)i * Chunk, length)), reported);
        Assert.False(afc.IsBroken);

        // Still in step with the phone: the next request gets its own answer.
        Assert.Equal(["IMG_0001.MOV"], await afc.ReadDirectoryAsync("/DCIM/100APPLE", Ct));
    }

    [Fact]
    public async Task A_connection_dropped_mid_file_is_broken_for_good()
    {
        var server = new FakeAfcServer().AddGeneratedFile("/DCIM/IMG_0002.MOV", Chunk * 3L, 9);
        server.HangUpWhileReading = "/DCIM/IMG_0002.MOV";
        await using var afc = Connect(server);

        await Assert.ThrowsAnyAsync<IOException>(() => afc.ReadFileAsync("/DCIM/IMG_0002.MOV", new MemoryStream(), null, Ct, readAhead: 2));
        Assert.True(afc.IsBroken);
        var again = await Assert.ThrowsAsync<DeviceException>(() => afc.ReadDirectoryAsync("/DCIM", Ct));
        Assert.Equal(DeviceErrorCodes.Disconnected, again.Code);
    }

    [Fact]
    public async Task A_missing_file_is_path_not_found_and_leaves_the_connection_usable()
    {
        var server = new FakeAfcServer().AddDir("/DCIM");
        await using var afc = Connect(server);

        var missing = await Assert.ThrowsAsync<DeviceException>(() => afc.ReadFileAsync("/DCIM/gone.JPG", new MemoryStream(), null, Ct, readAhead: 2));
        Assert.Equal(DeviceErrorCodes.PathNotFound, missing.Code);
        Assert.False(afc.IsBroken);
        Assert.Empty(await afc.ReadDirectoryAsync("/DCIM", Ct));
    }

    [Fact]
    public async Task The_pool_reuses_connections_and_never_has_more_than_its_limit_in_use()
    {
        var server = new FakeAfcServer().AddGeneratedFile("/a", 10, 1);
        await using var pool = new AfcConnectionPool(_ => Task.FromResult(Connect(server)), maxConnections: 4);

        for (var i = 0; i < 5; i++)
            await pool.UseAsync(afc => afc.GetFileInfoAsync("/a", Ct), null, Ct);
        Assert.Equal(1, pool.Opened);

        var inUse = 0;
        var most = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uses = Enumerable.Range(0, 7).Select(_ => pool.UseAsync(async afc =>
        {
            var now = Interlocked.Increment(ref inUse);
            lock (gate)
                most = Math.Max(most, now);
            await gate.Task.WaitAsync(Ct);
            Interlocked.Decrement(ref inUse);
            return await afc.GetFileInfoAsync("/a", Ct);
        }, null, Ct)).ToArray();
        await WaitUntilAsync(() => Volatile.Read(ref inUse) == 4);
        await Task.Delay(50, Ct);
        Assert.Equal(4, Volatile.Read(ref inUse));
        gate.SetResult();
        await Task.WhenAll(uses);

        Assert.Equal(4, most);
        Assert.Equal(4, pool.Opened);
        Assert.Equal(4, pool.Idle);
    }

    [Fact]
    public async Task A_connection_that_died_while_idle_is_replaced_when_nothing_was_done_yet()
    {
        var server = new FakeAfcServer().AddGeneratedFile("/a", 10, 1);
        await using var pool = new AfcConnectionPool(_ => Task.FromResult(Connect(server)), maxConnections: 2);
        await pool.UseAsync(afc => afc.GetFileInfoAsync("/a", Ct), null, Ct);

        // The phone closes the idle connection: its next request fails before anything was written.
        server.HangUpAfterOperations = server.Operations.Count;
        var info = await pool.UseAsync(afc => afc.GetFileInfoAsync("/a", Ct), () => false, Ct);
        Assert.Equal(10, info!.Size);
        Assert.Equal(2, pool.Opened);

        // When part of the work was done already it is not repeated: the caller decides.
        server.HangUpAfterOperations = server.Operations.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => pool.UseAsync(afc => afc.GetFileInfoAsync("/a", Ct), () => true, Ct));
        Assert.Equal(0, pool.Idle); // the broken one was dropped, not handed out again
        Assert.Equal(10, (await pool.UseAsync(afc => afc.GetFileInfoAsync("/a", Ct), null, Ct))!.Size);
        Assert.Equal(3, pool.Opened);
    }

    [Fact]
    public async Task Idle_connections_close_after_the_idle_timeout()
    {
        var time = new ManualTime();
        var server = new FakeAfcServer().AddGeneratedFile("/a", 10, 1);
        await using var pool = new AfcConnectionPool(_ => Task.FromResult(Connect(server)), 2, TimeSpan.FromSeconds(20), time);
        await pool.UseAsync(afc => afc.GetFileInfoAsync("/a", Ct), null, Ct);
        Assert.Equal(1, pool.Idle);

        time.Advance(TimeSpan.FromSeconds(19));
        await pool.TrimAsync();
        Assert.Equal(1, pool.Idle);
        time.Advance(TimeSpan.FromSeconds(2));
        await pool.TrimAsync();
        Assert.Equal(0, pool.Idle);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class Progress(List<long> reported) : IProgress<long>
    {
        public void Report(long value) => reported.Add(value);
    }

    /// <summary>A clock that only moves when told to (timers never fire on their own).</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
