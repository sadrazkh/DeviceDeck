using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Transfers;
using DeviceDeck.Core.Transfers.Handlers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>Leftover .ddpart files of backups that will never resume: only the backup's own, only in its folders, only when idle.</summary>
public sealed class PhotosBackupCleanupTests
{
    private static readonly DateTimeOffset Oct = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static string Leave(string folder, string name, int bytes = 1000)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3)); // left by a crash a while ago
        return path;
    }

    [Fact]
    public async Task Only_the_backups_own_temp_files_in_its_folders_go()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        await host.BackUpAsync(phone);
        await host.Backup.CleanupTask; // the one after the run, before the leftovers are made
        var month = Path.Combine(host.Destination(phone), "2026", "10");

        var ours = new[]
        {
            Leave(month, "IMG_0002.HEIC.original.ddpart", 4000),
            Leave(month, "IMG_0002.MOV.motion.ddpart", 3000),
            Leave(month, "IMG_0003.jpg.backup.ddpart", 2000),
            Leave(Path.Combine(host.Destination(phone), "Undated"), "IMG_0004 (edited).heic.edited.ddpart", 1000),
        };
        var kept = new[]
        {
            Leave(month, "IMG_0005.HEIC.ddpart"),                                   // an export's or a copy's: not ours to judge
            Leave(month, "notes.txt"),
            Leave(Path.Combine(host.Root, "pc", "Elsewhere"), "IMG_0006.HEIC.original.ddpart"), // not a backup folder
        };
        var fresh = Path.Combine(month, "IMG_0007.HEIC.original.ddpart");
        await File.WriteAllBytesAsync(fresh, new byte[10]);                         // written just now: maybe a backup starting

        var cleanup = host.Backup.CleanUpPartials();
        Assert.Equal(ours.Order(StringComparer.OrdinalIgnoreCase), cleanup.Files.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(10_000, cleanup.Bytes);
        Assert.All(ours, f => Assert.False(File.Exists(f)));
        Assert.All(kept, f => Assert.True(File.Exists(f)));
        Assert.True(File.Exists(fresh));
        Assert.Contains($"2026/10/IMG_0001.HEIC", host.Files(phone));
    }

    [Fact]
    public async Task Nothing_goes_while_a_backup_into_that_folder_is_not_finished_and_a_restart_cleans_up()
    {
        var phone = new PhotosTestPhone();
        for (var i = 0; i < 4; i++)
        {
            phone.Library.Add($"IMG_{i:D4}.HEIC", Oct);
        }

        phone.Library.BytesPerSecond = 40_000;
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var job = await host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None);
        await Wait.UntilAsync(() => host.Queue.Get(job.Id)!.TotalItems == 4, "the plan");
        host.Queue.Pause(job.Id);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Paused);

        var leftover = Leave(Path.Combine(host.Destination(phone), "2026", "10"), "IMG_0099.HEIC.original.ddpart");
        Assert.Empty(host.Backup.CleanUpPartials().Files);
        Assert.True(File.Exists(leftover));

        host.Queue.Cancel(job.Id);
        await Wait.ForStateAsync(host.Queue, job.Id, TransferState.Cancelled);
        await host.RestartAsync(); // attaching again looks for leftovers
        await Wait.UntilAsync(() => !File.Exists(leftover), "the leftover to go");
    }

    [Fact]
    public void The_backups_temp_names_are_its_own()
    {
        Assert.All(PhotosBackupHandler.TempSuffixes, s => Assert.EndsWith(".ddpart", s));
        Assert.DoesNotContain(".ddpart", PhotosBackupHandler.TempSuffixes); // never the bare suffix other copies use
    }
}
