using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.PhotoDelete;
using DeviceDeck.Ios.Wpd;

namespace DeviceDeck.Ios.Tests.Wpd;

/// <summary>
/// Deleting iPhone photos through Windows Portable Devices, against a fake WPD and a fake camera roll: the mapping,
/// refusals, batches, Live Photos, partial failures, where deleted photos went, and why a phone can't delete.
/// </summary>
public sealed class WpdPhotoDeleterTests
{
    private const string Udid = "00008140-000A1B2C3D4E801E";
    private static CancellationToken Ct => TestContext.Timeout();

    private readonly FakeWpd _wpd = new();
    private readonly FakeCameraRoll _roll = new();
    private readonly PhotoDeleteLog _log = new(null);

    private WpdPhotoDeleter Deleter() => new(_wpd, Udid, _roll, _log, (_, _) => Task.CompletedTask);

    /// <summary>n photos IMG_0001.HEIC… in 100APPLE, sizes 1000 + i, on the phone and in Windows; deleting one removes its file.</summary>
    private FakeWpdDevice Library(int n, Func<string, string>? wpdFolder = null, bool? canDelete = true)
    {
        var files = Enumerable.Range(1, n).Select(i => ("100APPLE", $"IMG_{i:0000}.HEIC", 1000L + i)).ToList();
        foreach (var (folder, name, size) in files)
        {
            _roll.Add($"DCIM/{folder}/{name}", size);
        }

        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll(files, canDelete, wpdFolder);
        device.Deleted = (_, path) => _roll.Disk.Remove(path);
        return device;
    }

    private static PhotoDeleteItem Item(int i) => new($"DCIM/100APPLE/IMG_{i:0000}.HEIC", 1000 + i);

    /// <summary>Internal Storage › DCIM › the first folder.</summary>
    private static string FolderOf(FakeWpdDevice device) =>
        device.Children(device.Children(device.Children(WpdIds.DeviceObjectId)[0])[0])[0];

    // ---------- Mapping ----------

    [Fact]
    public void Maps_by_folder_name_and_size_when_windows_shows_the_phones_folders()
    {
        var device = Library(3);
        device.AddFile(FolderOf(device), "IMG_0009.HEIC", 5, canDelete: false);
        using var c = _wpd.Open(device.PnpId, readOnly: true);
        var map = new WpdDcimMap(WpdDcimWalker.Walk(c, Ct, out _)!, [.. _roll.Disk.Keys, "DCIM/100APPLE/IMG_0009.HEIC"]);

        Assert.True(map.ByFolder);
        Assert.Equal("IMG_0002.HEIC", map.Find("DCIM/100APPLE/IMG_0002.HEIC", 1002).Object?.FileName);
        Assert.Equal(WpdMapProblem.SizeDiffers, map.Find("DCIM/100APPLE/IMG_0002.HEIC", 999).Problem);
        Assert.Equal(WpdMapProblem.NoFolder, map.Find("DCIM/101APPLE/IMG_0002.HEIC", 1002).Problem);
        Assert.Equal(WpdMapProblem.Absent, map.Find("DCIM/100APPLE/IMG_0404.HEIC", 1002).Problem);
        Assert.Equal(WpdMapProblem.ReadOnly, map.Find("DCIM/100APPLE/IMG_0009.HEIC", 5).Problem);
        Assert.Equal(WpdMapProblem.NotDcim, map.Find("PhotoData/Photos.sqlite", 5).Problem);
        Assert.Equal(WpdMapProblem.NotDcim, map.Find("DCIM/../Photos.sqlite", 5).Problem);
        Assert.Equal(WpdMapProblem.NotDcim, map.Find("DCIM/100APPLE", 5).Problem);
        Assert.Equal(WpdMapProblem.NotDcim, map.Find("DCIM/100APPLE/IMG_0001.AAE", 5).Problem);
    }

