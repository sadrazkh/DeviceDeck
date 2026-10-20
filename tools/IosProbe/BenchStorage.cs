using System.Diagnostics;
using DeviceDeck.Ios.Device;
using DeviceDeck.Ios.Device.Photos;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Services;

namespace IosProbe;

/// <summary>Times each source of the storage categories, to see what can answer within the Overview's budget.</summary>
internal static class BenchStorage
{
    /// <summary>
    /// What the host does when a phone turns Ready: Overview asks for storage while the Apps page lists apps with
    /// sizes, the Photos page builds its index and icons load. Prints when each finishes.
    /// </summary>
    public static async Task OverviewAsync(IosDevice device, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        async Task<string> Timed(string what, Func<Task<string>> work)
        {
            var result = await work();
            return $"  {sw.Elapsed.TotalSeconds,5:0.00} s  {what}: {result}";
        }
        string[] iconIds = ["com.apple.mobilesafari", "com.apple.mobileslideshow", "com.apple.camera", "com.apple.Preferences",
            "com.apple.MobileSMS", "com.apple.mobilephone", "com.apple.mobilemail", "com.apple.Maps", "com.apple.weather",
            "com.apple.mobilenotes", "com.apple.reminders", "com.apple.mobilecal", "com.apple.AppStore", "com.apple.Music",
            "com.apple.calculator", "com.apple.mobiletimer", "com.apple.Health", "com.apple.Passbook", "com.apple.facetime", "com.apple.podcasts"];
        var tasks = new List<Task<string>>
        {
            Timed("storage", async () => $"{(await device.GetStorageAsync(ct)).Volumes[0].Categories.Count} categories"),
            Timed("apps list with sizes", async () => $"{(await device.Apps.ListAsync(includeSystem: false, ct)).Count} apps"),
            Timed("photo index", async () => $"{(await ((IosPhotoLibrary)device.Photos).GetIndexAsync(refresh: false, ct)).Assets.Count} assets"),
            Timed("20 icons", async () =>
            {
                var icons = await Task.WhenAll(iconIds.Select(id => device.Apps.GetIconAsync(id, ct)));
                return $"{icons.Count(i => i is not null)} found";
            }),
        };
        foreach (var line in (await Task.WhenAll(tasks)).OrderBy(l => l, StringComparer.Ordinal))
            Console.WriteLine(line);
    }

    public static async Task RunAsync(IosDevice device, bool browse, CancellationToken ct)
    {
        var c = device.Connector();
        var sw = Stopwatch.StartNew();
        void Lap(string what, string result)
        {
            Console.WriteLine($"  {sw.Elapsed.TotalMilliseconds,7:0} ms  {what}: {result}");
            sw.Restart();
        }

        await using (var lockdown = await c.OpenSessionAsync(ct))
        {
            Lap("lockdown session", "ok");
            var usage = await lockdown.GetAllValuesAsync("com.apple.disk_usage", ct);
            Lap("disk_usage domain", $"{usage.Count} keys");
            foreach (var key in new[] { "MobileApplicationUsage", "AppUsage", "ApplicationUsage", "VideoUsage", "MovieUsage", "CameraUsage" })
            {
                var v = await lockdown.GetValueAsync("com.apple.disk_usage", key, ct);
                Lap($"disk_usage[{key}] by name", v?.ToString() ?? "(missing)");
            }
            var factory = await lockdown.GetAllValuesAsync("com.apple.disk_usage.factory", ct);
            Lap("disk_usage.factory domain", $"{factory.Count} keys");
            foreach (var key in new[] { "CameraUsage", "PhotoUsage", "MobileApplicationUsage" })
            {
                var v = await lockdown.GetValueAsync("com.apple.disk_usage.factory", key, ct);
                Lap($"disk_usage.factory[{key}] by name", v?.ToString() ?? "(missing)");
            }
            var again = await lockdown.GetAllValuesAsync("com.apple.disk_usage", ct);
            Lap("disk_usage domain again", $"{again.Count} keys");
        }

        // Browse with sizes takes the phone 9-13 s, so it is only timed on request.
        if (browse)
        foreach (var (type, attrs) in new (string, string[])[]
                 {
                     ("User", ["CFBundleIdentifier"]),
                     ("User", ["CFBundleIdentifier", "StaticDiskUsage", "DynamicDiskUsage"]),
                     ("Any", ["CFBundleIdentifier", "ApplicationType", "StaticDiskUsage", "DynamicDiskUsage"]),
                 })
        {
            await using var proxy = await c.StartServiceAsync(ServiceNames.InstallationProxy, ct);
            Lap("installation_proxy start", "ok");
            var apps = await InstallationProxyClient.BrowseAsync(proxy, type, ct, attrs);
            var bytes = apps.Sum(a => (a.GetLong("StaticDiskUsage") ?? 0) + (a.GetLong("DynamicDiskUsage") ?? 0));
            Lap($"Browse {type} [{string.Join(",", attrs)}]", $"{apps.Count} apps, {bytes / 1e9:0.00} GB");
        }

        await using var afc = await c.StartAfcAsync(ct);
        Lap("afc start", "ok");
        var files = new List<string>();
        foreach (var folder in await afc.ReadDirectoryAsync("/DCIM", ct))
        {
            if (folder.StartsWith('.'))
                continue;
            files.AddRange((await afc.ReadDirectoryAsync("/DCIM/" + folder, ct)).Where(PhotoIndex.IsMediaFile).Select(n => $"/DCIM/{folder}/{n}"));
        }
        Lap("DCIM listing", $"{files.Count} media files");
        var stems = files.Where(f => !PhotoIndex.IsVideoFile(f)).Select(f => Path.ChangeExtension(f, null)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var videos = files.Where(f => PhotoIndex.IsVideoFile(f) && !stems.Contains(Path.ChangeExtension(f, null))).ToList();
        long videoBytes = 0;
        foreach (var v in videos)
            videoBytes += (await afc.GetFileInfoAsync(v, ct))?.Size ?? 0;
        Lap("stat stand-alone videos", $"{videos.Count} files, {videoBytes / 1e9:0.00} GB");
    }
}
