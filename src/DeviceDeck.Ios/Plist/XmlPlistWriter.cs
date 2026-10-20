using System.Globalization;
using System.Text;
using System.Xml;

namespace DeviceDeck.Ios.Plist;

/// <summary>Writes Apple's XML plist format, the way lockdown, usbmux and the plist services expect it.</summary>
internal static class XmlPlistWriter
{
    public static byte[] Write(object root)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\n",
            OmitXmlDeclaration = false,
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            writer.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            writer.WriteStartElement("plist");
            writer.WriteAttributeString("version", "1.0");
            WriteValue(writer, root, 0);
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteValue(XmlWriter w, object value, int depth)
    {
        if (depth > 64)
            throw new InvalidOperationException("Property list nests too deeply.");
        switch (value)
        {
            case string s:
                w.WriteElementString("string", s);
                break;
            case bool b:
                w.WriteStartElement(b ? "true" : "false");
                w.WriteEndElement();
                break;
            case long or int or short or byte or sbyte or ushort or uint or ulong:
                w.WriteElementString("integer", Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
            case double d:
                w.WriteElementString("real", FormatReal(d));
                break;
            case float f:
                w.WriteElementString("real", FormatReal(f));
                break;
            case DateTimeOffset dto:
                w.WriteElementString("date", dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                w.WriteElementString("date", dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                break;
            case byte[] data:
                w.WriteElementString("data", Convert.ToBase64String(data));
                break;
            case IDictionary<string, object> dict:
                w.WriteStartElement("dict");
                foreach (var (k, v) in dict)
                {
                    w.WriteElementString("key", k);
                    WriteValue(w, v, depth + 1);
                }
                w.WriteEndElement();
                break;
            case System.Collections.IEnumerable list:
                w.WriteStartElement("array");
                foreach (var item in list)
                    WriteValue(w, item ?? throw new InvalidOperationException("Property lists cannot hold null."), depth + 1);
                w.WriteEndElement();
                break;
            case PlistUid uid:
                // XML has no UID type; CoreFoundation writes it as a CF$UID dictionary.
                w.WriteStartElement("dict");
                w.WriteElementString("key", "CF$UID");
                w.WriteElementString("integer", uid.Value.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
                break;
            default:
                throw new InvalidOperationException($"Cannot write {value.GetType().Name} to a property list.");
        }
    }

    private static string FormatReal(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "+infinity";
        if (double.IsNegativeInfinity(d)) return "-infinity";
        return d.ToString("R", CultureInfo.InvariantCulture);
    }
}
