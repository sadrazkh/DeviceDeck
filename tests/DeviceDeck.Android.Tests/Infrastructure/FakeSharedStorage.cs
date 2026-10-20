using System.Text;
using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Tests.Infrastructure;

/// <summary>
/// Shared storage (/sdcard) for a <see cref="FakeAdbChannel"/>: folders here, file bytes in the channel's
/// <see cref="FakeAdbChannel.Files"/> (which sync push and pull use). Answers the shell commands the file system and
/// app files send: the stat listings, mkdir, the rm and mv scripts, the measuring find and the app-folders script.
/// </summary>
internal sealed partial class FakeSharedStorage
{
    private const long Epoch = 1_760_000_000;
    private readonly FakeAdbChannel _adb;

    public FakeSharedStorage(FakeAdbChannel adb)
    {
        _adb = adb;
        Dirs.Add("/sdcard");
        foreach (var (name, command) in AndroidDevice.AppFilesParts)
            adb.On(c => c == command, _ => FakeReply.Text(AppFilesSection(name)));
        adb.On(c => ListCommand().IsMatch(c), c => List(Unquote(ListCommand().Match(c).Groups["dir"].Value)));
        adb.On(c => StatOne().IsMatch(c), c => StatOneReply(Unquote(StatOne().Match(c).Groups["path"].Value)));
        adb.On(c => Mkdir().IsMatch(c), c => MakeDir(Unquote(Mkdir().Match(c).Groups["path"].Value)));
        adb.On(c => RmScript().IsMatch(c), c => Remove(RmScript().Match(c)));
        adb.On(c => MvScript().IsMatch(c), c => Move(MvScript().Match(c)));
        adb.On(c => MeasureFind().IsMatch(c), c => Measure(MeasureFind().Match(c)));
    }

    public HashSet<string> Dirs { get; } = new(StringComparer.Ordinal);

    /// <summary>Folders whose listing answers "Permission denied" (Android/data on a phone that hides it).</summary>
    public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

    public FakeSharedStorage AddDir(string path)
    {
        for (var dir = path; dir.Length > 0 && Dirs.Add(dir); dir = Parent(dir))
        {
        }

        return this;
    }

    public FakeSharedStorage AddFile(string path, byte[] content)
    {
        AddDir(Parent(path));
        _adb.Files[path] = content;
        return this;
    }

    public bool Exists(string path) => Dirs.Contains(path) || _adb.Files.ContainsKey(path);

    /// <summary>Every folder and file under <paramref name="root"/>, with sizes, sorted; for before/after comparisons.</summary>
    public List<string> Snapshot(string root) =>
        Dirs.Where(d => Under(d, root)).Select(d => d + "/")
            .Concat(_adb.Files.Where(f => Under(f.Key, root)).Select(f => $"{f.Key}|{f.Value.Length}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.Value))[..12]}"))
            .Order(StringComparer.Ordinal)
            .ToList();

    private IEnumerable<string> Children(string dir) =>
        Dirs.Concat(_adb.Files.Keys).Where(p => p != dir && Parent(p) == dir).Order(StringComparer.Ordinal);

    private string Line(string path, bool withTime = true) =>
        Dirs.Contains(path)
            ? withTime ? $"41f9|3488|{Epoch}|{path}" : $"41f9|3488|{path}"
            : withTime ? $"81b0|{_adb.Files[path].Length}|{Epoch}|{path}" : $"81b0|{_adb.Files[path].Length}|{path}";

    private FakeReply List(string dir)
    {
        if (Denied.Any(d => Under(dir, d)))
            return FakeReply.Text("", 1, $"find: '{dir}/': Permission denied");
        if (!Dirs.Contains(dir))
            return FakeReply.Text("", 1, $"find: '{dir}/': No such file or directory");
        return FakeReply.Text(string.Concat(Children(dir).Select(c => Line(c) + "\n")));
    }

    private FakeReply StatOneReply(string path) =>
        Exists(path) ? FakeReply.Text(Line(path) + "\n") : FakeReply.Text("", 1, $"stat: '{path}': No such file or directory");

    private FakeReply MakeDir(string path)
    {
        if (Exists(path))
            return FakeReply.Text("", 1, $"mkdir: '{path}': File exists");
        if (!Dirs.Contains(Parent(path)))
            return FakeReply.Text("", 1, $"mkdir: '{path}': No such file or directory");
        Dirs.Add(path);
        return FakeReply.Text("");
    }

