using System.Globalization;

namespace DeviceDeck.Android.Parsing;

internal sealed record DfEntry(string Filesystem, long TotalBytes, long UsedBytes, long AvailableBytes, string MountedOn);

/// <summary>
/// Parses `df -k` from toybox (Android 6+): "Filesystem 1K-blocks Used Available Use% Mounted on". Also copes with
/// -h style sizes ("5.9G"), the old toolbox layout ("Size Used Free Blksize") and a long filesystem name that
/// wrapped onto its own line.
/// </summary>
internal static class DfParser
{
    public static IReadOnlyList<DfEntry> Parse(string text)
    {
        var entries = new List<DfEntry>();
        long unit = 1024;
        bool toolbox = false;
        string? wrappedFilesystem = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }
            string[] tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens[0] == "Filesystem")
            {
                unit = tokens.Length > 1 ? HeaderUnit(tokens[1]) : 1024;
                toolbox = line.Contains("Blksize", StringComparison.Ordinal);
                continue;
            }
            if (tokens[0].StartsWith("df:", StringComparison.Ordinal))
            {
                continue;
            }
            if (tokens.Length == 1)
            {
                wrappedFilesystem = tokens[0];
                continue;
            }
            if (wrappedFilesystem != null)
            {
                tokens = [wrappedFilesystem, .. tokens];
                wrappedFilesystem = null;
            }

            if (toolbox)
            {
                // toolbox: Filesystem(=mount point) Size Used Free Blksize
                if (tokens.Length >= 4 && TryBytes(tokens[1], unit, out long size) && TryBytes(tokens[2], unit, out long used)
                    && TryBytes(tokens[3], unit, out long free))
                {
                    entries.Add(new DfEntry(tokens[0], size, used, free, tokens[0]));
                }
                continue;
            }

            // toybox: Filesystem Total Used Available Use% Mounted-on
            if (tokens.Length >= 6 && TryBytes(tokens[1], unit, out long total) && TryBytes(tokens[2], unit, out long usedBytes)
                && TryBytes(tokens[3], unit, out long available))
            {
                string mount = string.Join(' ', tokens[5..]);
                entries.Add(new DfEntry(tokens[0], total, usedBytes, available, mount));
            }
        }
        return entries;
    }

    private static long HeaderUnit(string header)
    {
        // "1K-blocks", "1M-blocks", "512B-blocks", "Size"
        int dash = header.IndexOf("-blocks", StringComparison.Ordinal);
        if (dash > 0 && TryBytes(header[..dash], 1, out long blockSize))
        {
            return blockSize;
        }
        return 1;
    }

    /// <summary>A plain number is <paramref name="unit"/>s; a suffixed one (5.9G, 512M, 100K) is binary-scaled bytes.</summary>
    internal static bool TryBytes(string token, long unit, out long bytes)
    {
        bytes = 0;
        if (token.Length == 0)
        {
            return false;
        }
        char last = char.ToUpperInvariant(token[^1]);
        long multiplier = last switch
        {
            'B' => 1,
            'K' => 1L << 10,
            'M' => 1L << 20,
            'G' => 1L << 30,
            'T' => 1L << 40,
            'P' => 1L << 50,
            _ => 0,
        };
        if (multiplier == 0)
        {
            if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
            {
                return false;
            }
            bytes = count * unit;
            return true;
        }
        if (!double.TryParse(token[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
        {
            return false;
        }
        bytes = (long)(value * multiplier);
        return true;
    }
}
