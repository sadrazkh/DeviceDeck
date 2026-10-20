using System.Collections.Concurrent;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Fake;

internal sealed class FakeAppManager : IAppManager
{
    private readonly FakeDevice _device;
    private readonly List<FakeApp> _apps;
    private readonly ConcurrentDictionary<string, byte[]> _icons = new(StringComparer.Ordinal);

    public FakeAppManager(FakeDevice device)
    {
        _device = device;
        _apps = device.Spec.Apps.ToList();
    }

    private bool IsIos => _device.Summary.Platform == DevicePlatform.Ios;

    public Task<IReadOnlyList<AppInfo>> ListAsync(bool includeSystem, CancellationToken ct)
    {
        _device.EnsureConnected();
        _device.Require(DeviceCapability.AppsList, "list apps");
        lock (_device.Gate)
        {
            IReadOnlyList<AppInfo> apps = _apps
                .Select(a => a.Info)
                .Where(a => includeSystem || a.Source != AppSource.System)
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            return Task.FromResult(apps);
        }
    }

    public Task<byte[]?> GetIconAsync(string appId, CancellationToken ct)
    {
        _device.EnsureConnected();
        FakeApp? app;
        lock (_device.Gate)
        {
            app = _apps.FirstOrDefault(a => a.Info.Id == appId);
        }

        if (app is null)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return Task.FromResult<byte[]?>(_icons.GetOrAdd(appId, id => FakeArt.AppIcon(id, 128, app.Colour)));
    }

    public async Task InstallAsync(IReadOnlyList<string> localPaths, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        _device.EnsureConnected();
        _device.Require(DeviceCapability.AppsInstall, "install apps");
        if (localPaths.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, "Choose an app file to install.");
        }

        foreach (var path in localPaths)
        {
            if (!File.Exists(path))
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{Path.GetFileName(path)} isn't on this PC any more.", new FileNotFoundException(path));
            }
        }

        var first = Path.GetFileName(localPaths[0]);
        var ext = Path.GetExtension(first).ToLowerInvariant();
        if (IsIos ? (localPaths.Count != 1 || ext != ".ipa") : localPaths.Any(p => Path.GetExtension(p).ToLowerInvariant() is not (".apk" or ".apks" or ".xapk")))
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, IsIos ? "An iPhone installs one .ipa file." : "Android installs .apk, .apks or .xapk files.");
        }

        if (IsIos && first.Contains("unsigned", StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, "This .ipa isn't signed for this iPhone.", new InvalidOperationException("ApplicationVerificationFailed"));
        }

        var total = localPaths.Sum(p => new FileInfo(p).Length);
        var rate = _device.Options.BytesPerSecond ?? 0;
        var uploadTime = rate > 0 ? TimeSpan.FromSeconds(Math.Min(20, (double)total / rate)) : TimeSpan.Zero;
        const int steps = 10;
        for (var i = 0; i <= steps; i++)
        {
            progress?.Report(new InstallProgress("Uploading", 60.0 * i / steps));
            await FakeIo.DelayAsync(_device, uploadTime / steps, ct).ConfigureAwait(false);
        }

        for (var i = 1; i <= steps; i++)
        {
            progress?.Report(new InstallProgress("Installing", 60 + 40.0 * i / steps));
            await FakeIo.DelayAsync(_device, rate > 0 ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero, ct).ConfigureAwait(false);
        }

        var name = Path.GetFileNameWithoutExtension(first);
        var slug = string.Concat(name.ToLowerInvariant().Where(char.IsLetterOrDigit));
        var id = (IsIos ? "com.sideloaded." : "com.example.") + (slug.Length == 0 ? "app" : slug);
        lock (_device.Gate)
        {
            _apps.RemoveAll(a => a.Info.Id == id);
            _apps.Add(new FakeApp(
                new AppInfo(id, name, "1.0", total, 0, AppSource.Sideloaded, false, DateTimeOffset.Now),
                (90 + FakeArt.StableHash(id) % 120, 90, 200)));
        }

        _icons.TryRemove(id, out _);
        progress?.Report(new InstallProgress("Done", 100));
    }

    public Task UninstallAsync(string appId, CancellationToken ct)
    {
        _device.EnsureConnected();
        _device.Require(DeviceCapability.AppsUninstall, "remove apps");
        lock (_device.Gate)
        {
            var app = _apps.FirstOrDefault(a => a.Info.Id == appId)
                ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That app isn't on the phone.");
            if (app.Info.Source == AppSource.System)
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "System apps can't be removed.");
            }

            _apps.Remove(app);
        }

        _icons.TryRemove(appId, out _);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ExportAsync(string appId, string destinationFolder, CancellationToken ct)
    {
        _device.EnsureConnected();
        if (IsIos)
        {
            throw new DeviceException(DeviceErrorCodes.NotSupported, "App Store apps are encrypted, so they can't be saved from an iPhone.");
        }

        _device.Require(DeviceCapability.AppsExport, "save apps");
        AppInfo info;
        lock (_device.Gate)
        {
            info = _apps.FirstOrDefault(a => a.Info.Id == appId)?.Info
                ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That app isn't on the phone.");
        }

        Directory.CreateDirectory(destinationFolder);
        var target = Path.Combine(destinationFolder, $"{info.Id}-{info.Version}.apk");
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            // A zip header so it looks like an APK; capped so a sample export stays small.
            await FakeIo.ProduceAsync(_device, "PK\u0003\u0004"u8.ToArray(), Math.Min(info.AppBytes, 8_000_000), file, null, ct).ConfigureAwait(false);
        }

        return [target];
    }
}
