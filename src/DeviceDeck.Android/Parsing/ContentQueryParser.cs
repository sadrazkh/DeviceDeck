namespace DeviceDeck.Android.Parsing;

/// <summary>
/// Parses `content query --projection a:b:c` output: "Row: 0 a=1, b=IMG 1, 2.jpg, c=NULL".
/// Values are not escaped, so a value may contain ", " — but the columns always come in projection order,
/// so each value runs up to ", &lt;next column&gt;=". A value with a newline continues on the next line.
/// </summary>
internal static class ContentQueryParser
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string?>> Parse(string text, IReadOnlyList<string> projection)
    {
        var rows = new List<IReadOnlyDictionary<string, string?>>();
        foreach (string record in Records(text))
        {
            var row = ParseRecord(record, projection);
            if (row != null)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>Joins continuation lines onto the "Row: n" line they belong to.</summary>
    private static IEnumerable<string> Records(string text)
    {
        string? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("Row: ", StringComparison.Ordinal))
            {
                if (current != null)
                {
                    yield return current;
                }
                current = line;
            }
            else if (current != null)
            {
                current += "\n" + line;
            }
        }
        if (current != null)
        {
            yield return current;
        }
    }

    private static Dictionary<string, string?>? ParseRecord(string record, IReadOnlyList<string> projection)
    {
        // "Row: 12 " prefix
        int space = record.IndexOf(' ', "Row: ".Length);
        if (space < 0)
        {
            return null;
        }
        string body = record[(space + 1)..];
        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        int position = 0;
        for (int i = 0; i < projection.Count; i++)
        {
            string prefix = projection[i] + "=";
            if (string.CompareOrdinal(body, position, prefix, 0, prefix.Length) != 0)
            {
                return null; // not the layout we asked for
            }
            int start = position + prefix.Length;
            int end;
            if (i + 1 < projection.Count)
            {
                end = body.IndexOf(", " + projection[i + 1] + "=", start, StringComparison.Ordinal);
                if (end < 0)
                {
                    return null;
                }
                position = end + 2;
            }
            else
            {
                end = body.Length;
                // Trailing newline from a multi-line record.
                while (end > start && body[end - 1] == '\n')
                {
                    end--;
                }
            }
            string value = body[start..end];
            row[projection[i]] = value == "NULL" ? null : value;
        }
        return row;
    }
}
