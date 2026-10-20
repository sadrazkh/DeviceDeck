using System.IO.Compression;
using System.Text.RegularExpressions;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Apps;

/// <summary>What to send to the phone for one app: its APK files (base first) and any OBB expansion files.</summary>
internal sealed class InstallPlan(IReadOnlyList<string> apks, IReadOnlyList<(string LocalPath, string DevicePath)> obbs, string? tempDirectory) : IDisposable
{
    public IReadOnlyList<string> Apks { get; } = apks;
    public IReadOnlyList<(string LocalPath, string DevicePath)> Obbs { get; } = obbs;

    public void Dispose()
    {
        if (tempDirectory != null)
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// Expands what the user dropped into APK files: one .apk; several .apk (base + splits of one app); or one bundle —
/// .apks (bundletool / SAI), .xapk (APKPure, may carry OBB files) or .apkm (APKMirror, unencrypted ones only).
/// Splits for processors the phone doesn't have are left out.
/// </summary>
internal static partial class PackageArchive
{
    private static readonly string[] BundleExtensions = [".apks", ".xapk", ".apkm", ".zip"];

    [GeneratedRegex(@"(?:^|[.\-_])(?<abi>arm64_v8a|armeabi_v7a|armeabi|x86_64|x86|mips64|mips)(?:\.apk$|[.\-])", RegexOptions.IgnoreCase)]
    private static partial Regex AbiToken();

    [GeneratedRegex(@"^Android/obb/(?<package>[A-Za-z0-9_.]+)/(?<file>[^/]+\.obb)$", RegexOptions.IgnoreCase)]
    private static partial Regex ObbEntry();

    public static bool IsBundle(string path) => BundleExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static InstallPlan Prepare(IReadOnlyList<string> localPaths, IReadOnlyList<string> deviceAbis)
    {
        if (localPaths.Count == 0)
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, "Pick an .apk, .apks, .xapk or .apkm file to install.");
        }
        foreach (string path in localPaths)
        {
            if (!File.Exists(path))
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, $"{Path.GetFileName(path)} isn't on this PC any more.");
            }
        }

        if (localPaths.Count == 1 && IsBundle(localPaths[0]))
        {
            return ExtractBundle(localPaths[0], deviceAbis);
        }
        if (localPaths.Any(IsBundle))
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, "Install one .apks, .xapk or .apkm bundle at a time.");
        }
        if (localPaths.Any(p => !p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed, "Only .apk, .apks, .xapk and .apkm files can be installed.");
        }
        var apks = FilterSplits(localPaths.Select(p => (Name: Path.GetFileName(p), Path: p)).ToList(), deviceAbis);
        return new InstallPlan(apks.Select(a => a.Path).ToList(), [], null);
    }

    private static InstallPlan ExtractBundle(string bundle, IReadOnlyList<string> deviceAbis)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(bundle);
        }
        catch (InvalidDataException)
        {
            throw new DeviceException(DeviceErrorCodes.InstallFailed,
                $"{Path.GetFileName(bundle)} isn't a ZIP bundle (newer .apkm files are encrypted and can't be installed this way).");
        }

        string temp = Path.Combine(Path.GetTempPath(), "DeviceDeck", "install-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (archive)
            {
                var apkEntries = archive.Entries
                    .Where(e => e.FullName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                    .Where(e => !e.FullName.StartsWith("standalones/", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // bundletool output: when there are splits/, the top-level universal.apk is an alternative, not a part.
                if (apkEntries.Any(e => e.FullName.StartsWith("splits/", StringComparison.OrdinalIgnoreCase)))
                {
                    apkEntries = apkEntries.Where(e => e.FullName.StartsWith("splits/", StringComparison.OrdinalIgnoreCase)).ToList();
                }
                if (apkEntries.Count == 0)
                {
                    throw new DeviceException(DeviceErrorCodes.InstallFailed, $"{Path.GetFileName(bundle)} has no APK files inside.");
                }

                Directory.CreateDirectory(temp);
                var extracted = new List<(string Name, string Path)>();
                int index = 0;
                foreach (var entry in apkEntries)
                {
                    string name = Path.GetFileName(entry.FullName);
                    string target = Path.Combine(temp, $"{index++:D3}_{name}");
                    entry.ExtractToFile(target);
                    extracted.Add((name, target));
                }

                var obbs = new List<(string, string)>();
                foreach (var entry in archive.Entries)
                {
                    var match = ObbEntry().Match(entry.FullName);
                    if (match.Success)
                    {
                        string target = Path.Combine(temp, $"{index++:D3}_{match.Groups["file"].Value}");
                        entry.ExtractToFile(target);
                        obbs.Add((target, $"/sdcard/Android/obb/{match.Groups["package"].Value}/{match.Groups["file"].Value}"));
                    }
                }
                var apks = FilterSplits(extracted, deviceAbis);
                return new InstallPlan(apks.Select(a => a.Path).ToList(), obbs, temp);
            }
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Drops ABI splits the phone can't run (it would refuse them anyway) and orders base first.</summary>
    internal static List<(string Name, string Path)> FilterSplits(List<(string Name, string Path)> apks, IReadOnlyList<string> deviceAbis)
    {
        if (apks.Count <= 1)
        {
            return apks;
        }
        var supported = new HashSet<string>(deviceAbis.Select(a => a.Replace('-', '_')), StringComparer.OrdinalIgnoreCase);
        var kept = apks.Where(apk =>
        {
            var match = AbiToken().Match(apk.Name);
            return !match.Success || supported.Count == 0 || supported.Contains(match.Groups["abi"].Value);
        }).ToList();
        return kept.OrderBy(a => IsBase(a.Name) ? 0 : 1).ToList();
    }

    private static bool IsBase(string name) =>
        name.Equals("base.apk", StringComparison.OrdinalIgnoreCase)
        || name.Equals("base-master.apk", StringComparison.OrdinalIgnoreCase)
        || (!name.Contains("config.", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("split", StringComparison.OrdinalIgnoreCase)
            && !name.Contains('-'));
}
