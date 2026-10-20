using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Parsing;

/// <summary>
/// Reads the String16 out of `service call` output:
/// "Result: Parcel(0x00000000: 00000000 0000000f 00350033 ... '........3.5.')".
/// Word 0 is the exception code (0 = none), word 1 the UTF-16 length, then two chars per word, low half first.
/// </summary>
internal static partial class ParcelParser
{
    [GeneratedRegex(@"\b[0-9a-fA-F]{8}\b")]
    private static partial Regex Word();

    [GeneratedRegex(@"0x[0-9a-fA-F]+:\s*(?<words>(?:[0-9a-fA-F]{8}\s*){1,4})")]
    private static partial Regex DumpLine();

    public static string? ReadString(string output)
    {
        if (!output.Contains("Parcel(", StringComparison.Ordinal))
        {
            return null;
        }
        var words = new List<uint>();
        foreach (Match line in DumpLine().Matches(output))
        {
            foreach (Match word in Word().Matches(line.Groups["words"].Value))
            {
                words.Add(uint.Parse(word.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }
        if (words.Count < 2 || words[0] != 0)
        {
            return null; // an exception (0xffffffff = SecurityException) or empty reply
        }
        int length = (int)words[1];
        if (length <= 0 || length == -1 || length > 256)
        {
            return null;
        }
        var text = new StringBuilder(length);
        for (int i = 2; i < words.Count && text.Length < length; i++)
        {
            text.Append((char)(words[i] & 0xFFFF));
            if (text.Length < length)
            {
                text.Append((char)(words[i] >> 16));
            }
        }
        return text.Length == length ? text.ToString() : null;
    }

    /// <summary>15 digits with a valid Luhn check digit: guards against reading some other string as an IMEI.</summary>
    public static bool IsValidImei(string? value)
    {
        if (value is not { Length: 15 } || !value.All(char.IsAsciiDigit) || value.All(c => c == '0'))
        {
            return false;
        }
        int sum = 0;
        for (int i = 0; i < 15; i++)
        {
            int digit = value[14 - i] - '0';
            if (i % 2 == 1)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }
            sum += digit;
        }
        return sum % 10 == 0;
    }
}
