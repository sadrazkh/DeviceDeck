using System.Globalization;
using System.Text.RegularExpressions;
using DeviceDeck.Bridge.Tests.Support;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Battery;

namespace DeviceDeck.Bridge.Tests;

/// <summary>Battery history: the SQLite store, a snapshot per connect, the exported report and the bridge methods.</summary>
public sealed class BatteryTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset T = new(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(3.5));

    private static BatterySnapshot Snap(string device, DateTimeOffset at, int? health, int? cycles, DevicePlatform platform = DevicePlatform.Ios, string name = "Phone") =>
        new(0, device, name, platform, "iPhone 16", "27.0", at, 80, false, health, platform == DevicePlatform.Ios, cycles, 3561, health is { } h ? 3561 * h / 100 : null, 30.5);

    private static string TempFile(string name)
    {
        var folder = Path.Combine(Path.GetTempPath(), "DeviceDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, name);
    }

    [Fact]
    public void Store_keeps_snapshots_per_phone_oldest_first_across_a_restart()
    {
        var path = TempFile("battery.db");
        using (var store = new BatteryHistoryStore(path))
        {
            var a1 = store.Add(Snap("a", T.AddDays(10), 95, 100));
            store.Add(Snap("a", T, 99, 10));
            store.Add(Snap("b", T.AddDays(20), 90, 300, DevicePlatform.Android, "Pixel") with { HealthEstimated = false, TemperatureCelsius = null });
            Assert.True(a1.Id > 0);
        }

        using var again = new BatteryHistoryStore(path);
        var history = again.List("a");
        Assert.Equal([99, 95], history.Select(s => s.HealthPercent));
        Assert.Equal(T, history[0].At);
        Assert.True(history[0].HealthEstimated);
        Assert.Equal(30.5, history[0].TemperatureCelsius);
        Assert.Equal(95, again.Latest("a")!.HealthPercent);
        Assert.Null(again.Latest("nope"));

        var devices = again.Devices();
        Assert.Equal(["b", "a"], devices.Select(d => d.DeviceId));
        Assert.Equal("Pixel", devices[0].Name);
        Assert.Equal(DevicePlatform.Android, devices[0].Platform);
        Assert.Null(devices[0].Latest.TemperatureCelsius);
        Assert.Equal(2, devices[1].Count);
        Assert.Equal(T, devices[1].FirstAt);
    }

    [Fact]
    public async Task A_snapshot_is_recorded_when_a_phone_connects_and_not_again_within_the_interval()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Iphone(), CleanupPhones.Android());
        var store = host.Battery.Store;
        await Wait.UntilAsync(() => store.List(CleanupPhones.IphoneId).Count == 1 && store.List(CleanupPhones.AndroidId).Count == 1, "a snapshot per phone");

        var iphone = store.List(CleanupPhones.IphoneId)[0];
        Assert.True(iphone.HealthEstimated);
        Assert.Equal(88, iphone.HealthPercent);
        Assert.Equal(530, iphone.CycleCount);
        Assert.Equal(3133, iphone.FullChargeCapacityMah);
        Assert.Equal("iPhone 16", iphone.Model);
        Assert.Equal("27.0", iphone.OsVersion);
        var android = store.List(CleanupPhones.AndroidId)[0];
        Assert.False(android.HealthEstimated);
        Assert.Equal(31.5, android.TemperatureCelsius);
        Assert.True(android.IsCharging);

        // A reconnect inside 30 minutes adds nothing.
        host.Fake.Disconnect(CleanupPhones.IphoneId);
        host.Fake.Reconnect(CleanupPhones.IphoneId);
        await Wait.UntilAsync(() => host.Hub.ListDevices().Any(d => d.Id == CleanupPhones.IphoneId && d.State == DeviceState.Ready), "the iPhone back");
        await Task.Delay(200);
        Assert.Single(store.List(CleanupPhones.IphoneId));

        // "Record now" ignores the interval, and the page hears about it.
        var now = await host.ResultAsync("battery.record", new { deviceId = CleanupPhones.IphoneId });
        Assert.Equal(88, (int)now!["healthPercent"]!);
        Assert.Equal(2, store.List(CleanupPhones.IphoneId).Count);
        await Wait.UntilAsync(() => host.Transport.EventsNamed("battery.recorded").Any(), "battery.recorded");
    }

    [Fact]
    public async Task Every_connect_is_recorded_when_the_interval_allows()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Android());
        var id = CleanupPhones.AndroidId;
        await using var recorder = new BatteryRecorder(host.Hub, new BatteryHistoryStore(TempFile("battery.db")),
            new BatteryRecorderOptions { SettleDelay = TimeSpan.Zero, MinInterval = TimeSpan.Zero });
        await Wait.UntilAsync(() => recorder.Store.List(id).Count == 1, "the snapshot of the phone already connected");

        for (var i = 0; i < 2; i++)
        {
            host.Fake.Disconnect(id);
            await Wait.UntilAsync(() => host.Hub.ListDevices().All(d => d.Id != id || d.State != DeviceState.Ready), "the phone gone");
            host.Fake.Reconnect(id);
            var expected = i + 2;
            await Wait.UntilAsync(() => recorder.Store.List(id).Count == expected, $"snapshot {expected}");
        }

        // A phone that isn't ready when the settle delay ends is skipped, not an error.
        var failures = 0;
        recorder.RecordFailed += (_, _) => failures++;
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Report_shows_the_latest_figures_charts_and_every_reading_and_labels_iPhone_health_estimated()
    {
        var history = new[] { Snap("a", T, 99, 12, name: "Nima's iPhone"), Snap("a", T.AddMonths(4), 95, 180, name: "Nima's iPhone"), Snap("a", T.AddMonths(8), 91, 350, name: "Nima's iPhone") };
        var html = BatteryReport.Render(history, "en", T.AddMonths(8), "0.2.0");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("lang=\"en\" dir=\"ltr\"", html);
        Assert.Contains("Nima&#39;s iPhone", html);
        Assert.Contains("Health (estimated)", html);
        Assert.Contains("91%", html);
        Assert.Contains("−8 points since", html);
        Assert.Contains("Maximum Capacity", html);
        Assert.Equal(3, html.Split("<path class=\"line\"").Length - 1); // health, capacity, cycles
        Assert.Contains("class=\"reference\"", html); // the design capacity line
        Assert.Equal(3, html.Split("<tr><td>").Length - 1);
        Assert.DoesNotContain("<script", html);

        var android = history.Select(s => s with { Platform = DevicePlatform.Android, HealthEstimated = false }).ToArray();
        Assert.DoesNotContain("estimated", BatteryReport.Render(android, "en", T, "0.2.0"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Persian_report_is_right_to_left_with_Persian_digits()
    {
        var html = BatteryReport.Render([Snap("a", T, 88, 530)], "fa", T, "0.2.0");
        Assert.Contains("lang=\"fa\" dir=\"rtl\"", html);
        Assert.Contains("گزارش باتری", html);
        Assert.Contains("۸۸٪", html);
        Assert.Contains("سلامت (تخمینی)", html);
        Assert.DoesNotContain(">88%<", html);

        // Time runs right to left, as in the app: the older reading sits further right.
        var two = BatteryReport.Render([Snap("a", T, 90, 500), Snap("a", T.AddMonths(3), 88, 530)], "fa", T, "0.2.0");
        var xs = Regex.Matches(two, "<circle class=\"dot\" cx=\"([0-9.]+)\"").Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.True(xs[0] > xs[1], $"oldest at {xs[0]}, newest at {xs[1]}");
    }

    [Theory]
    [InlineData(88, 99, null, 85, 100, 5)]
    [InlineData(3000, 3561, null, 3000, 3600, 200)]
    [InlineData(0, 530, null, 0, 600, 200)]
    [InlineData(91, 91, 100, 90, 92, 1)]
    [InlineData(96, 100, 100, 96, 100, 1)]
    public void Chart_axes_use_clean_steps(double min, double max, int? cap, double lo, double hi, double step)
    {
        var range = BatteryReport.NiceRange(min, max, cap);
        Assert.Equal((lo, hi, step), range);
    }

    [Fact]
    public async Task Bridge_lists_phones_with_history_and_exports_the_report_to_a_folder()
    {
        await using var host = await TestHost.StartAsync(CleanupPhones.Iphone());
        var id = CleanupPhones.IphoneId;
        await Wait.UntilAsync(() => host.Battery.Store.List(id).Count == 1, "the first snapshot");

        var devices = (await host.ResultAsync("battery.devices"))!.AsArray();
        var phone = devices.Single()!.AsObject();
        Assert.Equal(["deviceId", "name", "platform", "model", "count", "firstAt", "lastAt", "latest"], phone.Select(p => p.Key));
        Assert.Equal("ios", (string?)phone["platform"]);
        var snapshot = phone["latest"]!.AsObject();
        Assert.Equal(["id", "deviceId", "deviceName", "platform", "model", "osVersion", "at", "levelPercent", "isCharging", "healthPercent",
            "healthEstimated", "cycleCount", "designCapacityMah", "fullChargeCapacityMah", "temperatureCelsius"], snapshot.Select(p => p.Key));

        var history = (await host.ResultAsync("battery.history", new { deviceId = id }))!.AsArray();
        Assert.Single(history);

        var folder = Path.Combine(host.PcFolder("reports"), "new");
        var first = (string)(await host.ResultAsync("battery.export", new { deviceId = id, destination = folder, language = "fa" }))!["path"]!;
        var second = (string)(await host.ResultAsync("battery.export", new { deviceId = id, destination = folder }))!["path"]!;
        Assert.NotEqual(first, second); // never replaces a report already there
        Assert.Contains("dir=\"rtl\"", await File.ReadAllTextAsync(first));
        Assert.Contains("lang=\"en\" dir=\"ltr\"", await File.ReadAllTextAsync(second)); // the app's language (English) when none is given
        Assert.StartsWith("Battery report - Clean iPhone - ", Path.GetFileName(first));

        var none = await host.CallAsync("battery.export", new { deviceId = "unknown", destination = folder });
        Assert.Equal(-32602, (int)none["error"]!["code"]!);
        var relative = await host.CallAsync("battery.export", new { deviceId = id, destination = "reports" });
        Assert.Equal(-32602, (int)relative["error"]!["code"]!);
    }
}
