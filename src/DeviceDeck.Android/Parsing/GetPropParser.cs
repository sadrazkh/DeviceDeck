using System.Text;

namespace DeviceDeck.Android.Parsing;

/// <summary>Parses `getprop` output: "[key]: [value]" per line; a value may run over several lines.</summary>
internal static class GetPropParser
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pendingKey = null;
        var pendingValue = new StringBuilder();

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (pendingKey != null)
            {
                // Inside a multi-line value: it ends at the first line ending with ']'.
                pendingValue.Append('\n');
                if (line.EndsWith(']'))
                {
                    pendingValue.Append(line, 0, line.Length - 1);
                    props[pendingKey] = pendingValue.ToString();
                    pendingKey = null;
                }
                else
                {
                    pendingValue.Append(line);
                }
                continue;
            }

            if (!line.StartsWith('['))
            {
                continue;
            }
            int separator = line.IndexOf("]: [", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }
            string key = line[1..separator];
            string rest = line[(separator + 4)..];
            if (rest.EndsWith(']'))
            {
                props[key] = rest[..^1];
            }
            else
            {
                pendingKey = key;
                pendingValue.Clear().Append(rest);
            }
        }
        return props;
    }

    /// <summary>First non-empty value among <paramref name="keys"/>.</summary>
    public static string? First(this IReadOnlyDictionary<string, string> props, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (props.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        return null;
    }
}
