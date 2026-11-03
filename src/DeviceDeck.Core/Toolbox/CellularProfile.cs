using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DeviceDeck.Core.Toolbox;

public enum CellularProfileMode { Both, Ipv6 }

/// <param name="Xlat464">Only counts with <see cref="CellularProfileMode.Ipv6"/>: it matters when the line has no IPv4 of its own.</param>
public sealed record CellularProfileOptions(string Apn, CellularProfileMode Mode = CellularProfileMode.Both, bool AttachApn = true, bool Xlat464 = false);

/// <summary>
/// The configuration profile that lets an iPhone ask for IPv6 on its SIM, which Settings has no switch for. Keys and values
/// follow Apple's schema (apple/device-management, mdm/profiles/com.apple.cellular.yaml). The Ipv6Profile site builds the same
/// profile from the same choices, down to the UUIDs.
/// </summary>
public static partial class CellularProfile
{
    /// <summary>Fixed on purpose: a profile with the same identifier replaces the old one, so changing carrier leaves no second profile.</summary>
    public const string Identifier = "local.ipv6-profile.cellular";

    public static bool IsValidApn(string apn) => ApnPattern().IsMatch(apn);

    public static string FileName(CellularProfileOptions options) => $"ipv6-{options.Apn}.mobileconfig";

    public static byte[] Build(CellularProfileOptions options)
    {
        if (!IsValidApn(options.Apn))
        {
            throw new ArgumentException("An APN is letters, digits, dots, dashes and underscores, at most 100 of them.");
        }

        var apn = options.Apn;
        var ipv6Only = options.Mode == CellularProfileMode.Ipv6;
        // AllowedProtocolMask: 1 = IPv4, 2 = IPv6, 3 = both.
        var mask = ipv6Only ? 2 : 3;
        var allowed = ipv6Only ? "IPv6 only" : "IPv4 and IPv6";
        var xlat = ipv6Only && options.Xlat464;

        var item = new List<(string, object)>
        {
            ("Name", apn),
            ("AllowedProtocolMask", mask),
            ("AllowedProtocolMaskInRoaming", mask),
            ("AllowedProtocolMaskInDomesticRoaming", mask),
        };
        if (xlat)
        {
            item.Add(("EnableXLAT464", true));
        }

        var payload = new List<(string, object)>
        {
            ("PayloadType", "com.apple.cellular"),
            ("PayloadVersion", 1),
            ("PayloadIdentifier", Identifier + ".apn"),
            ("PayloadUUID", UuidFor("payload", options, xlat)),
            ("PayloadDisplayName", $"Cellular {apn}"),
            ("PayloadDescription", $"APN {apn}: {allowed}."),
        };
        if (options.AttachApn)
        {
            payload.Add(("AttachAPN", new List<(string, object)> { ("Name", apn), ("AllowedProtocolMask", mask) }));
        }

        payload.Add(("APNs", new object[] { item }));

        return ToPlist(new List<(string, object)>
        {
            ("PayloadType", "Configuration"),
            ("PayloadVersion", 1),
            ("PayloadIdentifier", Identifier),
            ("PayloadUUID", UuidFor("profile", options, xlat)),
            ("PayloadDisplayName", $"IPv6 ({apn})"),
            ("PayloadDescription", $"Allows {allowed} on the cellular APN {apn}. Remove this profile to go back to the phone's own settings."),
            ("PayloadRemovalDisallowed", false),
            ("PayloadContent", new object[] { payload }),
        });
    }

    /// <summary>The same choices always give the same file, so a profile can be compared and installed again.</summary>
    private static string UuidFor(string part, CellularProfileOptions options, bool xlat)
    {
        var mode = options.Mode == CellularProfileMode.Ipv6 ? "ipv6" : "both";
        var seed = $"{part}|{options.Apn}|{mode}|{(options.AttachApn ? "true" : "false")}|{(xlat ? "true" : "false")}";
        var hex = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(seed)));
        var variant = ((int.Parse(hex.AsSpan(16, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) & 0x3) | 0x8).ToString("x", CultureInfo.InvariantCulture);
        return $"{hex[..8]}-{hex[8..12]}-5{hex[13..16]}-{variant}{hex[17..20]}-{hex[20..32]}".ToUpperInvariant();
    }

    private static byte[] ToPlist(List<(string, object)> root)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "\t", NewLineChars = "\n" };
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, settings))
        {
            w.WriteStartDocument();
            w.WriteDocType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);
            w.WriteStartElement("plist");
            w.WriteAttributeString("version", "1.0");
            WriteValue(w, root);
            w.WriteEndElement();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static void WriteValue(XmlWriter w, object value)
    {
        switch (value)
        {
            case string s:
                w.WriteElementString("string", s);
                break;
            case int i:
                w.WriteElementString("integer", i.ToString(CultureInfo.InvariantCulture));
                break;
            case bool b:
                w.WriteStartElement(b ? "true" : "false");
                w.WriteEndElement();
                break;
            case object[] list:
                w.WriteStartElement("array");
                foreach (var item in list)
                {
                    WriteValue(w, item);
                }

                w.WriteEndElement();
                break;
            case List<(string Key, object Value)> dict:
                w.WriteStartElement("dict");
                foreach (var (key, item) in dict)
                {
                    w.WriteElementString("key", key);
                    WriteValue(w, item);
                }

                w.WriteEndElement();
                break;
            default:
                throw new InvalidOperationException($"Cannot write {value.GetType().Name} to a profile.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,98}[A-Za-z0-9])?$")]
    private static partial Regex ApnPattern();
}
