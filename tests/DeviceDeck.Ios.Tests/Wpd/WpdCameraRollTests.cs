using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>
/// Reading Windows' view of the camera roll for a delete (v0.4.1): bulk reads with a one-by-one fallback, only the folders
/// that can hold the items (a full walk when a match is missing), the listing kept between deletes and when it is dropped,
/// cancelling before and during the deletes, and the stages a delete reports.
/// </summary>
public sealed class WpdCameraRollTests
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly FakeWpd _wpd = new();
    private readonly FakeCameraRoll _roll = new();
    private readonly PhotoDeleteLog _log = new(null);
    private readonly ManualTime _time = new();
    private readonly WpdListingCache _cache;

    public WpdCameraRollTests() => _cache = new WpdListingCache(_time);

    private WpdPhotoDeleter Deleter() => new(_wpd, Udid, _roll, _log, (_, _) => Task.CompletedTask, _cache);

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 9, 38, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>n photos in 100APPLE, shown under the same folder name (the match goes by folder).</summary>
    private FakeWpdDevice Library(int n)
    {
        var files = Enumerable.Range(1, n).Select(i => ("100APPLE", $"IMG_{i:0000}.HEIC", 1000L + i)).ToList();
        foreach (var (folder, name, size) in files)
        {
            _roll.Add($"DCIM/{folder}/{name}", size);
        }

        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll(files);
        device.Deleted = (_, path) => _roll.Disk.Remove(path);
        return device;
    }

    private static PhotoDeleteItem Item(int i) => new($"DCIM/100APPLE/IMG_{i:0000}.HEIC", 1000 + i);

    private static PhotoDeleteItem ItemOf(BigCameraRoll.Entry e) => new(e.Path, e.Size);

    // ---------- Bulk reads ----------

    [Fact]
    public void Objects_read_in_bulk_one_by_one_or_both_are_the_same()
    {
        var device = Library(30);
        using var c = _wpd.Open(device.PnpId, readOnly: true);
        var folder = WpdDcimWalker.Folders(c, WpdDcimWalker.FindDcim(c, Ct, out _)!.Value.DcimId, Ct).Single();
        var ids = c.ChildIds(folder.Id, Ct).Append("o-gone").ToList();
        var expected = ids.Select(c.Get).Where(o => o is not null).OrderBy(o => o!.Id).ToList();

        var bulk = new WpdReadStats();
        var progress = new List<int>();
        Assert.Equal(expected, WpdObjectReader.Read(c, ids, bulk, progress.Add, Ct).OrderBy(o => o.Id));
        Assert.Equal((30, 1, 1, false), (bulk.Bulk, bulk.OneByOne, bulk.BulkRequests, bulk.NoBulk)); // the gone id isn't in the bulk answer: asked once more
        Assert.Equal(ids.Count, progress[^1]);

        _wpd.BulkLeavesOut = id => id.EndsWith('3');
        var partial = new WpdReadStats();
        Assert.Equal(expected, WpdObjectReader.Read(c, ids, partial, null, Ct).OrderBy(o => o.Id));
        Assert.True(partial.Bulk > 0 && partial.OneByOne > 0, partial.ToString());

        _wpd.BulkReads = false;
        var none = new WpdReadStats();
        Assert.Equal(expected, WpdObjectReader.Read(c, ids, none, null, Ct).OrderBy(o => o.Id));
        Assert.Equal((0, ids.Count, true), (none.Bulk, none.OneByOne, none.NoBulk));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_delete_maps_and_deletes_the_same_with_or_without_bulk_reads(bool bulk)
    {
        var device = Library(12);
        _wpd.BulkReads = bulk;
        _wpd.BulkLeavesOut = bulk ? id => id.EndsWith('5') : null;
        var deleter = Deleter();

        var results = await deleter.DeleteAsync([Item(2), Item(5), Item(11)], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal(9, _roll.Disk.Count);
        Assert.Equal(!bulk, deleter.LastRead!.NoBulk);
        Assert.True(bulk ? deleter.LastRead.Bulk > 0 : deleter.LastRead.OneByOne > 0, deleter.LastRead.ToString());
        Assert.Contains(_log.Written, l => l.Contains("read Windows' view for 3 item(s) (by folder)", StringComparison.Ordinal));
        Assert.Empty(device.Forbidden);
    }

    [Fact]
    public async Task A_bulk_read_that_fails_falls_back_to_one_by_one_and_the_delete_goes_on()
    {
        var device = Library(8);
        _wpd.BulkFails = WpdErrors.Timeout;
        var deleter = Deleter();

        var results = await deleter.DeleteAsync([Item(3), Item(4)], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.True(deleter.LastRead!.NoBulk);
        Assert.Contains("bulk read failed", deleter.LastRead.ToString());
        Assert.Single(device.DeleteCalls);
    }

    [Fact]
    public void Bulk_answers_come_back_through_windows_own_objects()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (hr, objects, status, progress) = ComWpdBackend.BulkSelfTest();

        Assert.Equal(0, hr); // Windows can reach the callback through its COM interface
        var o = Assert.Single(objects); // the entry without an id can't be placed
        Assert.Equal(("o2A", "IMG_0002.MOV", 123_456L), (o.Id, o.FileName, o.Size));
        Assert.Equal(WpdObjectKind.File, o.Kind);
        Assert.Equal((0, 1), (status, progress));
    }

    // ---------- Only the folders that can hold the items ----------

    [Theory]
    [InlineData("202610_a", 2026, 10)]
    [InlineData("202501_b", 2025, 1)]
    [InlineData("2021__06", 2021, 6)]
    public void Month_folder_names_are_read(string name, int year, int month) =>
        Assert.Equal((year, month), WpdMonths.MonthOf(name));

    [Theory]
    [InlineData("100APPLE")]
    [InlineData("202613_a")]
    [InlineData("2026100a")]
    [InlineData("DCIM")]
    [InlineData(null)]
    public void Other_folder_names_have_no_month(string? name) => Assert.Null(WpdMonths.MonthOf(name));

    [Fact]
    public void A_picture_near_a_months_edge_may_be_filed_under_either_month()
    {
        Assert.Equal([(2026, 10)], WpdMonths.MonthsFor(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)));
        // 2 a.m. UTC on 1 November is still 31 October in New York.
        Assert.Equal([(2026, 10), (2026, 11)], WpdMonths.MonthsFor(new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero)));
        // 10 p.m. UTC on 31 December is already 1 January in Tehran.
        Assert.Equal([(2025, 12), (2026, 1)], WpdMonths.MonthsFor(new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Month_folders_narrow_the_walk_to_the_months_the_items_were_taken_in()
    {
        var big = BigCameraRoll.Build(_wpd, _roll, Udid);
        var items = big.Entries.Where(e => e.Month == "202508_a").Take(3).Select(ItemOf).ToList();
        var deleter = Deleter();

        var results = await deleter.DeleteAsync(items, Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal((1, 18, null), (deleter.LastRead!.FoldersRead, deleter.LastRead.FoldersInRoll, deleter.LastRead.FreshBecause));
        Assert.Contains(_log.Written, l => l.Contains("(by month)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_at_a_months_edge_reads_the_month_next_to_it_too()
    {
        var wpd = _wpd;
        var entries = BigCameraRoll.Build(new FakeWpd(), new FakeCameraRoll(), Udid).Entries.ToList();
        var index = entries.FindIndex(e => e.Month == "202508_a");
        entries[index] = BigCameraRoll.AtMonthEdge(entries[index]); // 1 August, 2 a.m. UTC: 31 July west of UTC−2
        BigCameraRoll.Place(wpd, _roll, Udid, entries);
        var deleter = Deleter();

        var r = Assert.Single(await deleter.DeleteAsync([ItemOf(entries[index])], Ct));

        Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome);
        Assert.Equal(2, deleter.LastRead!.FoldersRead);
    }

    [Fact]
    public async Task An_item_windows_files_under_another_month_is_found_by_a_full_walk()
    {
        // A picture saved into Photos long after it was taken: Windows may file it by when it was added.
        var entries = BigCameraRoll.Build(new FakeWpd(), new FakeCameraRoll(), Udid).Entries.ToList();
        var index = entries.FindIndex(e => e.Month == "202508_a");
        entries[index] = entries[index] with { Month = "202610_a" };
        BigCameraRoll.Place(_wpd, _roll, Udid, entries);
        var deleter = Deleter();

        var results = await deleter.DeleteAsync([ItemOf(entries[index]), ItemOf(entries[index + 1])], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal(18, deleter.LastRead!.FoldersRead);
        Assert.Contains("isn't in the folders its date points to", deleter.LastRead.FreshBecause);
    }

    [Fact]
    public async Task A_name_used_twice_in_windows_months_is_still_refused()
    {
        // Two objects called IMG_0042.HEIC in the month the item was taken: as ambiguous as before.
        var big = BigCameraRoll.Build(_wpd, _roll, Udid);
        var entry = big.Entries[41];
        big.Device.AddFile(big.MonthFolders[entry.Month], entry.Name, entry.Size + 1, canDelete: null, null, entry.Taken);

        var results = await Deleter().DeleteAsync([ItemOf(entry)], Ct);

        Assert.Equal(PhotoDeleteOutcome.Mismatch, Assert.Single(results).Outcome);
        Assert.Contains("2 files called", results[0].Message);
        Assert.Empty(big.Device.DeleteCalls);
    }

    // ---------- The listing kept between deletes ----------

    [Fact]
    public async Task The_listing_is_kept_and_the_objects_a_delete_uses_are_read_again()
    {
        var device = Library(12);
        var deleter = Deleter();
        await deleter.DeleteAsync([Item(1), Item(2)], Ct);
        Assert.True(_cache.Holds);
        _wpd.Latency.Reset();

        var results = await deleter.DeleteAsync([Item(3), Item(4)], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal((0, 1, 2), (deleter.LastRead!.FoldersRead, deleter.LastRead.FoldersFromCache, deleter.LastRead.Verified));
        Assert.Equal(0, _wpd.Latency.ChildIdsCalls); // no folder listed again
        Assert.Equal(2, device.DeleteCalls.Count);
    }

    [Fact]
    public async Task After_a_clean_delete_only_the_deleted_objects_leave_the_listing()
    {
        var device = Library(6);
        var deleter = Deleter();
        var gone = new[] { device.IdOf("IMG_0001.HEIC"), device.IdOf("IMG_0002.HEIC") };

        await deleter.DeleteAsync([Item(1), Item(2)], Ct);

        var kept = _cache.Take(device.PnpId, out _)!;
        var files = kept.ToDcim().Folders.Single().Files.Select(o => o.Id).ToList();
        Assert.Equal(4, files.Count);
        Assert.DoesNotContain(gone[0], files);
        Assert.DoesNotContain(gone[1], files);
    }

    [Fact]
    public async Task A_phone_that_locks_or_goes_away_drops_the_listing()
    {
        Library(6);
        var deleter = Deleter();
        await deleter.DeleteAsync([Item(1)], Ct);
        Assert.True(_cache.Holds);

        deleter.Reset(PhotoDeleteBlock.Locked);

        Assert.False(_cache.Holds);
        await deleter.DeleteAsync([Item(2)], Ct);
        Assert.Equal((1, 0), (deleter.LastRead!.FoldersRead, deleter.LastRead.FoldersFromCache));
    }

    [Fact]
    public async Task A_delete_that_didnt_go_cleanly_drops_the_listing()
    {
        var device = Library(6);
        device.Answer = o => o.FileName == "IMG_0002.HEIC" ? WpdErrors.AccessDenied : 0;
        var deleter = Deleter();

        var results = await deleter.DeleteAsync([Item(1), Item(2)], Ct);

        Assert.Equal(PhotoDeleteOutcome.Failed, results[1].Outcome);
        Assert.False(_cache.Holds);
    }

    [Fact]
    public async Task The_listing_is_read_fresh_after_five_minutes()
    {
        Library(6);
        var deleter = Deleter();
        await deleter.DeleteAsync([Item(1)], Ct);

        _time.Now += TimeSpan.FromMinutes(4);
        await deleter.DeleteAsync([Item(2)], Ct);
        Assert.Equal(1, deleter.LastRead!.FoldersFromCache);

        _time.Now += TimeSpan.FromMinutes(6);
        await deleter.DeleteAsync([Item(3)], Ct);
        Assert.Equal((1, 0), (deleter.LastRead!.FoldersRead, deleter.LastRead.FoldersFromCache));
    }

    [Fact]
    public async Task An_object_that_changed_since_it_was_kept_is_read_fresh_before_anything_goes()
    {
        var device = Library(6);
        var deleter = Deleter();
        await deleter.DeleteAsync([Item(1)], Ct);
        // The phone shows IMG_0005 as a new object now (the old id is gone).
        var folder = device.Children(device.Children(device.Children(WpdIds.DeviceObjectId)[0])[0])[0];
        device.Remove(device.IdOf("IMG_0005.HEIC"));
        var renewed = device.AddFile(folder, "IMG_0005.HEIC", 1005, dcimPath: "DCIM/100APPLE/IMG_0005.HEIC");

        var results = await deleter.DeleteAsync([Item(4), Item(5)], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal("an object kept from earlier changed", deleter.LastRead!.FreshBecause);
        Assert.Contains(renewed, device.DeleteCalls[^1]);
        Assert.Equal(2, device.DeleteCalls.Count); // nothing went part-way on the out-of-date listing
    }

    [Fact]
    public async Task A_photo_taken_since_the_listing_was_kept_is_found_fresh()
    {
        var device = Library(4);
        var deleter = Deleter();
        await deleter.DeleteAsync([Item(1)], Ct);
        var folder = device.Children(device.Children(device.Children(WpdIds.DeviceObjectId)[0])[0])[0];
        device.AddFile(folder, "IMG_0009.HEIC", 1009, dcimPath: "DCIM/100APPLE/IMG_0009.HEIC");
        _roll.Add("DCIM/100APPLE/IMG_0009.HEIC", 1009);

        var r = Assert.Single(await deleter.DeleteAsync([Item(9)], Ct));

        Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome);
        Assert.Equal("an item isn't in the listing kept from earlier", deleter.LastRead!.FreshBecause);
    }

    // ---------- Cancelling, and the stages a delete reports ----------

    private sealed class Watcher(Action<PhotoDeleteStage, int, int>? onStage = null, Action<IReadOnlyList<int>>? onDeleting = null) : IPhotoDeleteObserver
    {
        public List<(PhotoDeleteStage Stage, int Done, int Total)> Stages { get; } = [];
        public List<IReadOnlyList<int>> Batches { get; } = [];
        public Dictionary<int, PhotoDeleteResult> Results { get; } = [];

        public void Stage(PhotoDeleteStage stage, int done, int total)
        {
            Stages.Add((stage, done, total));
            onStage?.Invoke(stage, done, total);
        }

        public void Deleting(IReadOnlyList<int> items)
        {
            Batches.Add(items);
            onDeleting?.Invoke(items);
        }

        public void Result(int item, PhotoDeleteResult result) => Results[item] = result;
    }

    [Fact]
    public async Task A_delete_reports_reading_n_of_m_then_deleting_then_checking()
    {
        var big = BigCameraRoll.Build(_wpd, _roll, Udid);
        var items = big.Entries.Where(e => e.Month is "202508_a" or "202509_a").Where((_, i) => i % 20 == 0).Select(ItemOf).ToList();
        var watcher = new Watcher();

        var results = await Deleter().DeleteAsync(items, watcher, Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        var order = watcher.Stages.Select(s => s.Stage).Distinct().ToList();
        Assert.Equal([PhotoDeleteStage.Reading, PhotoDeleteStage.Deleting, PhotoDeleteStage.Checking], order);
        var reading = watcher.Stages.Where(s => s.Stage == PhotoDeleteStage.Reading && s.Total > 0).ToList();
        Assert.Equal(reading.Select(s => s.Done).Order(), reading.Select(s => s.Done)); // never backwards
        Assert.Equal((362, 362), (reading[^1].Done, reading[^1].Total)); // two months of 181
        Assert.Equal((items.Count, items.Count), (watcher.Stages.Last(s => s.Stage == PhotoDeleteStage.Deleting).Done, items.Count));
        Assert.Equal((items.Count, items.Count), (watcher.Stages[^1].Done, watcher.Stages[^1].Total));
        Assert.Equal(items.Count, watcher.Results.Count);
        Assert.Equal((items.Count + 4) / 5, watcher.Batches.Count);
    }

    [Fact]
    public async Task Cancelling_while_reading_deletes_nothing()
    {
        var big = BigCameraRoll.Build(_wpd, _roll, Udid);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watcher = new Watcher((stage, done, _) =>
        {
            if (stage == PhotoDeleteStage.Reading && done > 100)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Deleter().DeleteAsync(big.LargestVideos(39), watcher, cts.Token));

        Assert.Empty(big.Device.DeleteCalls);
        Assert.Equal(BigCameraRoll.Files, _roll.Disk.Count);
        Assert.Empty(watcher.Batches);
    }

    [Fact]
    public async Task Cancelling_while_deleting_stops_after_the_batch_and_says_which_went()
    {
        var device = Library(12);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var watcher = new Watcher(onDeleting: _ => cts.Cancel());

        var results = await Deleter().DeleteAsync(Enumerable.Range(1, 12).Select(Item).ToList(), watcher, cts.Token);

        Assert.Single(device.DeleteCalls); // the batch in flight finished; no other started
        Assert.Equal(Enumerable.Repeat(PhotoDeleteOutcome.Deleted, 5), results.Take(5).Select(r => r.Outcome));
        Assert.All(results.Take(5), r => Assert.Equal(PhotoDeleteDestination.Permanent, r.WentTo)); // still checked on the phone
        Assert.All(results.Skip(5), r => Assert.Equal((PhotoDeleteOutcome.NotTried, WpdPhotoDeleter.CancelledNote), (r.Outcome, r.Message)));
        Assert.Equal(7, _roll.Disk.Count);
        Assert.Contains(_log.Written, l => l.Contains("stopped: cancelled after 5 of 12", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_the_phone_cant_be_asked_afterwards_windows_word_stands()
    {
        Library(3);
        var watcher = new Watcher();
        _roll.FailIndexAfter = 1; // the index read before the delete works; the one after fails (the cable came out)

        var results = await Deleter().DeleteAsync([Item(1), Item(2)], watcher, Ct);

        Assert.All(results, r => Assert.Equal((PhotoDeleteOutcome.Deleted, PhotoDeleteDestination.Unknown), (r.Outcome, r.WentTo)));
        Assert.All(results, r => Assert.Contains("couldn't check on the iPhone", r.Message));
        Assert.False(_cache.Holds);
    }

    // ---------- Reading ahead (v0.4.2) ----------

    private static async Task ReadAheadFinished(WpdPhotoDeleter deleter)
    {
        if (deleter.Prefetching is { } running)
        {
            await running.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task A_read_ahead_reads_the_whole_roll_read_only_and_the_delete_then_reads_almost_nothing()
    {
        var big = BigCameraRoll.Build(_wpd, _roll, Udid);
        _wpd.Latency.LikeTheOwnersIphone(TimeSpan.FromMilliseconds(47)); // bulk no faster, as on the owner's iPhone 16
        var deleter = Deleter();

        Assert.True(deleter.StartPrefetch("test", Ct));
        await ReadAheadFinished(deleter);

        Assert.Equal((0, 0), (_wpd.ReadWriteOpens, big.Device.DeleteCalls.Count)); // read-only, nothing deleted
        Assert.True(_wpd.ReadOnlyOpens > 0);
        Assert.True(_cache.Peek()!.HasAll);
        Assert.Equal(18, deleter.LastPrefetch!.FoldersRead);
        Assert.Contains(_log.Written, l => l.Contains("read ahead (test)", StringComparison.Ordinal));
        var readAhead = _wpd.Latency.Elapsed;

        _wpd.Latency.Reset();
        TimeSpan? first = null;
        big.Device.Answer = _ =>
        {
            first ??= _wpd.Latency.Elapsed;
            return 0;
        };
        var items = big.Entries.Where(e => e.Month == "202508_a").Take(3).Select(ItemOf).ToList();
        var results = await deleter.DeleteAsync(items, Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal((0, 1, 3), (deleter.LastRead!.FoldersRead, deleter.LastRead.FoldersFromCache, deleter.LastRead.Verified));
        Assert.True(first < TimeSpan.FromSeconds(2), $"{first} after a read ahead of {readAhead}");
    }

    [Fact]
    public async Task A_read_ahead_doesnt_start_again_while_the_listing_is_complete_and_recent()
    {
        Library(6);
        var deleter = Deleter();
        Assert.True(deleter.StartPrefetch("first", Ct));
        await ReadAheadFinished(deleter);
        var opens = _wpd.ReadOnlyOpens;

        Assert.False(deleter.StartPrefetch("again", Ct));
        _time.Now += TimeSpan.FromMinutes(3); // past half the listing's life: read again
        Assert.True(deleter.StartPrefetch("later", Ct));
        await ReadAheadFinished(deleter);
        Assert.True(_wpd.ReadOnlyOpens > opens);
    }

    [Fact]
    public async Task A_delete_stops_a_read_ahead_and_carries_on_from_what_it_read()
    {
        var big = BigCameraRoll.Build(_wpd, _roll, Udid);
        using var hold = new SemaphoreSlim(0);
        var pause = true;
        var held = false;
        // Once three folders are read ahead, the read ahead waits.
        _wpd.BeforeRead = () =>
        {
            if (Volatile.Read(ref pause) && _cache.Peek() is { } kept && kept.ToDcim().Folders.Count >= 3)
            {
                Volatile.Write(ref held, true);
                hold.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var deleter = Deleter();
        Assert.True(deleter.StartPrefetch("test", Ct));
        await WaitUntil(() => Volatile.Read(ref held));
        var partly = _cache.Peek()!.ToDcim().Folders.Select(f => f.Name).ToList();
        Assert.True(partly.Count < 18);

        // The delete wants items from a folder read ahead and from one not read yet.
        var month = partly[0];
        var other = big.MonthFolders.Keys.First(m => !partly.Contains(m));
        var items = big.Entries.Where(e => e.Month == month).Take(2).Concat(big.Entries.Where(e => e.Month == other).Take(2)).Select(ItemOf).ToList();
        var deleting = Task.Run(() => deleter.DeleteAsync(items, Ct));
        await Task.Delay(200);
        Volatile.Write(ref pause, false);
        hold.Release(1000);
        var results = await deleting;

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Null(deleter.Prefetching);
        Assert.True(deleter.LastRead!.FoldersFromCache >= 1, deleter.LastRead.ToString()); // carried on from the read ahead
        Assert.True(deleter.LastRead.FoldersRead >= 1, deleter.LastRead.ToString()); // and read the rest itself
        Assert.Contains(_log.Written, l => l.Contains("read ahead (test) stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Locking_or_unplugging_stops_a_read_ahead_and_drops_what_it_read()
    {
        BigCameraRoll.Build(_wpd, _roll, Udid);
        using var hold = new SemaphoreSlim(0);
        var pause = false;
        _wpd.BeforeRead = () =>
        {
            if (Volatile.Read(ref pause))
            {
                hold.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var deleter = Deleter();
        deleter.StartPrefetch("test", Ct);
        await WaitUntil(() => _cache.Peek() is { } kept && kept.ToDcim().Folders.Count >= 2);
        Volatile.Write(ref pause, true);

        deleter.Reset(PhotoDeleteBlock.Locked);
        Volatile.Write(ref pause, false);
        hold.Release(1000);
        await ReadAheadFinished(deleter);

        Assert.False(_cache.Holds);
        Assert.Null(deleter.Prefetching);
    }

    [Fact]
    public async Task No_read_ahead_while_a_delete_runs()
    {
        Library(4);
        using var hold = new SemaphoreSlim(0);
        var deleter = Deleter();
        var pause = true;
        _wpd.BeforeRead = () =>
        {
            if (Volatile.Read(ref pause))
            {
                hold.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var deleting = Task.Run(() => deleter.DeleteAsync([Item(1)], Ct));
        await Task.Delay(100);

        Assert.False(deleter.StartPrefetch("during a delete", Ct));
        Volatile.Write(ref pause, false);
        hold.Release(1000);
        Assert.Equal(PhotoDeleteOutcome.Deleted, Assert.Single(await deleting).Outcome);
    }

    [Fact]
    public async Task A_months_second_folder_is_read_with_the_first()
    {
        // The owner's iPhone shows "202610_b" beside "202610_a": both are October 2026.
        var entries = BigCameraRoll.Build(new FakeWpd(), new FakeCameraRoll(), Udid).Entries.ToList();
        var index = entries.FindIndex(e => e.Month == "202508_a");
        entries[index] = entries[index] with { Month = "202508_b" };
        BigCameraRoll.Place(_wpd, _roll, Udid, entries);
        var deleter = Deleter();

        var results = await deleter.DeleteAsync([ItemOf(entries[index]), ItemOf(entries[index + 1])], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal((2, null), (deleter.LastRead!.FoldersRead, deleter.LastRead.FreshBecause));
    }

    [Fact]
    public async Task An_item_windows_lists_nowhere_is_refused_saying_to_delete_it_on_the_iPhone()
    {
        // As 38 of the owner's 39 largest videos: in the phone's DCIM, moved over from an earlier iPhone, not in Windows' view.
        var device = Library(4);
        _roll.Add("DCIM/100APPLE/IMG_0009.MOV", 900_000_000);

        var results = await Deleter().DeleteAsync([Item(1), new PhotoDeleteItem("DCIM/100APPLE/IMG_0009.MOV", 900_000_000)], Ct);

        Assert.Equal([PhotoDeleteOutcome.NotTried, PhotoDeleteOutcome.Mismatch], results.Select(r => r.Outcome));
        Assert.Contains("anywhere in the iPhone's camera roll", results[1].Message);
        Assert.Contains("Photos app", results[1].Message);
        Assert.Empty(device.DeleteCalls);
    }

    // ---------- Which items the PC can't delete (v0.4.2) ----------

    [Fact]
    public async Task Nothing_is_marked_until_windows_view_is_read_completely_and_then_only_what_it_lists_nowhere()
    {
        var device = Library(6);
        // Moved over from an earlier iPhone: in DCIM, in the library, not in Windows' view.
        _roll.Add("DCIM/100APPLE/IMG_0020.MOV", 900_000_000).Add("DCIM/100APPLE/IMG_0021.HEIC", 3_000_000);
        using var hold = new SemaphoreSlim(0);
        var pause = true;
        var held = false;
        var deleter = Deleter();
        // Held once it knows how much there is to read, before the files.
        _wpd.BeforeRead = () =>
        {
            if (Volatile.Read(ref pause) && deleter.PrefetchProgress.Total > 0)
            {
                Volatile.Write(ref held, true);
                hold.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var first = await deleter.ReachAsync("phone", Ct, Ct); // starts the read ahead
        Assert.Equal((PhotoDeleteReachState.Checking, 0), (first.State, first.NotListed.Count));
        await WaitUntil(() => Volatile.Read(ref held));
        var midway = await deleter.ReachAsync("phone", Ct, Ct);
        Assert.Equal(PhotoDeleteReachState.Checking, midway.State);
        Assert.Empty(midway.NotListed); // never from a partial listing
        Assert.Equal(6, midway.Total);

        Volatile.Write(ref pause, false);
        hold.Release(1000);
        await ReadAheadFinished(deleter);
        var ready = await deleter.ReachAsync("phone", Ct, Ct);

        Assert.Equal(PhotoDeleteReachState.Ready, ready.State);
        Assert.Equal(["DCIM/100APPLE/IMG_0020.MOV", "DCIM/100APPLE/IMG_0021.HEIC"], ready.NotListed.Order());
        Assert.Equal((6, 6), (ready.Done, ready.Total));
        Assert.Same(ready, await deleter.ReachAsync("phone", Ct, Ct)); // worked out once per listing
        Assert.Empty(device.DeleteCalls);
    }

    [Fact]
    public async Task Items_it_cant_tell_are_never_marked()
    {
        // A name the phone uses twice, and a picture Windows shows under a name of the phone's making.
        var taken = new DateTimeOffset(2026, 10, 21, 13, 42, 44, TimeSpan.Zero);
        _roll.Add("DCIM/107APPLE/IMG_7413.HEIC", 3_013_973).Add("DCIM/107APPLE/IMG_7414.PNG", 35_861);
        _roll.Add("DCIM/108APPLE/IMG_7413.HEIC", 5_000); // IMG_7413.HEIC twice on the phone
        _roll.Taken["DCIM/107APPLE/IMG_7414.PNG"] = taken;
        var device = _wpd.AddIphone(Udid);
        var month = device.AddFolder(device.AddStorage(), "202610_a");
        device.AddFile(month, "IMG_7413.HEIC", 3_013_973, canDelete: null, "DCIM/107APPLE/IMG_7413.HEIC");
        device.AddFile(month, "PZHI6539.PNG", 35_861, canDelete: null, "DCIM/107APPLE/IMG_7414.PNG", taken.ToOffset(TimeSpan.FromHours(3.5)));
        var deleter = Deleter();
        deleter.StartPrefetch("test", Ct);
        await ReadAheadFinished(deleter);

        var reach = await deleter.ReachAsync("phone", Ct, Ct);

        Assert.Equal(PhotoDeleteReachState.Ready, reach.State);
        Assert.Empty(reach.NotListed); // the renamed PNGs are found; the name used twice is ambiguous, not "nowhere"
        Assert.Empty(device.DeleteCalls);

        // Without Photos.sqlite no size is known: nothing is marked either.
        _roll.WithDatabase = false;
        _roll.Add("DCIM/100APPLE/IMG_0099.HEIC", 1_000);
        await _roll.GetIndexAsync(refresh: true, Ct);
        _time.Now += TimeSpan.FromMinutes(6);
        deleter.StartPrefetch("again", Ct);
        await ReadAheadFinished(deleter);
        Assert.Empty((await deleter.ReachAsync("phone", Ct, Ct)).NotListed);
    }

    [Fact]
    public async Task While_a_delete_runs_what_was_known_stands()
    {
        Library(4);
        _roll.Add("DCIM/100APPLE/IMG_0020.MOV", 900_000_000);
        var deleter = Deleter();
        deleter.StartPrefetch("test", Ct);
        await ReadAheadFinished(deleter);
        var known = await deleter.ReachAsync("phone", Ct, Ct);
        using var hold = new SemaphoreSlim(0);
        var pause = true;
        _wpd.BeforeRead = () =>
        {
            if (Volatile.Read(ref pause))
            {
                hold.Wait(TimeSpan.FromSeconds(10));
            }
        };
        var deleting = Task.Run(() => deleter.DeleteAsync([Item(1)], Ct));
        await WaitUntil(() => _wpd.ReadWriteOpens > 0);

        Assert.Same(known, await deleter.ReachAsync("phone", Ct, Ct));
        Volatile.Write(ref pause, false);
        hold.Release(1000);
        await deleting;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }
}
