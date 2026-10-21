using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The backup with several photos in flight: names, duplicates and the ledger stay exactly as one at a time.</summary>
public sealed class PhotosBackupSpeedTests
{
    private static readonly DateTimeOffset Oct = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Photos_in_flight_together_never_take_one_name()
    {
        var phone = new PhotosTestPhone();
        for (var i = 0; i < 24; i++)
        {
            phone.Library.Add("IMG_0001.HEIC", Oct, motion: i % 3 == 0);
        }

        await using var host = await PhotosBackupHost.StartAsync(phone);
        var job = await host.BackUpAsync(phone);

        Assert.Equal((24, 0), (job.DoneItems, job.FailedItems));
        var stills = host.Files(phone).Where(f => f.EndsWith(".HEIC", StringComparison.Ordinal)).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(24, stills.Count);
        Assert.Contains("IMG_0001.HEIC", stills);
        Assert.All(Enumerable.Range(1, 23), n => Assert.Contains($"IMG_0001 ({n}).HEIC", stills));
        // Each Live Photo's video sits under its own still's name.
        var videos = host.Files(phone).Where(f => f.EndsWith(".MOV", StringComparison.Ordinal)).ToList();
        Assert.Equal(8, videos.Count);
        Assert.All(videos, v => Assert.Contains(Path.ChangeExtension(Path.GetFileName(v), ".HEIC"), stills));
        Assert.Equal(24, host.Backup.Ledger.CountBackedUp(phone.Summary.Id));
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((24, 24, 0), (run.Planned, run.Copied, run.Duplicates));
    }

    [Fact]
    public async Task The_same_bytes_in_flight_twice_are_written_once()
    {
        var phone = new PhotosTestPhone();
        var same = PhotosTestLibrary.ContentFor("one photo");
        for (var i = 0; i < 9; i++)
        {
            phone.Library.Add($"IMG_{i:D4}.JPG", Oct, content: same);
        }

        await using var host = await PhotosBackupHost.StartAsync(phone);
        var job = await host.BackUpAsync(phone);

        Assert.Single(host.Files(phone));
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((1, 8), (run.Copied, run.Duplicates));
        Assert.Equal(9, host.Backup.Ledger.CountBackedUp(phone.Summary.Id));
        Assert.Equal(0, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewCount);
    }

    [Fact]
    public async Task A_photo_the_ledger_has_but_the_queue_had_not_saved_yet_is_not_copied_again()
    {
        var phone = new PhotosTestPhone();
        var assets = Enumerable.Range(0, 6).Select(i => phone.Library.Add($"IMG_{i:D4}.HEIC", Oct)).ToList();
        phone.Library.BytesPerSecond = 40_000; // a second a photo: the pause lands before any of them is done
        await using var host = await PhotosBackupHost.StartAsync(phone);

        var started = await host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None);
        await Wait.UntilAsync(() => host.Queue.Get(started.Id)!.TotalItems == 6, "the plan");
        host.Queue.Pause(started.Id);
        await Wait.ForStateAsync(host.Queue, started.Id, TransferState.Paused);
        var done = host.Queue.Get(started.Id)!.DoneItems;

        // As if the app had stopped right after the ledger saved one photo and before the queue saved that item.
        var first = assets.First(a => !File.Exists(Path.Combine(host.Destination(phone), "2026", "10", a.FileName)));
        var batch = new PhotoBackupLedgerBatch();
        batch.Record(phone.Summary.Id, new PhotoBackupRecord(first.Key, first.SourcePath, first.FileName, first.Size, first.TakenAt,
            Path.Combine(host.Destination(phone), "elsewhere.HEIC"), null, false, started.Id, DateTimeOffset.Now));
        host.Backup.Ledger.Save(batch);

        phone.Library.BytesPerSecond = null;
        var reads = phone.Library.Reads;
        host.Queue.Resume(started.Id);
        await Wait.ForStateAsync(host.Queue, started.Id, TransferState.Done);
        Assert.Equal(6, host.Queue.Get(started.Id)!.DoneItems);
        Assert.Equal(6 - done - 1, phone.Library.Reads - reads);
        Assert.DoesNotContain(first.FileName, host.Files(phone).Select(Path.GetFileName));
    }

    [Fact]
    public void The_backup_runs_a_few_photos_at_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        using (var ledger = new PhotoBackupLedger(Path.Combine(folder, "photo-backup.db")))
        {
            IBatchingTransferHandler handler = new PhotosBackupHandler(ledger);
            Assert.InRange(handler.ParallelItems, 2, 4);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }
}
