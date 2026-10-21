using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Tests;

/// <summary>"Also save edited versions": an iPhone photo edited in Photos gets "IMG_1234 (edited)" next to it, kept up to date.</summary>
public sealed class PhotosBackupEditsTests
{
    private static readonly DateTimeOffset Oct = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static string Month => Oct.ToLocalTime().ToString("yyyy/MM", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] Render(string what) => PhotosTestLibrary.ContentFor("render " + what, 30_000);

    [Fact]
    public async Task Edited_versions_are_left_alone_unless_asked_for()
    {
        var phone = new PhotosTestPhone();
        var photo = phone.Library.Add("IMG_1234.HEIC", Oct);
        phone.Library.Edit(photo, "stamp-1", Render("one"));
        await using var host = await PhotosBackupHost.StartAsync(phone);

        var status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.False(status.Options.SaveEdited);
        Assert.Equal(0, status.Count.NewEdits);
        await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_1234.HEIC"], host.Files(phone));
    }

    [Fact]
    public async Task An_edited_photo_gets_its_edited_version_next_to_it_and_a_new_edit_updates_that_file()
    {
        var phone = new PhotosTestPhone();
        var edited = phone.Library.Add("IMG_1234.HEIC", Oct);
        phone.Library.Add("IMG_1235.HEIC", Oct);
        phone.Library.Edit(edited, "stamp-1", Render("one"));
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var id = phone.Summary.Id;
        host.Backup.Configure(id, null, null, null, null, saveEdited: true);

        var count = await host.Backup.CountNewAsync(id, CancellationToken.None);
        Assert.Equal((2, 1), (count.NewCount, count.NewEdits));
        var job = await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_1234 (edited).heic", $"{Month}/IMG_1234.HEIC", $"{Month}/IMG_1235.HEIC"], host.Files(phone));
        var editPath = Path.Combine(host.Destination(phone), Month.Replace('/', '\\'), "IMG_1234 (edited).heic");
        Assert.Equal(Render("one"), await File.ReadAllBytesAsync(editPath));
        Assert.Equal(Oct.UtcDateTime, File.GetLastWriteTimeUtc(editPath));
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((2, 1, 0), (run.Copied, run.Edited, run.EditsMissing));
        Assert.Equal(("stamp-1", editPath), (host.Backup.Ledger.Find(id, edited.Key)!.EditStamp, host.Backup.Ledger.Find(id, edited.Key)!.EditPcPath));

        // Nothing new: nothing to copy, edits included.
        Assert.Equal((0, 0), Counts(await host.Backup.CountNewAsync(id, CancellationToken.None)));

        // Edited again on the phone: the same file is brought up to date, the original isn't read again.
        phone.Library.Edit(edited, "stamp-2", Render("two"));
        Assert.Equal((0, 1), Counts(await host.Backup.CountNewAsync(id, CancellationToken.None)));
        var reads = phone.Library.Reads;
        job = await host.BackUpAsync(phone);
        Assert.Equal((1, 1), (job.TotalItems, job.DoneItems));
        Assert.Equal(reads + 1, phone.Library.Reads);
        Assert.Equal(Render("two"), await File.ReadAllBytesAsync(editPath));
        Assert.Equal(3, host.Files(phone).Length);
        Assert.Equal(1, host.Backup.Ledger.GetRun(job.Id)!.Edited);
        Assert.Equal("stamp-2", host.Backup.Ledger.Find(id, edited.Key)!.EditStamp);
    }

    [Fact]
    public async Task An_edited_version_someone_changed_on_the_pc_is_never_overwritten()
    {
        var phone = new PhotosTestPhone();
        var photo = phone.Library.Add("IMG_1234.HEIC", Oct);
        phone.Library.Edit(photo, "stamp-1", Render("one"));
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, saveEdited: true);
        await host.BackUpAsync(phone);

        // The user touched up the edited copy on the PC.
        var editPath = Path.Combine(host.Destination(phone), Month.Replace('/', '\\'), "IMG_1234 (edited).heic");
        await File.WriteAllBytesAsync(editPath, Render("mine"));

        phone.Library.Edit(photo, "stamp-2", Render("two"));
        await host.BackUpAsync(phone);
        Assert.Equal(Render("mine"), await File.ReadAllBytesAsync(editPath));
        var newer = Path.Combine(host.Destination(phone), Month.Replace('/', '\\'), "IMG_1234 (edited) (1).heic");
        Assert.Equal(Render("two"), await File.ReadAllBytesAsync(newer));
        Assert.Equal(newer, host.Backup.Ledger.Find(phone.Summary.Id, photo.Key)!.EditPcPath);
    }

    [Fact]
    public async Task An_edit_the_phone_has_no_rendered_version_of_is_reported_not_guessed_and_not_looked_for_again()
    {
        var phone = new PhotosTestPhone();
        var photo = phone.Library.Add("IMG_1234.HEIC", Oct);
        phone.Library.Edit(photo, "stamp-1", render: null);
        await using var host = await PhotosBackupHost.StartAsync(phone);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, saveEdited: true);

        var job = await host.BackUpAsync(phone);
        Assert.Equal([$"{Month}/IMG_1234.HEIC"], host.Files(phone));
        var run = host.Backup.Ledger.GetRun(job.Id)!;
        Assert.Equal((1, 0, 1), (run.Copied, run.Edited, run.EditsMissing));
        Assert.Equal(0, (await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)).NewEdits);

        // A new edit that does have its rendered version: copied then.
        phone.Library.Edit(photo, "stamp-2", Render("two"));
        await host.BackUpAsync(phone);
        Assert.Contains($"{Month}/IMG_1234 (edited).heic", host.Files(phone));
    }

    [Fact]
    public async Task Photos_backed_up_before_the_option_was_on_get_their_edits_the_next_time_and_jpg_when_converting()
    {
        var phone = new PhotosTestPhone();
        var photo = phone.Library.Add("IMG_1234.HEIC", Oct);
        phone.Library.Edit(photo, "stamp-1", Render("one"));
        await using var host = await PhotosBackupHost.StartAsync(phone, new FakeJpegTranscoder());
        await host.BackUpAsync(phone);

        host.Backup.Configure(phone.Summary.Id, null, null, convertHeic: true, null, saveEdited: true);
        Assert.Equal((0, 1), Counts(await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)));
        var job = await host.BackUpAsync(phone);
        Assert.Equal(TransferState.Done, job.State);
        Assert.Contains($"{Month}/IMG_1234 (edited).jpg", host.Files(phone));
    }

    [Fact]
    public void Edited_names_follow_the_photo_and_the_render()
    {
        Assert.Equal(@"D:\B\2026\10\IMG_1234 (edited).heic", PhotoBackupEdits.EditedPath(@"D:\B\2026\10\IMG_1234.HEIC", "FullSizeRender.heic", convertHeic: false));
        Assert.Equal(@"D:\B\2026\10\IMG_1234 (1) (edited).jpg", PhotoBackupEdits.EditedPath(@"D:\B\2026\10\IMG_1234 (1).jpg", "FullSizeRender.heic", convertHeic: true));
        Assert.Equal(@"D:\B\IMG_0005 (edited).mov", PhotoBackupEdits.EditedPath(@"D:\B\IMG_0005.MOV", "FullSizeRender.mov", convertHeic: true));
    }

    private static (int New, int Edits) Counts(PhotoBackupCount count) => (count.NewCount, count.NewEdits);
}
