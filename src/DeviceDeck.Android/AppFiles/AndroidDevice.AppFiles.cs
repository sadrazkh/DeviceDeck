using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DeviceDeck.Android.Files;
using DeviceDeck.Android.Parsing;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.AppFiles;

namespace DeviceDeck.Android;

/// <summary>
/// App files on Android: the per-app folders of shared storage (Android/media/&lt;package&gt; always,
/// Android/data/&lt;package&gt; when this phone lets adb read it) and the shared Documents and Download folders.
/// Each is opened as a root of its own (see <see cref="AppFilesRootKeys"/>), so the browser and the transfer
/// queue use the normal sync-service file system on it. Names are package names; the page shows app labels.
/// </summary>
internal sealed partial class AndroidDevice : IAppFilesSource
{
    internal const string StorageBase = "/sdcard";

    public async Task<AppFilesOverview> GetAppFilesAsync(CancellationToken ct)
    {
        EnsureReady();
        var output = (await Channel.ShellAsync(AppFilesScript, ct).ConfigureAwait(false)).Output;
        return ParseAppFiles(ShellScript.Split(output));
    }

    /// <summary>One shell round trip: a <c>find … -exec stat</c> over every path, summed here.</summary>
    public async Task<FolderUsage> MeasureAsync(string rootKey, IReadOnlyList<string> paths, CancellationToken ct)
    {
        string basePath;
        await using (var fs = (AndroidFileSystem)await OpenFileSystemAsync(rootKey, ct).ConfigureAwait(false))
        {
            basePath = fs.BasePath;
        }

        var targets = paths.Select(p => RootPath.ToDevice(basePath, RootPath.Normalize(p))).ToList();
        if (targets.Count == 0)
        {
            return FolderUsage.Of([]);
        }

        var command = $"find {string.Join(' ', targets.Distinct(StringComparer.Ordinal).Select(ShellQuote.Quote))} -exec stat -c '%f|%s|%n' {{}} +";
        var result = await Channel.ShellAsync(command, ct).ConfigureAwait(false);
        return SumUsage(paths, targets, result.Output);
    }

    /// <summary>The device folder a root key stands for, for <see cref="OpenFileSystemAsync"/>.</summary>
    private static bool TryAppFilesBase(string rootKey, [NotNullWhen(true)] out string? basePath)
    {
        basePath = AppFilesRootKeys.TryParse(rootKey, out var segments) ? StorageBase + "/" + string.Join('/', segments) : null;
        return basePath is not null;
    }

    /// <summary>
    /// "media": one "&lt;0|1&gt;|&lt;package&gt;" line per folder of Android/media (1 = holds at least one file);
    /// "data": "allowed" or "blocked", then the same lines for Android/data; "shared": the same for Documents and Download.
    /// </summary>
    internal static readonly (string Name, string Command)[] AppFilesParts =
    [
        ("media", ScanCommand(StorageBase + "/Android/media")),
        ("data", $"if ls {ShellQuote.Quote(StorageBase + "/Android/data/")} >/dev/null 2>&1; then echo allowed; {ScanCommand(StorageBase + "/Android/data")}; else echo blocked; fi"),
        ("shared", $"for n in {string.Join(' ', AppFilesRootKeys.SharedFolders)}; do d=\"{StorageBase}/$n/\"; [ -d \"$d\" ] || continue; {HasFileTest("$n")}; done"),
    ];

    internal static readonly string AppFilesScript = ShellScript.Build(AppFilesParts);

    private static string ScanCommand(string parent) =>
        $"for d in {ShellQuote.Quote(parent)}/*/; do [ -d \"$d\" ] || continue; n=${{d%/}}; n=${{n##*/}}; {HasFileTest("$n")}; done";

    // `head -n 1` stops find early in a large folder (find ends on the closed pipe).
    private static string HasFileTest(string name) =>
        $"if [ -n \"$(find \"$d\" -type f 2>/dev/null | head -n 1)\" ]; then echo \"1|{name}\"; else echo \"0|{name}\"; fi";

    internal static AppFilesOverview ParseAppFiles(IReadOnlyDictionary<string, string> sections)
    {
        var folders = new SortedDictionary<string, List<AppFolder>>(StringComparer.Ordinal);
        void Add(string prefix, string label, IEnumerable<(string Name, bool HasFiles)> lines)
        {
            foreach (var (name, hasFiles) in lines.Where(l => AppFilesRootKeys.IsPackageName(l.Name)))
            {
                if (!folders.TryGetValue(name, out var list))
                {
                    folders[name] = list = [];
                }

                list.Add(new AppFolder(prefix + name, label, Empty: !hasFiles));
            }
        }

        Add(AppFilesRootKeys.MediaPrefix, "Media", Lines(sections.GetValueOrDefault("media")));

        var data = (sections.GetValueOrDefault("data") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dataLines = Lines(string.Join('\n', data.Skip(1))).ToList();
        // A phone that lets adb list Android/data but shows nothing in it is hiding it: no real phone has an empty one.
        var access = data.FirstOrDefault() == "allowed" && dataLines.Count > 0 ? AppDataAccess.Allowed : AppDataAccess.Blocked;
        if (access == AppDataAccess.Allowed)
        {
            Add(AppFilesRootKeys.DataPrefix, "Data", dataLines);
        }

        var entries = folders.Select(f => new AppFilesEntry(f.Key, f.Key, AppFilesKind.App, f.Key, f.Value)).ToList();
        foreach (var (name, hasFiles) in Lines(sections.GetValueOrDefault("shared")).Where(l => AppFilesRootKeys.SharedFolders.Contains(l.Name)))
        {
            var key = AppFilesRootKeys.SharedPrefix + name;
            entries.Add(new AppFilesEntry(key, name, AppFilesKind.SharedFolder, null, [new AppFolder(key, name, Empty: !hasFiles)]));
        }

        return new AppFilesOverview(entries, access);
    }

    private static IEnumerable<(string Name, bool HasFiles)> Lines(string? text)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 2 && line[1] == '|' && line[0] is '0' or '1')
            {
                yield return (line[2..], line[0] == '1');
            }
        }
    }

    /// <summary>
    /// Adds up `stat -c '%f|%s|%n'` lines under each target; a line counts for every target it is under (paths may
    /// nest, and find then lists an entry once per starting point, so each path is taken once). A target folder isn't
    /// counted in itself.
    /// </summary>
    internal static FolderUsage SumUsage(IReadOnlyList<string> paths, IReadOnlyList<string> targets, string output)
    {
        var files = new int[targets.Count];
        var folders = new int[targets.Count];
        var bytes = new long[targets.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.TrimEnd('\r').Split('|', 3);
            if (parts.Length < 3
                || !int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mode)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                || !seen.Add(parts[2]))
            {
                continue;
            }

            var path = parts[2];
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                if (path != target && !path.StartsWith(target + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                switch (mode & 0xF000)
                {
                    case 0x4000 when path != target:
                        folders[i]++;
                        break;
                    case 0x8000:
                        files[i]++;
                        bytes[i] += size;
                        break;
                }
            }
        }

        return FolderUsage.Of(paths.Select((p, i) => new PathUsage(p, files[i], folders[i], bytes[i])).ToList());
    }
}
