using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;
using Xunit.Abstractions;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>
/// How long Windows' side of a delete takes before the first object goes, on a simulated clock as slow as the owner's
/// iPhone 16 (v0.4.0 log: 3,258 objects read one by one in about 160 s, ~50 ms each): Clean-up › largest videos ›
/// 39 items. The numbers are written to the test output (dotnet test --logger "console;verbosity=detailed").
/// </summary>
public sealed class WpdMappingSpeedTests(ITestOutputHelper output)
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private sealed class Rig
    {
        public FakeWpd Wpd { get; } = new();
        public FakeCameraRoll Roll { get; } = new();
        public BigCameraRoll Big { get; }
        public WpdPhotoDeleter Deleter { get; }
        public TimeSpan? FirstDelete { get; private set; }

        public Rig(Func<int, int>? monthOf = null, TimeSpan? perBulkObject = null, bool bulk = true)
        {
            Big = BigCameraRoll.Build(Wpd, Roll, Udid, monthOf);
            Wpd.Latency.LikeTheOwnersIphone(perBulkObject);
            Wpd.BulkReads = bulk;
            Big.Device.Answer = _ =>
            {
                FirstDelete ??= Wpd.Latency.Elapsed;
                return 0;
            };
            Deleter = new WpdPhotoDeleter(Wpd, Udid, Roll, new PhotoDeleteLog(null), (_, _) => Task.CompletedTask);
        }

        public async Task<TimeSpan> DeleteAsync(IReadOnlyList<PhotoDeleteItem> items)
        {
            Wpd.Latency.Reset();
            FirstDelete = null;
            var results = await Deleter.DeleteAsync(items, Ct);
            Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
            return FirstDelete!.Value;
        }
    }

    /// <summary>What v0.4.0 did before its first delete: every object of every folder read one by one (WpdDcimWalker.Walk).</summary>
    private static TimeSpan OldWalk(Rig rig)
    {
        rig.Wpd.Latency.Reset();
        rig.Wpd.ListDevices();
        using var c = rig.Wpd.Open(rig.Big.Device.PnpId, readOnly: false);
        var dcim = WpdDcimWalker.Walk(c, Ct, out _)!;
        Assert.Equal(BigCameraRoll.Files, dcim.Folders.Sum(f => f.Files.Count));
        return rig.Wpd.Latency.Elapsed;
    }

    private void Line(string what, TimeSpan time, Rig rig) =>
        output.WriteLine($"{what,-66} {time.TotalSeconds,6:0.0} s  ({rig.Deleter.LastRead})");

    [Fact]
    public async Task The_39_largest_videos_among_3258_objects()
    {
        // As in the owner's log: the largest videos fall in 17 of the 18 months, so narrowing by month saves little here;
        // bulk reads and the kept listing do the work.
        var before = OldWalk(new Rig());
        output.WriteLine($"{"v0.4.0: every object read one by one",-66} {before.TotalSeconds,6:0.0} s");
        Assert.InRange(before.TotalSeconds, 150, 180);

        var bulk = new Rig();
        var items = bulk.Big.LargestVideos(39);
        Assert.Equal(17, bulk.Big.MonthsOf(items));
        var cold = await bulk.DeleteAsync(items);
        Line("now, bulk reads at 2 ms an object (assumed), 17 of 18 months", cold, bulk);
        Assert.True(cold < before / 10, $"{cold} isn't a tenth of {before}");

        var again = await bulk.DeleteAsync(bulk.Big.LargestVideos(78).Skip(39).ToList());
        Line("  the next 39, within 5 minutes (kept listing, objects read again)", again, bulk);
        Assert.True(again < TimeSpan.FromSeconds(3), again.ToString());

        var noBulk = new Rig(bulk: false);
        var oneByOne = await noBulk.DeleteAsync(items);
        Line("now, no bulk reads (driver says no), 17 of 18 months", oneByOne, noBulk);
        Assert.True(oneByOne < before, $"{oneByOne} vs {before}");
        var keptNoBulk = await noBulk.DeleteAsync(noBulk.Big.LargestVideos(78).Skip(39).ToList());
        Line("  the next 39, within 5 minutes", keptNoBulk, noBulk);
        Assert.True(keptNoBulk < TimeSpan.FromSeconds(15), keptNoBulk.ToString());

        var slowBulk = new Rig(perBulkObject: TimeSpan.FromMilliseconds(50));
        var noFaster = await slowBulk.DeleteAsync(items);
        Line("now, bulk no faster than one by one (50 ms an object)", noFaster, slowBulk);
        Assert.True(noFaster < before, $"{noFaster} vs {before}");
    }

    [Fact]
    public async Task With_a_read_ahead_the_delete_reads_almost_nothing_even_where_bulk_is_no_faster()
    {
        // The owner's iPhone 16 (v0.4.1 probe): bulk 48 ms an object, one by one 47 ms; 39 items in 17 months.
        var rig = new Rig(perBulkObject: TimeSpan.FromMilliseconds(48));
        var items = rig.Big.LargestVideos(39);
        rig.Wpd.Latency.Reset();
        Assert.True(rig.Deleter.StartPrefetch("Clean-up's analysis finished", Ct));
        // Null once it finished: on a busy machine it can finish (the clock is simulated) before this line runs.
        await (rig.Deleter.Prefetching ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(rig.Deleter.LastPrefetch is not null, "the read ahead ran");
        var ahead = rig.Wpd.Latency.Elapsed;
        output.WriteLine($"{"read ahead in the background (while the user chooses)",-66} {ahead.TotalSeconds,6:0.0} s  ({rig.Deleter.LastPrefetch})");

        var after = await rig.DeleteAsync(items);
        Line("then Delete → first delete", after, rig);
        Assert.True(after < TimeSpan.FromSeconds(5), after.ToString());

        var cold = await new Rig(perBulkObject: TimeSpan.FromMilliseconds(48)).DeleteAsync(items);
        output.WriteLine($"{"the same delete without a read ahead",-66} {cold.TotalSeconds,6:0.0} s");
        Assert.True(after < cold / 10, $"{after} vs {cold}");
    }

    [Fact]
    public async Task Videos_from_three_months_read_only_those_months()
    {
        // A trip: every video is in month 3, 9 or 15; the photos spread over all 18 months.
        int MonthOf(int i) => i % 10 == 9 ? new[] { 3, 9, 15 }[i % 3] : i * BigCameraRoll.Months / BigCameraRoll.Files;
        var before = OldWalk(new Rig(MonthOf));

        var bulk = new Rig(MonthOf);
        var items = bulk.Big.LargestVideos(39);
        Assert.Equal(3, bulk.Big.MonthsOf(items));
        var cold = await bulk.DeleteAsync(items);
        Line("trip (videos in 3 months), bulk reads at 2 ms an object", cold, bulk);
        Assert.Equal(3, bulk.Deleter.LastRead!.FoldersRead);

        var noBulk = new Rig(MonthOf, bulk: false);
        var oneByOne = await noBulk.DeleteAsync(items);
        Line("trip (videos in 3 months), no bulk reads", oneByOne, noBulk);
        Assert.Equal(3, noBulk.Deleter.LastRead!.FoldersRead);
        Assert.True(oneByOne < before / 3, $"{oneByOne} vs {before}");
        output.WriteLine($"{"trip, v0.4.0",-66} {before.TotalSeconds,6:0.0} s");
    }
}