    [Fact]
    public void With_month_folders_maps_by_name_and_size_and_refuses_a_name_used_twice()
    {
        // Recent iOS shows the PC month folders ("202510_a") instead of 100APPLE.
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 10).Add("DCIM/100APPLE/IMG_0002.HEIC", 20).Add("DCIM/101APPLE/IMG_0002.HEIC", 30);
        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 10), ("100APPLE", "IMG_0002.HEIC", 20), ("101APPLE", "IMG_0002.HEIC", 30)],
            wpdFolder: f => f == "100APPLE" ? "202509__" : "202510_a");
        using var c = _wpd.Open(device.PnpId, readOnly: true);
        var map = new WpdDcimMap(WpdDcimWalker.Walk(c, Ct, out _)!, _roll.Disk.Keys);

        Assert.False(map.ByFolder);
        Assert.True(map.Find("DCIM/100APPLE/IMG_0001.HEIC", 10).Ok);
        Assert.Equal(WpdMapProblem.SizeDiffers, map.Find("DCIM/100APPLE/IMG_0001.HEIC", 11).Problem);
        Assert.Equal(WpdMapProblem.Ambiguous, map.Find("DCIM/100APPLE/IMG_0002.HEIC", 20).Problem);
        Assert.Equal(WpdMapProblem.Ambiguous, map.Find("DCIM/101APPLE/IMG_0002.HEIC", 30).Problem);
    }

    /// <summary>
    /// As seen on an iPhone 16 with iOS 27: pictures saved into Photos from elsewhere (LocalSend) are IMG_7414.PNG… on the
    /// phone but PZHI6539.PNG… to Windows, in month folders straight under the storage, with no can-delete flag.
    /// </summary>
    private (FakeWpdDevice Device, DateTimeOffset Taken) RenamedImports(bool twin = false)
    {
        var taken = new DateTimeOffset(2026, 10, 21, 13, 42, 44, TimeSpan.Zero);
        var shown = taken.ToOffset(TimeSpan.FromHours(3.5));
        _roll.Add("DCIM/107APPLE/IMG_7413.HEIC", 3_013_973).Add("DCIM/107APPLE/IMG_7414.PNG", 35_861).Add("DCIM/107APPLE/IMG_7415.PNG", 36_216);
        _roll.Taken["DCIM/107APPLE/IMG_7414.PNG"] = taken;
        _roll.Taken["DCIM/107APPLE/IMG_7415.PNG"] = taken;
        var device = _wpd.AddIphone(Udid);
        device.Commands.Add(WpdCommands.DeleteObjects);
        var storage = device.AddStorage();
        var month = device.AddFolder(storage, "202610_a");
        device.AddFile(month, "IMG_7413.HEIC", 3_013_973, canDelete: null, "DCIM/107APPLE/IMG_7413.HEIC");
        device.AddFile(month, "PZHI6539.PNG", 35_861, canDelete: null, "DCIM/107APPLE/IMG_7414.PNG", shown);
        device.AddFile(month, "HGGS6324.PNG", 36_216, canDelete: null, "DCIM/107APPLE/IMG_7415.PNG", shown);
        if (twin)
        {
            device.AddFile(month, "QQQQ0001.PNG", 35_861, canDelete: null, "DCIM/107APPLE/IMG_9999.PNG", shown);
        }

        device.Deleted = (_, path) => _roll.Disk.Remove(path);
        return (device, taken);
    }

    [Fact]
    public async Task A_picture_windows_shows_under_another_name_maps_by_type_size_and_time_and_is_deleted()
    {
        var (device, _) = RenamedImports();
        var deleter = Deleter();
        Assert.True((await deleter.CheckAsync(DeviceState.Ready, Ct)).CanDelete); // no can-delete flag, but the phone deletes objects

        var results = await deleter.DeleteAsync([new PhotoDeleteItem("DCIM/107APPLE/IMG_7414.PNG", 35_861), new PhotoDeleteItem("DCIM/107APPLE/IMG_7415.PNG", 36_216)], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Contains("Windows calls it PZHI6539.PNG", results[0].Message);
        Assert.Equal(["DCIM/107APPLE/IMG_7413.HEIC"], _roll.Disk.Keys);
        Assert.True(device.Has("IMG_7413.HEIC"));
        Assert.Empty(device.Forbidden);
    }

    [Fact]
    public async Task A_renamed_picture_that_another_object_could_be_is_refused()
    {
        var (device, _) = RenamedImports(twin: true);

        var results = await Deleter().DeleteAsync([new PhotoDeleteItem("DCIM/107APPLE/IMG_7414.PNG", 35_861), new PhotoDeleteItem("DCIM/107APPLE/IMG_7415.PNG", 36_216)], Ct);

        Assert.Equal([PhotoDeleteOutcome.Mismatch, PhotoDeleteOutcome.NotTried], results.Select(r => r.Outcome));
        Assert.Contains("2 objects", results[0].Message);
        Assert.Empty(device.DeleteCalls);
    }

    [Fact]
    public async Task Without_photos_sqlite_a_renamed_picture_is_not_matched()
    {
        var (device, _) = RenamedImports();
        _roll.WithDatabase = false;

        var r = Assert.Single(await Deleter().DeleteAsync([new PhotoDeleteItem("DCIM/107APPLE/IMG_7414.PNG", 35_861)], Ct));

        Assert.Equal(PhotoDeleteOutcome.Mismatch, r.Outcome);
        Assert.Empty(device.DeleteCalls);
    }

    [Fact]
    public async Task A_phone_that_doesnt_mark_its_files_and_doesnt_say_it_deletes_stays_read_only()
    {
        var (device, _) = RenamedImports();
        device.Commands.Clear();
        Assert.Equal(PhotoDeleteBlock.ReadOnly, (await Deleter().CheckAsync(DeviceState.Ready, Ct)).Reason);
    }

    [Fact]
    public void Camera_folders_straight_under_the_storage_are_the_camera_roll()
    {
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 10);
        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 10)], wpdFolder: _ => "202510_a", noDcimLevel: true);
        using var c = _wpd.Open(device.PnpId, readOnly: true);

        var dcim = WpdDcimWalker.Walk(c, Ct, out var missing);
        Assert.Equal(WpdDcimWalker.Missing.None, missing);
        Assert.Equal(dcim!.StorageId, dcim.DcimId);
        Assert.True(new WpdDcimMap(dcim, _roll.Disk.Keys).Find("DCIM/100APPLE/IMG_0001.HEIC", 10).Ok);
        Assert.True(WpdDcimWalker.IsCameraFolderName("100APPLE") && WpdDcimWalker.IsCameraFolderName("2021__06"));
        Assert.False(WpdDcimWalker.IsCameraFolderName("Documents"));
    }

    // ---------- Deleting ----------

    [Fact]
    public async Task Deletes_exactly_the_confirmed_photos_in_small_batches_and_logs_each()
    {
        var device = Library(12);
        var items = new[] { 2, 3, 5, 7, 8, 9, 11 }.Select(Item).ToList();

        var results = await Deleter().DeleteAsync(items, Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal(items.Select(i => i.Id), results.Select(r => r.Id));
        Assert.Equal([5, 2], device.DeleteCalls.Select(c => c.Count));
        Assert.Equal(["IMG_0001.HEIC", "IMG_0004.HEIC", "IMG_0006.HEIC", "IMG_0010.HEIC", "IMG_0012.HEIC"],
            _roll.Disk.Keys.Select(Path.GetFileName).Order());
        Assert.Empty(device.Forbidden);
        Assert.Equal(1, _wpd.ReadWriteOpens);
        Assert.All(items, i => Assert.Contains(_log.Written, l => l.Contains($"deleted {i.Id} ({i.Size:N0} bytes)", StringComparison.Ordinal)));
        Assert.All(results, r => Assert.Equal(PhotoDeleteDestination.Permanent, r.WentTo));
    }

    [Fact]
    public async Task A_mismatch_anywhere_means_nothing_is_deleted()
    {
        var device = Library(4);
        var items = new[] { Item(1), Item(2) with { Size = 5 }, Item(3), new PhotoDeleteItem("DCIM/100APPLE/IMG_0404.HEIC", 1) };

        var results = await Deleter().DeleteAsync(items, Ct);

        Assert.Equal([PhotoDeleteOutcome.NotTried, PhotoDeleteOutcome.Mismatch, PhotoDeleteOutcome.NotTried, PhotoDeleteOutcome.Mismatch],
            results.Select(r => r.Outcome));
        Assert.Contains("changed", results[1].Message);
        Assert.Empty(device.DeleteCalls);
        Assert.Equal(0, _wpd.ReadWriteOpens);
        Assert.Equal(4, _roll.Disk.Count);
    }

    [Fact]
    public async Task A_photo_windows_lists_at_another_size_is_refused_before_anything_goes()
    {
        var files = new[] { ("100APPLE", "IMG_0001.HEIC", 1001L), ("100APPLE", "IMG_0002.HEIC", 999L) };
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 1001).Add("DCIM/100APPLE/IMG_0002.HEIC", 1002);
        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll(files);

        var results = await Deleter().DeleteAsync([Item(1), Item(2)], Ct);

        Assert.Equal([PhotoDeleteOutcome.NotTried, PhotoDeleteOutcome.Mismatch], results.Select(r => r.Outcome));
        Assert.Contains("999", results[1].Message);
        Assert.Empty(device.DeleteCalls);
    }

    [Fact]
    public async Task Stops_at_the_first_failure_and_reports_the_rest_not_tried()
    {
        var device = Library(12);
        device.Answer = o => o.FileName == "IMG_0007.HEIC" ? WpdErrors.AccessDenied : 0;

        var results = await Deleter().DeleteAsync(Enumerable.Range(1, 12).Select(Item).ToList(), Ct);

        // Batches of five: the second one (6-10) went in one call, so 8-10 went with it; the third never ran.
        Assert.Equal(Enumerable.Repeat(PhotoDeleteOutcome.Deleted, 6), results.Take(6).Select(r => r.Outcome));
        Assert.Equal(PhotoDeleteOutcome.Failed, results[6].Outcome);
        Assert.Equal(DeviceErrorCodes.PermissionDenied, results[6].Code);
        Assert.Equal(Enumerable.Repeat(PhotoDeleteOutcome.Deleted, 3), results.Skip(7).Take(3).Select(r => r.Outcome));
        Assert.All(results.Skip(10), r => Assert.Equal(PhotoDeleteOutcome.NotTried, r.Outcome));
        Assert.Equal(2, device.DeleteCalls.Count);
        Assert.True(_roll.Disk.ContainsKey(Item(7).Id) && _roll.Disk.ContainsKey(Item(11).Id) && _roll.Disk.ContainsKey(Item(12).Id));
        Assert.Contains(_log.Written, l => l.Contains("failed DCIM/100APPLE/IMG_0007.HEIC", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delete_that_went_through_stays_deleted_when_windows_answers_an_error_for_the_gone_object()
    {
        var device = Library(6);
        device.GoneObjectError = unchecked((int)0x80042009); // an undocumented answer for "no such object"

        var results = await Deleter().DeleteAsync(Enumerable.Range(1, 6).Select(Item).ToList(), Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome));
        Assert.Equal(2, device.DeleteCalls.Count);
        Assert.Empty(_roll.Disk);
    }

    [Fact]
    public async Task An_object_that_changed_just_before_its_batch_stops_the_delete()
    {
        var device = Library(7);
        var deleter = Deleter();
        // After the first batch, IMG_0006 disappears from Windows' list (someone deleted it on the phone).
        var calls = 0;
        device.Deleted = (o, path) =>
        {
            _roll.Disk.Remove(path);
            if (++calls == 5)
            {
                device.Remove(device.IdOf("IMG_0006.HEIC"));
            }
        };

        var results = await deleter.DeleteAsync(Enumerable.Range(1, 7).Select(Item).ToList(), Ct);

        Assert.Equal(5, results.Count(r => r.Outcome == PhotoDeleteOutcome.Deleted));
        Assert.Equal(PhotoDeleteOutcome.Mismatch, results[5].Outcome);
        Assert.Equal(PhotoDeleteOutcome.NotTried, results[6].Outcome);
        Assert.Single(device.DeleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_live_photo_goes_with_its_video(bool stillTakesItsVideo)
    {
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 3000).Add("DCIM/100APPLE/IMG_0001.MOV", 2000).Add("DCIM/100APPLE/IMG_0002.HEIC", 1002);
        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 3000), ("100APPLE", "IMG_0001.MOV", 2000), ("100APPLE", "IMG_0002.HEIC", 1002)]);
        device.StillTakesItsVideo = stillTakesItsVideo;
        device.Deleted = (_, path) => _roll.Disk.Remove(path);

        var results = await Deleter().DeleteAsync([new PhotoDeleteItem("DCIM/100APPLE/IMG_0001.HEIC", 3000)], Ct);

        var r = Assert.Single(results);
        Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome);
        Assert.Equal(["IMG_0001.MOV"], r.Also);
        Assert.Equal(5000, r.Size);
        Assert.Equal(2, device.DeleteCalls.Single().Count); // still and video in one call
        Assert.Equal(["DCIM/100APPLE/IMG_0002.HEIC"], _roll.Disk.Keys);
        Assert.Contains(_log.Written, l => l.Contains("deleted DCIM/100APPLE/IMG_0001.MOV (2,000 bytes), the Live Photo's video", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_live_photo_whose_video_windows_doesnt_list_goes_alone_with_a_note()
    {
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 3000).Add("DCIM/100APPLE/IMG_0001.MOV", 2000);
        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 3000)]);
        device.Deleted = (_, path) => _roll.Disk.Remove(path);

        var r = Assert.Single(await Deleter().DeleteAsync([new PhotoDeleteItem("DCIM/100APPLE/IMG_0001.HEIC", 3000)], Ct));

        Assert.Equal(PhotoDeleteOutcome.Deleted, r.Outcome);
        Assert.Empty(r.Also);
        Assert.Contains("Windows doesn't list its Live Photo video", r.Message);
    }

    [Fact]
    public async Task A_live_photo_whose_video_windows_lists_at_another_size_is_refused()
    {
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 3000).Add("DCIM/100APPLE/IMG_0001.MOV", 2000);
        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 3000), ("100APPLE", "IMG_0001.MOV", 1999)]);

        var r = Assert.Single(await Deleter().DeleteAsync([new PhotoDeleteItem("DCIM/100APPLE/IMG_0001.HEIC", 3000)], Ct));

        Assert.Equal(PhotoDeleteOutcome.Mismatch, r.Outcome);
        Assert.Contains("Live Photo video doesn't match", r.Message);
        Assert.Empty(device.DeleteCalls);
    }

    [Fact]
    public async Task An_edited_photo_reports_what_happened_to_the_edit_windows_listed()
    {
        var device = Library(2);
        _roll.Edited.Add(Item(1).Id);
        device.AddFile(FolderOf(device), "IMG_E0001.JPG", 777);
        device.AddFile(FolderOf(device), "IMG_0001.AAE", 3);

        var results = await Deleter().DeleteAsync([Item(1)], Ct);

        Assert.Equal(PhotoDeleteOutcome.Deleted, results[0].Outcome);
        Assert.Contains("Windows still lists IMG_E0001.JPG, IMG_0001.AAE", results[0].Message);
        Assert.Single(device.DeleteCalls.Single()); // the edit itself isn't deleted by DeviceDeck
    }

    [Fact]
    public async Task Where_deleted_photos_went_comes_from_the_phone()
    {
        var device = Library(3);
        // Kept on disk and marked deleted in the library: Recently Deleted.
        device.Deleted = (_, path) => _roll.Trashed.Add(path);
        var deleter = Deleter();
        Assert.Equal((PhotoDeleteDestination.Permanent, false), (deleter.Current.GoesTo, deleter.Current.Observed)); // as users report

        var results = await deleter.DeleteAsync([Item(1), Item(2)], Ct);

        Assert.All(results, r => Assert.Equal(PhotoDeleteDestination.RecentlyDeleted, r.WentTo));
        Assert.Equal((PhotoDeleteDestination.RecentlyDeleted, true), (deleter.Current.GoesTo, deleter.Current.Observed));
        Assert.Contains(_log.Written, l => l.Contains("marks it deleted (Recently Deleted)", StringComparison.Ordinal));
        Assert.True(_roll.Refreshes >= 2); // the index was rebuilt after the delete
    }

    [Fact]
    public async Task Without_photos_sqlite_the_files_own_size_is_what_was_confirmed()
    {
        _roll.WithDatabase = false;
        var device = Library(2);

        var results = await Deleter().DeleteAsync([Item(1), Item(2) with { Size = 1 }], Ct);

        Assert.Equal([PhotoDeleteOutcome.NotTried, PhotoDeleteOutcome.Mismatch], results.Select(r => r.Outcome));
        Assert.Equal(PhotoDeleteOutcome.Deleted, Assert.Single(await Deleter().DeleteAsync([Item(1)], Ct)).Outcome);
        Assert.Single(device.DeleteCalls);
    }

    [Fact]
    public async Task Photos_in_icloud_only_are_refused()
    {
        var device = Library(1);
        _roll.CloudOnly["DCIM/100APPLE/IMG_0002.HEIC"] = 1002; // the library lists it, the phone keeps only a small version

        var r = Assert.Single(await Deleter().DeleteAsync([Item(2)], Ct));

        Assert.Equal(PhotoDeleteOutcome.Mismatch, r.Outcome);
        Assert.Contains("iCloud only", r.Message);
        Assert.Empty(device.DeleteCalls);
    }

    [Fact]
    public async Task A_device_that_refuses_the_whole_call_fails_the_first_item_and_stops()
    {
        var device = Library(3);
        _wpd.Devices.Clear(); // Windows lost the camera between the check and the delete

        var results = await Deleter().DeleteAsync([Item(1), Item(2)], Ct);

        Assert.Equal([PhotoDeleteOutcome.Failed, PhotoDeleteOutcome.NotTried], results.Select(r => r.Outcome));
        Assert.Empty(device.DeleteCalls);
    }

    // ---------- Can this phone delete? ----------

    [Fact]
    public async Task A_phone_windows_shows_as_deletable_can_delete_and_the_check_only_reads()
    {
        Library(5);
        var deleter = Deleter();

        var support = await deleter.CheckAsync(DeviceState.Ready, Ct);

        Assert.True(support.CanDelete, support.Detail);
        Assert.Equal(PhotoDeleteBlock.None, support.Reason);
        Assert.Equal(PhotoDeleteDestination.Permanent, support.GoesTo);
        Assert.Equal(0, _wpd.ReadWriteOpens);
        Assert.True(_wpd.ReadOnlyOpens > 0);
    }

    [Fact]
    public async Task The_phone_is_found_by_its_wpd_serial_when_the_usb_serial_is_unknown()
    {
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 1);
        var other = _wpd.AddIphone("00008110-111111111111111E");
        other.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 1)]);
        var mine = _wpd.AddIphone(Udid, usbSerial: null, wpdSerial: Udid.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant());
        mine.AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 1)]);

        Assert.True((await Deleter().CheckAsync(DeviceState.Ready, Ct)).CanDelete);
    }

    [Fact]
    public async Task Why_a_phone_cant_delete()
    {
        Assert.Equal(PhotoDeleteBlock.Unsupported, (await new WpdPhotoDeleter(null, Udid, _roll, _log).CheckAsync(DeviceState.Ready, Ct)).Reason);
        Assert.Equal(PhotoDeleteBlock.Locked, (await Deleter().CheckAsync(DeviceState.Locked, Ct)).Reason);

        // Windows doesn't list it as a camera.
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 1);
        _wpd.AddIphone("00008110-111111111111111E").AddCameraRoll([("100APPLE", "IMG_0001.HEIC", 1)]);
        var notCamera = await Deleter().CheckAsync(DeviceState.Ready, Ct);
        Assert.Equal(PhotoDeleteBlock.NotCamera, notCamera.Reason);
        Assert.Contains("1 from Apple", notCamera.Detail);

        // Listed, but no storage yet / an empty one: locked or not trusted.
        var mine = _wpd.AddIphone(Udid);
        Assert.Equal(PhotoDeleteBlock.Locked, (await Deleter().CheckAsync(DeviceState.Ready, Ct)).Reason);
        mine.AddStorage();
        Assert.Equal(PhotoDeleteBlock.Locked, (await Deleter().CheckAsync(DeviceState.Ready, Ct)).Reason);
    }

    [Theory]
    [InlineData(true, PhotoDeleteBlock.ICloudPhotos)]
    [InlineData(null, PhotoDeleteBlock.ReadOnly)]
    public async Task Photos_marked_read_only_give_icloud_or_read_only(bool? icloud, PhotoDeleteBlock expected)
    {
        Library(3, canDelete: false);
        _roll.ICloud = icloud;

        var support = await Deleter().CheckAsync(DeviceState.Ready, Ct);

        Assert.False(support.CanDelete);
        Assert.Equal(expected, support.Reason);
        Assert.Contains("0 of 3 sampled files", support.Detail);
    }

    [Fact]
    public async Task Photos_windows_doesnt_say_can_be_deleted_are_not_deletable()
    {
        Library(3, canDelete: null);
        Assert.Equal(PhotoDeleteBlock.ReadOnly, (await Deleter().CheckAsync(DeviceState.Ready, Ct)).Reason);
    }

    [Fact]
    public async Task A_phone_that_converts_photos_for_the_pc_is_refused()
    {
        _roll.Add("DCIM/100APPLE/IMG_0001.HEIC", 3000).Add("DCIM/100APPLE/IMG_0002.HEIC", 3000);
        var device = _wpd.AddIphone(Udid);
        device.AddCameraRoll([("100APPLE", "IMG_0001.JPG", 1500), ("100APPLE", "IMG_0002.JPG", 1500)], wpdFolder: _ => "202510_a");

        var support = await Deleter().CheckAsync(DeviceState.Ready, Ct);

        Assert.Equal(PhotoDeleteBlock.Converted, support.Reason);
        Assert.True(WpdPhotoDeleter.IsEditedName("IMG_E0001.JPG"));
        Assert.False(WpdPhotoDeleter.IsEditedName("IMG_0001.JPG"));
    }

    [Fact]
    public void Serials_compare_without_dashes_or_case()
    {
        Assert.Equal(WpdPhotoDeleter.Normalize("00008140-000a1b2c3d4e801e"), WpdPhotoDeleter.Normalize("00008140000A1B2C3D4E801E"));
        Assert.Equal("", WpdPhotoDeleter.Normalize(null));
        Assert.Equal(@"USB\VID_05AC&PID_12A8&MI_00\7&2A7C6B1C&0&0000",
            WpdPnp.InstanceIdOf(@"\\?\usb#vid_05ac&pid_12a8&mi_00#7&2a7c6b1c&0&0000#{6ac27878-a6fa-4155-ba85-f98f491d4f33}"));
        Assert.Equal("00008140000A1B2C3D4E801E", WpdPnp.SerialFromInstanceId(@"USB\VID_05AC&PID_12A8\00008140000A1B2C3D4E801E"));
        Assert.Null(WpdPnp.SerialFromInstanceId(@"USB\VID_05AC&PID_12A8&MI_00\7&2A7C6B1C&0&0000"));
    }
}

/// <summary>The real COM declarations against Windows' own WPD objects (in-process; no device needed).</summary>
public sealed class ComWpdInteropTests
{
    [Fact]
    public void Values_keys_and_ids_round_trip_through_windows_portable_devices()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.Equal(IntPtr.Size == 8 ? 24 : 20, System.Runtime.InteropServices.Marshal.SizeOf<PropVariant>());
        var (name, size, canDelete, count, second, id, error) = ComWpdBackend.SelfTest();

        Assert.Equal("IMG_0001.HEIC", name);
        Assert.Equal(1234, size);
        Assert.Null(canDelete); // not set: missing, not false
        Assert.Equal(2u, count);
        Assert.Equal((new Guid("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C"), 11u), (second.FormatId, second.Pid)); // WPD_OBJECT_SIZE
        Assert.Equal("o1A2B", id);
        Assert.Equal(WpdErrors.AccessDenied, error);
    }

    [Fact]
    public void Listing_devices_works_on_this_pc()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Read-only; the list may be empty (no phone is plugged in while the tests run).
        Assert.NotNull(new ComWpdBackend().ListDevices());
    }
}
