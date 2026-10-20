namespace DeviceDeck.Android.Parsing;

internal static class ShellQuote
{
    /// <summary>Single-quotes one argument for the phone's sh: 'it'\''s' — safe for spaces, $, `, quotes and Unicode.</summary>
    public static string Quote(string value)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("A shell argument can't contain a NUL character.", nameof(value));
        }
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>A literal for a SQL clause passed to `content --where`: 'it''s'.</summary>
    public static string SqlString(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
