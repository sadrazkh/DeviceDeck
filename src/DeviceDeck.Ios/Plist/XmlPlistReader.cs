using System.Globalization;
using System.Xml;

namespace DeviceDeck.Ios.Plist;

/// <summary>Reads Apple's XML plist format (PropertyList-1.0.dtd). DTDs are ignored, never fetched.</summary>
internal static class XmlPlistReader
{
    private const int MaxDepth = 64;

    public static object Read(ReadOnlySpan<byte> data)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            // Keep whitespace so "<string>  </string>" survives; MoveToContent skips it between elements.
            IgnoreWhitespace = false,
        };
        // Some devices pad messages with trailing NULs; XmlReader rejects them.
        var trimmed = data.TrimEnd((byte)0);
        try
        {
            using var stream = new MemoryStream(trimmed.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "plist")
                throw new FormatException("Not an XML property list: the root element is not <plist>.");
            if (reader.IsEmptyElement)
                throw new FormatException("Empty <plist> element.");
            reader.Read();
            reader.MoveToContent();
            return ReadValue(reader, 0);
        }
        catch (XmlException ex)
        {
            throw new FormatException("Malformed XML property list: " + ex.Message, ex);
        }
    }

    private static object ReadValue(XmlReader reader, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("Property list nests too deeply.");
        if (reader.NodeType != XmlNodeType.Element)
            throw new FormatException($"Expected a value element, found {reader.NodeType} '{reader.Name}'.");

        var name = reader.Name;
        switch (name)
        {
            case "dict":
                return ReadDict(reader, depth);
            case "array":
                return ReadArray(reader, depth);
            case "true":
            case "false":
                reader.Skip();
                return name == "true";
            case "string":
            case "key":
                return ReadText(reader);
            case "integer":
            {
                var text = ReadText(reader).Trim();
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
                    return l;
                if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var u))
                    return u;
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                    return h;
                throw new FormatException($"Bad <integer> value '{text}'.");
            }
            case "real":
            {
                var text = ReadText(reader).Trim();
                return text switch
                {
                    "nan" => double.NaN,
                    "+infinity" or "inf" or "+inf" => double.PositiveInfinity,
                    "-infinity" or "-inf" => double.NegativeInfinity,
                    _ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                        ? d
                        : throw new FormatException($"Bad <real> value '{text}'."),
                };
            }
            case "date":
            {
                var text = ReadText(reader).Trim();
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                    return date;
                throw new FormatException($"Bad <date> value '{text}'.");
            }
            case "data":
            {
                var text = ReadText(reader);
                try
                {
                    return Convert.FromBase64String(StripWhitespace(text));
                }
                catch (FormatException ex)
                {
                    throw new FormatException("Bad base64 in <data>.", ex);
                }
            }
            default:
                throw new FormatException($"Unknown plist element <{name}>.");
        }
    }

    private static PlistDict ReadDict(XmlReader reader, int depth)
    {
        var dict = new PlistDict();
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return dict;
        }
        reader.Read();
        while (true)
        {
            reader.MoveToContent();
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                reader.Read();
                return dict;
            }
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "key")
                throw new FormatException($"Expected <key> in <dict>, found '{reader.Name}'.");
            var key = ReadText(reader);
            reader.MoveToContent();
            dict[key] = ReadValue(reader, depth + 1);
        }
    }

    private static List<object> ReadArray(XmlReader reader, int depth)
    {
        var list = new List<object>();
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return list;
        }
        reader.Read();
        while (true)
        {
            reader.MoveToContent();
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                reader.Read();
                return list;
            }
            list.Add(ReadValue(reader, depth + 1));
        }
    }

    /// <summary>Reads an element's text content and moves past its end tag. Whitespace inside &lt;string&gt; is kept.</summary>
    private static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }
        var text = reader.ReadElementContentAsString();
        return text;
    }

    private static string StripWhitespace(string s)
    {
        Span<char> buffer = s.Length <= 1024 ? stackalloc char[s.Length] : new char[s.Length];
        var n = 0;
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
                buffer[n++] = c;
        }
        return new string(buffer[..n]);
    }
}
