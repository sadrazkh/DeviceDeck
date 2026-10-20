using System.Text;

namespace DeviceDeck.Android.Parsing;

/// <summary>
/// Runs several read-only commands in one shell round trip and splits the output again: each part is preceded by a
/// marker line "@@dd:name@@". A part that fails just leaves its section empty.
/// </summary>
internal static class ShellScript
{
    private const string Prefix = "@@dd:";
    private const string Suffix = "@@";

    public static string Build(params (string Name, string Command)[] parts)
    {
        var script = new StringBuilder();
        foreach (var (name, command) in parts)
        {
            script.Append("echo '").Append(Prefix).Append(name).Append(Suffix).Append("'; ");
            script.Append("{ ").Append(command).Append("; } 2>/dev/null; ");
        }
        script.Append("true");
        return script.ToString();
    }

    public static IReadOnlyDictionary<string, string> Split(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? name = null;
        var body = new StringBuilder();
        string[] lines = output.Split('\n');
        // A trailing newline leaves an empty last element that isn't a line of output.
        int count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        foreach (string raw in lines.Take(count))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith(Prefix, StringComparison.Ordinal) && line.EndsWith(Suffix, StringComparison.Ordinal) && line.Length > Prefix.Length + Suffix.Length)
            {
                Flush();
                name = line[Prefix.Length..^Suffix.Length];
                continue;
            }
            if (name != null)
            {
                body.Append(line).Append('\n');
            }
        }
        Flush();
        return sections;

        void Flush()
        {
            if (name != null)
            {
                sections[name] = body.ToString();
            }
            body.Clear();
        }
    }
}
