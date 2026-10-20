using System.Globalization;
using System.Text.RegularExpressions;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Apk;
using DeviceDeck.Android.Info;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Apps;

/// <summary>
/// Apps through the package manager: `pm list packages` for what's installed, `dumpsys package` for versions and
/// install times, `dumpsys diskstats` for sizes, the APK itself (read in place) for label and icon, and streamed
/// `cmd package install[-create/-write/-commit]` sessions for installs.
/// </summary>
internal sealed partial class AndroidAppManager(
    IAdbChannel channel,
    Func<DeviceProfile> profile,
    AppMetadataCache cache,
    AndroidProviderOptions options,
    CancellationToken deviceLifetime) : IAppManager
{
    // Stores whose installs count as "Store" (Play, OEM stores, F-Droid, and the two big Iranian stores).
    private static readonly HashSet<string> StoreInstallers = new(StringComparer.Ordinal)
    {
        "com.android.vending", "com.sec.android.app.samsungapps", "com.huawei.appmarket", "com.xiaomi.market",
        "com.xiaomi.mipicks", "com.heytap.market", "com.oppo.market", "com.bbk.appstore", "com.amazon.venezia",
        "org.fdroid.fdroid", "com.aurora.store", "com.farsitel.bazaar", "ir.mservices.market", "com.hihonor.appmarket",
    };

    private readonly SemaphoreSlim _apkReaders = new(4, 4);

    public const string ListCommand = "pm list packages -f -U -i --show-versioncode";

    public async Task<IReadOnlyList<AppInfo>> ListAsync(bool includeSystem, CancellationToken ct)
    {
        string script = ShellScript.Build(
            ("all", ListCommand),
            ("system", "pm list packages -s"),
            ("diskstats", "dumpsys diskstats"));
        var listing = channel.ShellAsync(script, ct);
        var dumpsys = channel.ShellAsync("dumpsys package packages", ct);
        var sections = ShellScript.Split((await listing.ConfigureAwait(false)).Output);
        var packages = DumpsysPackageParser.Parse((await dumpsys.ConfigureAwait(false)).Output);

        var all = PackageListParser.Parse(sections.GetValueOrDefault("all") ?? "");
        if (all.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, "The phone's package manager didn't answer; it may still be starting up.");
        }
        var system = PackageListParser.Parse(sections.GetValueOrDefault("system") ?? "").Select(p => p.PackageName).ToHashSet(StringComparer.Ordinal);
        var sizes = DiskStatsParser.Parse(sections.GetValueOrDefault("diskstats") ?? "").Packages;

        var selected = all.Where(p => includeSystem || !(system.Contains(p.PackageName) || packages.GetValueOrDefault(p.PackageName)?.IsSystem == true)).ToList();
        var codeSizes = await CodeSizesAsync(selected.Where(p => !sizes.ContainsKey(p.PackageName)), packages, ct).ConfigureAwait(false);
        var labels = await LabelsAsync(selected, ct).ConfigureAwait(false);

        var apps = new List<AppInfo>(selected.Count);
        foreach (var entry in selected)
        {
            var details = packages.GetValueOrDefault(entry.PackageName);
            bool isSystem = system.Contains(entry.PackageName) || details?.IsSystem == true;
            string? installer = entry.Installer ?? details?.Installer;
            var size = sizes.GetValueOrDefault(entry.PackageName);
            DateTime? installed = details?.FirstInstallTime;
            apps.Add(new AppInfo(
                entry.PackageName,
                labels.GetValueOrDefault(entry.PackageName) ?? entry.PackageName,
                details?.VersionName ?? entry.VersionCode?.ToString(CultureInfo.InvariantCulture) ?? "",
                size?.AppBytes ?? codeSizes.GetValueOrDefault(entry.PackageName),
                size == null ? 0 : size.DataBytes + size.CacheBytes,
                isSystem ? AppSource.System : installer != null && StoreInstallers.Contains(installer) ? AppSource.Store : AppSource.Sideloaded,
                FileSharing: false,
                installed == null ? null : new DateTimeOffset(installed.Value, profile().UtcOffset)));
        }
        await cache.SaveAsync(ct).ConfigureAwait(false);
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Labels from the cache, else read from each APK (manifest + resources.arsc, a few hundred KB each). Waits up
    /// to <see cref="AndroidProviderOptions.AppLabelBudget"/>; what isn't done by then keeps loading in the
    /// background so the next listing has it.
    /// </summary>
    private async Task<Dictionary<string, string>> LabelsAsync(IReadOnlyList<PackageListEntry> entries, CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<PackageListEntry>();
        foreach (var entry in entries)
        {
            if (cache.TryGetLabel(AppMetadataCache.Key(entry.PackageName, entry.SourceDir, entry.VersionCode), out string label))
            {
                labels[entry.PackageName] = label;
            }
            else if (entry.SourceDir != null)
            {
                missing.Add(entry);
            }
        }
        if (missing.Count == 0)
        {
            return labels;
        }

        // Background work outlives this call but not the device.
        var work = missing.Select(entry => Task.Run(() => ReadMetadataAsync(entry.PackageName, entry.SourceDir!, entry.VersionCode, withIcon: false, deviceLifetime))).ToList();
        var all = Task.WhenAll(work);
        await Task.WhenAny(all, Task.Delay(options.AppLabelBudget, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        for (int i = 0; i < missing.Count; i++)
        {
            if (work[i].IsCompletedSuccessfully && work[i].Result?.Label is { } label)
            {
                labels[missing[i].PackageName] = label;
            }
        }
        if (!all.IsCompleted)
        {
            _ = all.ContinueWith(_ => cache.SaveAsync(CancellationToken.None), TaskScheduler.Default);
        }
        return labels;
    }

    private async Task<ApkMetadata?> ReadMetadataAsync(string packageName, string apkPath, long? versionCode, bool withIcon, CancellationToken ct)
    {
        await _apkReaders.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var reader = await RemoteFileRangeReader.OpenAsync(channel, apkPath, ct).ConfigureAwait(false);
            var metadata = await ApkInspector.ReadAsync(reader, withIcon, ct).ConfigureAwait(false);
            string key = AppMetadataCache.Key(packageName, apkPath, versionCode);
            if (metadata.Label != null)
            {
                cache.SetLabel(key, metadata.Label);
            }
            if (metadata.IconPng != null)
            {
                cache.SetIcon(key, metadata.IconPng);
            }
            return metadata;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An APK we can't parse just keeps its package name and gets no icon.
            return null;
        }
        finally
        {
            _apkReaders.Release();
        }
    }

    /// <summary>APK + native code size from du on each codePath, for apps diskstats hasn't measured yet.</summary>
    private async Task<Dictionary<string, long>> CodeSizesAsync(IEnumerable<PackageListEntry> entries, IReadOnlyDictionary<string, DumpsysPackage> packages, CancellationToken ct)
    {
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            string? path = packages.GetValueOrDefault(entry.PackageName)?.CodePath
                ?? (entry.SourceDir is { } source ? source[..Math.Max(0, source.LastIndexOf('/'))] : null);
            if (!string.IsNullOrEmpty(path))
            {
                byPath.TryAdd(path, entry.PackageName);
            }
        }
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var chunk in byPath.Keys.Chunk(150))
        {
            var result = await channel.ShellAsync("du -sk " + string.Join(' ', chunk.Select(ShellQuote.Quote)), ct).ConfigureAwait(false);
            foreach (string line in result.Output.Split('\n'))
            {
                string[] parts = line.Trim().Split('\t', 2);
                if (parts.Length == 2 && long.TryParse(parts[0], out long kb) && byPath.TryGetValue(parts[1], out string? package))
                {
                    sizes[package] = kb * 1024;
                }
            }
        }
        return sizes;
    }

    public async Task<byte[]?> GetIconAsync(string appId, CancellationToken ct)
    {
        ValidatePackageName(appId);
        var entry = await FindPackageAsync(appId, ct).ConfigureAwait(false);
        if (entry?.SourceDir == null)
        {
            return null;
        }
        string key = AppMetadataCache.Key(entry.PackageName, entry.SourceDir, entry.VersionCode);
        if (cache.TryGetIcon(key) is { } cached)
        {
            return cached;
        }
        var metadata = await ReadMetadataAsync(entry.PackageName, entry.SourceDir, entry.VersionCode, withIcon: true, ct).ConfigureAwait(false);
        return metadata?.IconPng;
    }

    private async Task<PackageListEntry?> FindPackageAsync(string packageName, CancellationToken ct)
    {
        // The filter is a substring match; pick the exact name.
        var result = await channel.ShellAsync($"{ListCommand} {ShellQuote.Quote(packageName)}", ct).ConfigureAwait(false);
        return PackageListParser.Parse(result.Output).FirstOrDefault(p => p.PackageName == packageName);
    }

    public async Task InstallAsync(IReadOnlyList<string> localPaths, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("preparing", null));
        using var plan = await Task.Run(() => PackageArchive.Prepare(localPaths, profile().SupportedAbis), ct).ConfigureAwait(false);
        if (plan.Apks.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, "None of the APKs in that bundle fit this phone's processor.");
        }

        long total = plan.Apks.Sum(p => new FileInfo(p).Length) + plan.Obbs.Sum(o => new FileInfo(o.LocalPath).Length);
        long done = 0;
        void Report(long current) => progress?.Report(new InstallProgress("uploading", total == 0 ? null : Math.Min(100.0, current * 100.0 / total)));

        if (plan.Apks.Count == 1)
        {
            await using var apk = File.OpenRead(plan.Apks[0]);
            string output = await channel.ExecAsync($"cmd package install -r -S {apk.Length}", apk,
                new SyncProgress(sent => Report(sent)), ct).ConfigureAwait(false);
            progress?.Report(new InstallProgress("installing", null));
            if (AdbErrors.FromInstallOutput(output) is { } error)
            {
                throw error;
            }
            done = apk.Length;
        }
        else
        {
            long apkTotal = plan.Apks.Sum(p => new FileInfo(p).Length);
            string created = await channel.ExecAsync($"cmd package install-create -r -S {apkTotal}", null, null, ct).ConfigureAwait(false);
            string session = ParseSession(created) ?? throw AdbErrors.FromInstallOutput(created)
                ?? new DeviceException(DeviceErrorCodes.InstallFailed, $"The phone didn't open an install session: {AdbErrors.FirstLine(created)}");
            try
            {
                for (int i = 0; i < plan.Apks.Count; i++)
                {
                    await using var apk = File.OpenRead(plan.Apks[i]);
                    long before = done;
                    string name = $"{i}_{SafeSplitName(plan.Apks[i])}";
                    string written = await channel.ExecAsync($"cmd package install-write -S {apk.Length} {session} {name} -", apk,
                        new SyncProgress(sent => Report(before + sent)), ct).ConfigureAwait(false);
                    if (!written.Contains("Success", StringComparison.Ordinal))
                    {
                        throw AdbErrors.FromInstallOutput(written)
                            ?? new DeviceException(DeviceErrorCodes.InstallFailed, $"Sending {Path.GetFileName(plan.Apks[i])} failed.");
                    }
                    done += apk.Length;
                }
                progress?.Report(new InstallProgress("installing", null));
                string committed = await channel.ExecAsync($"cmd package install-commit {session}", null, null, ct).ConfigureAwait(false);
                if (AdbErrors.FromInstallOutput(committed) is { } error)
                {
                    throw error;
                }
            }
            catch
            {
                await AbandonAsync(session).ConfigureAwait(false);
                throw;
            }
        }

        foreach (var (local, device) in plan.Obbs)
        {
            await using var obb = File.OpenRead(local);
            long before = done;
            await channel.PushAsync(obb, device, new SyncProgress(sent => Report(before + sent)), ct).ConfigureAwait(false);
            done += obb.Length;
        }
        progress?.Report(new InstallProgress("done", 100));
    }

    private async Task AbandonAsync(string session)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await channel.ExecAsync($"cmd package install-abandon {session}", null, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceException or OperationCanceledException)
        {
        }
    }

    [GeneratedRegex(@"\[(?<id>\d+)\]")]
    private static partial Regex SessionId();

    /// <summary>"Success: created install session [1234567]".</summary>
    internal static string? ParseSession(string output)
    {
        if (!output.Contains("Success", StringComparison.Ordinal))
        {
            return null;
        }
        var match = SessionId().Match(output);
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeName();

    private static string SafeSplitName(string path)
    {
        string name = Path.GetFileName(path);
        int underscore = name.IndexOf('_');
        if (underscore is > 0 and <= 4 && name[..underscore].All(char.IsAsciiDigit))
        {
            name = name[(underscore + 1)..]; // our own "000_" extraction prefix
        }
        return UnsafeName().Replace(name, "_");
    }

    public async Task UninstallAsync(string appId, CancellationToken ct)
    {
        ValidatePackageName(appId);
        var result = await channel.ShellAsync($"pm uninstall {appId}", ct).ConfigureAwait(false);
        if (AdbErrors.FromUninstallOutput(result.Output + result.Error, appId) is { } error)
        {
            throw error;
        }
    }

    public async Task<IReadOnlyList<string>> ExportAsync(string appId, string destinationFolder, CancellationToken ct)
    {
        ValidatePackageName(appId);
        var result = await channel.ShellAsync($"pm path {appId}", ct).ConfigureAwait(false);
        var paths = PackageListParser.ParsePaths(result.Output);
        if (paths.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{appId} isn't installed.");
        }
        Directory.CreateDirectory(destinationFolder);
        var written = new List<string>();
        foreach (string remote in paths)
        {
            string remoteName = remote[(remote.LastIndexOf('/') + 1)..];
            string localName = paths.Count == 1 || remoteName == "base.apk" ? $"{appId}.apk" : $"{appId}.{remoteName}";
            string local = Path.Combine(destinationFolder, localName);
            await using (var file = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await channel.PullAsync(remote, file, null, ct).ConfigureAwait(false);
            }
            written.Add(local);
        }
        return written;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$")]
    private static partial Regex PackageName();

    internal static void ValidatePackageName(string appId)
    {
        if (!PackageName().IsMatch(appId))
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, $"'{appId}' isn't an Android package name.");
        }
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to a captured context and arrive out of order).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
