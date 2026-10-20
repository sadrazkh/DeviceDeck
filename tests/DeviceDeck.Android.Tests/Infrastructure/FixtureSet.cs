namespace DeviceDeck.Android.Tests.Infrastructure;

/// <summary>One folder of raw command output (see Fixtures/README.md).</summary>
public sealed class FixtureSet(string directory)
{
    public string Directory { get; } = directory;

    public string Name => Path.GetFileName(Directory);

    public bool IsSynthetic => Directory.Contains($"{Path.DirectorySeparatorChar}synthetic{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    public bool Has(string file) => File.Exists(Path.Combine(Directory, file));

    public string Read(string file) => File.ReadAllText(Path.Combine(Directory, file));

    public string? TryRead(string file) => Has(file) ? Read(file) : null;

    public override string ToString() => Name;

    public static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Every synthetic and captured folder, for the generic checks.</summary>
    public static IEnumerable<FixtureSet> All() =>
        new[] { "synthetic", "captured" }
            .Select(kind => Path.Combine(Root, kind))
            .Where(System.IO.Directory.Exists)
            .SelectMany(System.IO.Directory.GetDirectories)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(d => new FixtureSet(d));

    public static FixtureSet Synthetic(string name) => new(Path.Combine(Root, "synthetic", name));

    /// <summary>xUnit member data: one row per fixture folder.</summary>
    public static TheoryData<string> AllNames()
    {
        var data = new TheoryData<string>();
        foreach (var set in All())
        {
            data.Add(Path.GetRelativePath(Root, set.Directory));
        }
        return data;
    }

    public static FixtureSet ByRelativePath(string relative) => new(Path.Combine(Root, relative));
}