    private FakeReply Remove(Match script)
    {
        var path = Unquote(script.Groups["path"].Value);
        if (!Exists(path))
            return FakeReply.Text("", 2, "No such file or directory");
        var recursive = script.Groups["rest"].Value.StartsWith("rm -rf", StringComparison.Ordinal);
        if (Dirs.Contains(path) && !recursive && Children(path).Any())
            return FakeReply.Text("", 1, $"rmdir: '{path}': Directory not empty");
        foreach (var dir in Dirs.Where(d => Under(d, path)).ToList())
            Dirs.Remove(dir);
        foreach (var file in _adb.Files.Keys.Where(f => Under(f, path)).ToList())
            _adb.Files.Remove(file);
        return FakeReply.Text("");
    }

    private FakeReply Move(Match script)
    {
        var target = Unquote(script.Groups["target"].Value);
        var source = Unquote(script.Groups["source"].Value);
        if (Exists(target))
            return FakeReply.Text("", 17, "File exists");
        if (!Exists(source))
            return FakeReply.Text("", 1, $"mv: '{source}': No such file or directory");
        foreach (var dir in Dirs.Where(d => Under(d, source)).ToList())
        {
            Dirs.Remove(dir);
            Dirs.Add(target + dir[source.Length..]);
        }

        foreach (var file in _adb.Files.Where(f => Under(f.Key, source)).ToList())
        {
            _adb.Files.Remove(file.Key);
            _adb.Files[target + file.Key[source.Length..]] = file.Value;
        }

        return FakeReply.Text("");
    }

    private FakeReply Measure(Match find)
    {
        var output = new StringBuilder();
        foreach (Capture quoted in find.Groups["path"].Captures)
        {
            var path = Unquote(quoted.Value);
            if (!Exists(path))
                continue;
            output.Append(Line(path, withTime: false)).Append('\n');
            foreach (var entry in Dirs.Concat(_adb.Files.Keys).Where(p => p != path && Under(p, path)).Order(StringComparer.Ordinal))
                output.Append(Line(entry, withTime: false)).Append('\n');
        }

        return FakeReply.Text(output.ToString(), output.Length == 0 ? 1 : 0);
    }

    /// <summary>What each part of the app-folders script prints (the channel runs a script's parts one by one).</summary>
    private string AppFilesSection(string name)
    {
        string Scan(string parent) => string.Concat(Children(parent).Where(Dirs.Contains)
            .Select(d => (HasFile(d) ? "1|" : "0|") + d[(d.LastIndexOf('/') + 1)..] + "\n"));

        return name switch
        {
            "media" => Scan("/sdcard/Android/media"),
            "data" => Denied.Contains("/sdcard/Android/data") ? "blocked\n" : "allowed\n" + Scan("/sdcard/Android/data"),
            _ => string.Concat(new[] { "Documents", "Download" }.Where(n => Dirs.Contains("/sdcard/" + n))
                .Select(n => (HasFile("/sdcard/" + n) ? "1|" : "0|") + n + "\n")),
        };
    }

    private bool HasFile(string dir) => _adb.Files.Keys.Any(f => Under(f, dir));

    private static bool Under(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string Parent(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? "" : path[..cut];
    }

    private static string Unquote(string quoted) => quoted.Replace("'\\''", "'", StringComparison.Ordinal);

    [GeneratedRegex(@"^find '(?<dir>.*)/' -mindepth 1 -maxdepth 1 -exec stat -L -c '%f\|%s\|%Y\|%n' \{\} \+$")]
    private static partial Regex ListCommand();

    [GeneratedRegex(@"^stat -L -c '%f\|%s\|%Y\|%n' -- '(?<path>.*)'$")]
    private static partial Regex StatOne();

    [GeneratedRegex(@"^mkdir -- '(?<path>.*)'$")]
    private static partial Regex Mkdir();

    [GeneratedRegex(@"^if \[ ! -e '(?<path>.*?)' \] && \[ ! -L '.*?' \]; then echo 'No such file or directory' >&2; exit 2; fi; (?<rest>.*)$")]
    private static partial Regex RmScript();

    [GeneratedRegex(@"^if \[ -e '(?<target>.*?)' \]; then echo 'File exists' >&2; exit 17; fi; mv -- '(?<source>.*?)' '.*'$")]
    private static partial Regex MvScript();

    [GeneratedRegex(@"^find (?:'(?<path>[^']*)' )+-exec stat -c '%f\|%s\|%n' \{\} \+$")]
    private static partial Regex MeasureFind();
}
