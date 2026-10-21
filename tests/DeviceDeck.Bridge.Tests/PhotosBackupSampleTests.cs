using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Photos;

namespace DeviceDeck.Bridge.Tests;

/// <summary>
/// After "forget history", or under a new device id, photos already in the folder are recognised by a sample (size, first
/// and last bytes) of the phone's file before anything is copied, so a library on the PC isn't downloaded again.
/// </summary>
public sealed class PhotosBackupSampleTests
{
    private static readonly DateTimeOffset Oct = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task After_forgetting_the_history_nothing_is_downloaded_again()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct, content: PhotosTestLibrary.ContentFor("one", 300_000));
        phone.Library.Add("IMG_0002.HEIC", Oct, motion: true);
        phone.Library.Add("IMG_0003.MOV", Oct, kind: Core.Abstractions.PhotoKind.Video);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        await host.BackUpAsync(phone);
        var files = host.Files(phone);

        host.Backup.Forget(phone.Summary.Id);
        var reads = phone.Library.Reads;
        var job = await host.BackUpAsync(phone);

        Assert.Equal(reads, phone.Library.Reads);   // no photo copied off the phone again
        Assert.True(phone.Library.RangeReads >= 4); // a sample each (two parts for the big one), and the Live Photo's video
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((0, 3), (run.Copied, run.Duplicates));
        Assert.Equal(files, host.Files(phone));
        Assert.Equal(0, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewCount);
    }

    [Fact]
    public async Task A_phone_under_a_new_device_id_finds_its_photos_in_the_folder()
    {
        var before = new PhotosTestPhone("old-id", name: "Nima’s iPhone");
        var content = Enumerable.Range(0, 5).Select(i => PhotosTestLibrary.ContentFor("photo " + i)).ToList();
        for (var i = 0; i < 5; i++)
        {
            before.Library.Add($"IMG_{i:D4}.JPG", Oct, content: content[i]);
        }

        var after = new PhotosTestPhone("new-id", name: "Nima’s iPhone");
        for (var i = 0; i < 5; i++)
        {
            after.Library.Add($"IMG_{i:D4}.JPG", Oct, content: content[i]); // the same photos, other keys
        }

        await using var host = await PhotosBackupHost.StartAsync(before, more: new PhotosTestProvider(after) { Id = "photos-test-2" });
        await host.BackUpAsync(before);
        host.Backup.Configure(after.Summary.Id, host.Destination(before), null, null, null);

        var job = await host.BackUpAsync(after);
        Assert.Equal(0, after.Library.Reads);
        Assert.Equal((0, 5), (host.Backup.Ledger.GetRun(job.Id)!.Copied, host.Backup.Ledger.GetRun(job.Id)!.Duplicates));
        Assert.Equal(5, host.Files(before).Length);
    }

    [Fact]
    public async Task Same_size_and_name_with_other_bytes_is_copied_under_a_new_name()
    {
        var phone = new PhotosTestPhone();
        var photo = phone.Library.Add("IMG_0001.JPG", Oct, content: PhotosTestLibrary.ContentFor("phone", 400_000));
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var folder = Path.Combine(host.Destination(phone), Oct.ToLocalTime().ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            Oct.ToLocalTime().ToString("MM", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        foreach (var (at, name) in new[] { (10, "IMG_0001.JPG"), (200_000, "IMG_0001 (1).JPG"), (399_999, "IMG_0001 (2).JPG") })
        {
            var lookalike = PhotosTestLibrary.ContentFor("phone", 400_000);
            lookalike[at] ^= 0xFF; // at the start, in the middle, at the end
            await File.WriteAllBytesAsync(Path.Combine(folder, name), lookalike);
        }

        await host.BackUpAsync(phone);

        // None of them is the photo: it is copied (then compared whole) and gets a free name.
        Assert.Equal(1, phone.Library.Reads);
        Assert.Equal(Path.Combine(folder, "IMG_0001 (3).JPG"), host.Backup.Ledger.Find(phone.Summary.Id, photo.Key)!.PcPath);
        Assert.Equal(phone.Library.FileOf(photo.AssetId), await File.ReadAllBytesAsync(Path.Combine(folder, "IMG_0001 (3).JPG")));
    }

    [Fact]
    public async Task A_differently_named_copy_or_a_phone_without_part_reads_is_compared_whole()
    {
        var phone = new PhotosTestPhone();
        var photo = phone.Library.Add("IMG_0001.JPG", Oct);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var renamed = Path.Combine(host.Destination(phone), "Old PC", "beach.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(renamed)!);
        await File.WriteAllBytesAsync(renamed, phone.Library.FileOf(photo.AssetId));

        var job = await host.BackUpAsync(phone);
        Assert.Equal(1, phone.Library.Reads); // no namesake: copied, then found by SHA-256
        Assert.Equal(0, phone.Library.RangeReads);
        Assert.Equal((0, 1), (host.Backup.Ledger.GetRun(job.Id)!.Copied, host.Backup.Ledger.GetRun(job.Id)!.Duplicates));

        host.Backup.Forget(phone.Summary.Id);
        phone.Library.CanReadRanges = false;
        await host.BackUpAsync(phone);
        Assert.Equal(2, phone.Library.Reads);
    }

    [Fact]
    public async Task A_jpeg_made_from_a_heic_is_found_by_the_heics_sample_without_converting_again()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0001.HEIC", Oct);
        phone.Library.Add("IMG_0002.HEIC", Oct);
        var transcoder = new FakeJpegTranscoder();
        await using var host = await PhotosBackupHost.StartAsync(phone, transcoder);
        host.Backup.Configure(phone.Summary.Id, null, null, convertHeic: true, null);
        await host.BackUpAsync(phone);
        Assert.Equal(2, transcoder.Calls);

        host.Backup.Forget(phone.Summary.Id);
        var reads = phone.Library.Reads;
        var job = await host.BackUpAsync(phone);
        Assert.Equal(reads, phone.Library.Reads);
        Assert.Equal(2, transcoder.Calls);
        Assert.Equal((0, 2), (host.Backup.Ledger.GetRun(job.Id)!.Copied, host.Backup.Ledger.GetRun(job.Id)!.Duplicates));
        Assert.Equal(2, host.Files(phone).Length);
    }

    [Theory]
    [InlineData("IMG_0001.HEIC", "IMG_0001")]
    [InlineData("IMG_0001 (3).HEIC", "IMG_0001")]
    [InlineData("Trip (Rome).jpg", "Trip (Rome)")]
    [InlineData("IMG_0001 (edited).jpg", "IMG_0001 (edited)")]
    public void Stems_drop_the_clash_number_only(string name, string stem) => Assert.Equal(stem, PhotoBackupSample.Stem(name));

    [Fact]
    public void Samples_cover_a_small_file_whole_and_a_big_one_at_both_ends()
    {
        Assert.Equal([(0L, 1000)], PhotoBackupSample.Ranges(1000));
        Assert.Equal([(0L, 65536), (500_000L - 32768, 65536), (1_000_000L - 65536, 65536)], PhotoBackupSample.Ranges(1_000_000));
        var bytes = PhotosTestLibrary.ContentFor("x", 1_000_000);
        byte[] Changed(int at)
        {
            var copy = bytes.ToArray();
            copy[at] ^= 1;
            return copy;
        }

        Assert.NotEqual(PhotoBackupSample.Of(bytes), PhotoBackupSample.Of(Changed(0)));
        Assert.NotEqual(PhotoBackupSample.Of(bytes), PhotoBackupSample.Of(Changed(500_000)));
        Assert.NotEqual(PhotoBackupSample.Of(bytes), PhotoBackupSample.Of(Changed(999_999)));
        Assert.Equal(PhotoBackupSample.Of(bytes), PhotoBackupSample.Of(Changed(200_000))); // between the parts: unseen, by design
        Assert.NotEqual(PhotoBackupSample.Of(bytes), PhotoBackupSample.Of(bytes.AsSpan(0, 999_999)));
    }
}
