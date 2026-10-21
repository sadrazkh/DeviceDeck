using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The free-space check before a backup starts, and the "nearly full" warning for a folder.</summary>
public sealed class PhotosBackupSpaceTests
{
    private const long GB = 1_000_000_000;
    private static readonly DateTimeOffset Oct = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_status_says_what_the_new_photos_need_with_a_margin_and_what_is_free()
    {
        var phone = new PhotosTestPhone();
        var a = phone.Library.Add("IMG_0001.JPG", Oct);
        var b = phone.Library.Add("IMG_0002.HEIC", Oct, motion: true);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.DiskSpace = _ => new PcDiskSpace(100 * GB, 900 * GB);

        var space = (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Space;
        var bytes = a.Size + b.Size + b.Motion!.Size;
        Assert.Equal(Path.GetPathRoot(host.Destination(phone)), space.Root);
        Assert.Equal((100 * GB, 900 * GB), (space.FreeBytes, space.TotalBytes));
        Assert.Equal(bytes + PhotoBackupSpaces.MinimumMargin, space.NeededBytes);
        Assert.Equal((true, false, false), (space.Enough, space.NearlyFull, space.DriveMissing));
    }

    [Fact]
    public async Task A_backup_that_would_not_fit_is_refused_and_nothing_is_queued()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.JPG", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.DiskSpace = _ => new PcDiskSpace(600_000_000, 250 * GB);

        var status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((false, true), (status.Space.Enough, status.Space.NearlyFull));
        var refused = await Assert.ThrowsAsync<DeviceException>(() => host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None));
        Assert.Equal("no_space", refused.Code);
        Assert.Contains("Not enough space on", refused.Message);
        Assert.Contains("600 MB free", refused.Message);
        Assert.Empty(host.Queue.List());

        // Through the bridge the page gets the code it shows "Choose another folder" for.
        var response = await new BridgeCalls(host.Transport).CallAsync("photos.backup", new { deviceId = phone.Summary.Id });
        Assert.Equal("no_space", (string?)response["error"]!["data"]!["code"]);

        // Room again (or another folder): it starts.
        host.DiskSpace = _ => new PcDiskSpace(50 * GB, 250 * GB);
        Assert.Equal(TransferState.Done, (await host.BackUpAsync(phone)).State);
    }

    [Fact]
    public async Task A_destination_whose_drive_is_not_there_is_refused()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.JPG", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.RootExists = _ => false;

        var status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal((true, false, null), (status.Space.DriveMissing, status.Space.Enough, status.Space.FreeBytes));
        var refused = await Assert.ThrowsAsync<DeviceException>(() => host.Backup.StartAsync(phone.Summary.Id, CancellationToken.None));
        Assert.Equal(DeviceErrorCodes.PathNotFound, refused.Code);
        Assert.Contains("isn't there", refused.Message);
        Assert.Empty(host.Queue.List());
    }

    [Fact]
    public async Task Nothing_new_needs_no_room()
    {
        var phone = new PhotosTestPhone();
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.DiskSpace = _ => new PcDiskSpace(10_000_000, 250 * GB);

        var space = (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Space;
        Assert.Equal((0L, true, true), (space.NeededBytes, space.Enough, space.NearlyFull));
        Assert.Equal(0, (await host.BackUpAsync(phone)).TotalItems);
    }

    [Fact]
    public async Task Photos_turning_into_jpg_and_sizes_the_phone_has_not_said_are_counted_in()
    {
        var phone = new PhotosTestPhone();
        var heic = phone.Library.Add("IMG_0001.HEIC", Oct);
        var jpg = phone.Library.Add("IMG_0002.JPG", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone, new FakeJpegTranscoder());
        host.Backup.Configure(phone.Summary.Id, null, null, convertHeic: true, null);

        var space = (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Space;
        Assert.Equal((long)(heic.Size * PhotoBackupSpaces.JpegGrowth) + jpg.Size + PhotoBackupSpaces.MinimumMargin, space.NeededBytes);

        // Sizes unknown (an iPhone's newest photos): the average of the known ones stands in.
        phone.Library.SizesNeedCompleting = true;
        host.Backup.Configure(phone.Summary.Id, null, null, convertHeic: false, null);
        space = (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Space;
        Assert.Equal(2 * 4_000_000 + PhotoBackupSpaces.MinimumMargin, space.NeededBytes);
    }

    [Fact]
    public async Task A_folder_can_be_checked_for_settings_and_a_drive_nearly_full_says_so()
    {
        var phone = new PhotosTestPhone();
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var bridge = new BridgeCalls(host.Transport);
        var folder = Path.Combine(host.Root, "pc", "Pictures");

        host.DiskSpace = _ => new PcDiskSpace(12 * GB, 220 * GB); // this PC's C:
        var nearly = (await bridge.ResultAsync("photos.backupSpace", new { folder }))!.AsObject();
        Assert.Equal(["root", "freeBytes", "totalBytes", "neededBytes", "enough", "nearlyFull", "driveMissing"], nearly.Select(p => p.Key));
        Assert.True((bool)nearly["nearlyFull"]!);
        Assert.True((bool)nearly["enough"]!);

        host.DiskSpace = _ => new PcDiskSpace(300 * GB, 2000 * GB); // 15% of a big disk
        Assert.False((bool)(await bridge.ResultAsync("photos.backupSpace", new { folder }))!["nearlyFull"]!);
        host.DiskSpace = _ => new PcDiskSpace(90 * GB, 1000 * GB); // under a tenth
        Assert.True((bool)(await bridge.ResultAsync("photos.backupSpace", new { folder }))!["nearlyFull"]!);

        var bad = await bridge.CallAsync("photos.backupSpace", new { folder = "Pictures" });
        Assert.Equal(-32602, (int)bad["error"]!["code"]!);
    }

    [Fact]
    public void The_real_probe_reads_a_drive_and_gives_up_quietly_on_what_is_not_one()
    {
        var here = PhotoBackupSpaces.Probe(Path.GetTempPath());
        Assert.NotNull(here);
        Assert.InRange(here!.FreeBytes, 1, here.TotalBytes);
        Assert.True(PhotoBackupSpaces.RootExists(Path.GetTempPath()));
        Assert.Null(PhotoBackupSpaces.Probe(@"\\no-such-server\share\Backup"));
    }
}
