using System.Text.Json;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Photos;
using DeviceDeck.Core.Serialization;

namespace DeviceDeck.Bridge.Tests;

/// <summary>The "from date" range: Everything / last N days / since a date, saved per phone with the ledger options.</summary>
public sealed class PhotosBackupRangeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Now;

    [Fact]
    public async Task The_last_30_days_leave_older_photos_out_of_the_count_and_the_backup()
    {
        var phone = new PhotosTestPhone();
        var recent = phone.Library.Add("IMG_0003.HEIC", Now.AddDays(-2));
        var edge = phone.Library.Add("IMG_0002.HEIC", Now.AddDays(-29));
        phone.Library.Add("IMG_0001.HEIC", Now.AddDays(-45));
        phone.Library.Add("IMG_0000.HEIC", Now.AddDays(-400));
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var id = phone.Summary.Id;

        host.Backup.Configure(id, null, null, null, null, range: PhotoBackupRange.LastDays(30));
        var count = await host.Backup.CountNewAsync(id, CancellationToken.None);
        Assert.Equal((2, 2, recent.Size + edge.Size), (count.NewCount, count.OutOfRange, count.NewBytes));

        var job = await host.BackUpAsync(phone);
        Assert.Equal(2, job.TotalItems);
        Assert.Equal(["IMG_0002.HEIC", "IMG_0003.HEIC"], host.Files(phone).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // The older ones were never backed up: still counted apart, and Everything brings them.
        Assert.Equal((0, 2), Counts(await host.Backup.CountNewAsync(id, CancellationToken.None)));
        host.Backup.Configure(id, null, null, null, null, range: PhotoBackupRange.Everything);
        Assert.Equal((2, 0), Counts(await host.Backup.CountNewAsync(id, CancellationToken.None)));
        Assert.Equal(2, (await host.BackUpAsync(phone)).TotalItems);
        Assert.Equal(4, host.Files(phone).Length);
    }

    [Fact]
    public async Task Since_a_date_starts_at_midnight_on_this_pc()
    {
        var phone = new PhotosTestPhone();
        var day = DateOnly.FromDateTime(Now.LocalDateTime).AddDays(-10);
        var midnight = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));
        phone.Library.Add("IMG_0002.HEIC", midnight);               // in: on the day itself
        phone.Library.Add("IMG_0001.HEIC", midnight.AddSeconds(-1)); // out: the evening before
        await using var host = await PhotosBackupHost.StartAsync(phone);

        host.Backup.Configure(phone.Summary.Id, null, null, null, null, range: PhotoBackupRange.From(day));
        Assert.Equal((1, 1), Counts(await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)));
        await host.BackUpAsync(phone);
        Assert.Equal(["IMG_0002.HEIC"], host.Files(phone).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Photos_the_list_has_no_date_for_are_kept_until_the_phone_says_when_they_were_taken()
    {
        var phone = new PhotosTestPhone();
        phone.Library.Add("IMG_0002.HEIC", Now.AddDays(-1));
        phone.Library.Add("IMG_0001.HEIC", Now.AddDays(-90));
        phone.Library.DatesNeedCompleting = true;
        await using var host = await PhotosBackupHost.StartAsync(phone);

        host.Backup.Configure(phone.Summary.Id, null, null, null, null, range: PhotoBackupRange.LastDays(30));
        // Without a date nothing can be left out yet.
        Assert.Equal((2, 0), Counts(await host.Backup.CountNewAsync(phone.Summary.Id, CancellationToken.None)));

        var job = await host.BackUpAsync(phone);
        Assert.Equal(1, job.TotalItems);
        Assert.Equal(["IMG_0002.HEIC"], host.Files(phone).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_range_is_kept_per_phone_survives_a_restart_and_goes_with_the_settings_defaults()
    {
        var phone = new PhotosTestPhone();
        var other = new PhotosTestPhone("photos-other", name: "Other iPhone");
        await using var host = await PhotosBackupHost.StartAsync(phone, more: new PhotosTestProvider(other) { Id = "photos-test-2" });

        var day = new DateOnly(2026, 9, 1);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, range: PhotoBackupRange.From(day));
        await host.RestartAsync();

        var status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal(PhotoBackupRange.From(day), status.Options.Range);
        Assert.True(status.Customized);
        Assert.Equal(PhotoBackupRange.Everything, (await host.Backup.GetStatusAsync(other.Summary.Id, CancellationToken.None)).Options.Range);

        // Changing something else keeps the range; going back to the defaults drops it.
        host.Backup.Configure(phone.Summary.Id, null, "", null, null);
        Assert.Equal(PhotoBackupRange.From(day), (await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None)).Options.Range);
        host.Backup.Configure(phone.Summary.Id, null, null, null, null, useDefaults: true);
        status = await host.Backup.GetStatusAsync(phone.Summary.Id, CancellationToken.None);
        Assert.Equal(PhotoBackupRange.Everything, status.Options.Range);
        Assert.False(status.Customized);
    }

    [Fact]
    public async Task The_bridge_takes_and_checks_the_range()
    {
        var phone = new PhotosTestPhone();
        await using var host = await PhotosBackupHost.StartAsync(phone);
        var bridge = new BridgeCalls(host.Transport);
        var id = phone.Summary.Id;

        var set = (await bridge.ResultAsync("photos.backupConfigure", new { deviceId = id, range = new { kind = "lastDays", days = 30 } }))!;
        Assert.Equal("""{"kind":"lastDays","days":30,"since":null}""", set["options"]!["range"]!.ToJsonString());
        set = (await bridge.ResultAsync("photos.backupConfigure", new { deviceId = id, range = new { kind = "since", since = "2026-09-01", days = 7 } }))!;
        Assert.Equal("""{"kind":"since","days":null,"since":"2026-09-01"}""", set["options"]!["range"]!.ToJsonString());

        foreach (var bad in new object[]
        {
            new { kind = "lastDays", days = 0 },
            new { kind = "lastDays" },
            new { kind = "since" },
            new { kind = "since", since = "1 September" },
            new { kind = "yesterday" },
        })
        {
            var response = await bridge.CallAsync("photos.backupConfigure", new { deviceId = id, range = bad });
            Assert.Equal(-32602, (int)response["error"]!["code"]!);
        }

        Assert.Equal("""{"kind":"since","days":null,"since":"2026-09-01"}""",
            (await bridge.ResultAsync("photos.backupStatus", new { deviceId = id }))!["options"]!["range"]!.ToJsonString());
    }

    [Fact]
    public void A_backup_queued_before_ranges_existed_copies_everything()
    {
        var request = JsonSerializer.Deserialize<PhotoBackupRequest>(
            """{"destination":"D:\\Backup","folderPattern":"yyyy/MM","convertHeic":false,"includeHidden":false}""", DeviceDeckJson.Options)!;
        Assert.Null(request.Range);
    }

    [Fact]
    public void Cutoffs_are_relative_to_now_and_since_is_local_midnight()
    {
        var tehran = TimeZoneInfo.CreateCustomTimeZone("test+0330", TimeSpan.FromMinutes(210), "test", "test");
        var now = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromMinutes(210));
        Assert.Null(PhotoBackupRange.Everything.Cutoff(now, tehran));
        Assert.Equal(now.AddDays(-30), PhotoBackupRange.LastDays(30).Cutoff(now, tehran));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromMinutes(210)), PhotoBackupRange.From(new DateOnly(2026, 9, 1)).Cutoff(now, tehran));

        var cutoff = PhotoBackupRange.LastDays(30).Cutoff(now, tehran);
        Assert.True(PhotoBackupRange.Includes(cutoff, null));
        Assert.True(PhotoBackupRange.Includes(cutoff, cutoff));
        Assert.False(PhotoBackupRange.Includes(cutoff, cutoff!.Value.AddTicks(-1)));
        Assert.True(PhotoBackupRange.Includes(null, DateTimeOffset.MinValue));
    }

    [Fact]
    public void A_range_keeps_only_the_field_its_kind_uses()
    {
        Assert.Equal(PhotoBackupRange.Everything, new PhotoBackupRange(PhotoBackupRangeKind.All, 3, new DateOnly(2026, 1, 1)).Validated());
        Assert.Equal(PhotoBackupRange.LastDays(7), new PhotoBackupRange(PhotoBackupRangeKind.LastDays, 7, new DateOnly(2026, 1, 1)).Validated());
        Assert.Throws<ArgumentException>(() => new PhotoBackupRange(PhotoBackupRangeKind.LastDays, PhotoBackupRange.MaxDays + 1).Validated());
        Assert.Throws<ArgumentException>(() => new PhotoBackupRange(PhotoBackupRangeKind.Since).Validated());
    }

    private static (int New, int OutOfRange) Counts(PhotoBackupCount count) => (count.NewCount, count.OutOfRange);
}
