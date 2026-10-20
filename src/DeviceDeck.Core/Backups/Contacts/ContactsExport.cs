using System.Text;

namespace DeviceDeck.Core.Backups.Contacts;

/// <summary>Writes contacts as vCard 3.0 (one .vcf with every card) or as CSV with Google Contacts' column names.</summary>
public static class ContactsExport
{
    /// <param name="photo">The contact's photo (JPEG), when it should go into the card.</param>
    public static void WriteVCard(TextWriter writer, Contact contact, byte[]? photo = null)
    {
        var item = 0;
        void Line(string line) => WriteFolded(writer, line);

        Line("BEGIN:VCARD");
        Line("VERSION:3.0");
        Line("N:" + string.Join(';', new[] { contact.LastName, contact.FirstName, contact.MiddleName, contact.Prefix, contact.Suffix }.Select(Escape)));
        Line("FN:" + Escape(contact.DisplayName));
        if (contact.Nickname is not null)
        {
            Line("NICKNAME:" + Escape(contact.Nickname));
        }

        if (contact.Organization is not null || contact.Department is not null)
        {
            Line("ORG:" + Escape(contact.Organization) + (contact.Department is null ? "" : ";" + Escape(contact.Department)));
        }

        if (contact.JobTitle is not null)
        {
            Line("TITLE:" + Escape(contact.JobTitle));
        }

        foreach (var phone in contact.Phones)
        {
            Labeled("TEL", phone.Label, PhoneTypes(phone.Label), phone.Value);
        }

        foreach (var email in contact.Emails)
        {
            Labeled("EMAIL", email.Label, "INTERNET" + (StandardType(email.Label) is { } t ? "," + t : ""), email.Value);
        }

        foreach (var address in contact.Addresses)
        {
            var value = string.Join(';', new[] { "", "", address.Street, address.City, address.Region, address.PostalCode, address.Country }.Select(Escape));
            Labeled("ADR", address.Label, StandardType(address.Label), value, escaped: true);
        }
        foreach (var url in contact.Urls)
        {
            Labeled("URL", url.Label, null, url.Value);
        }

        if (contact.Birthday is not null)
        {
            Line("BDAY:" + contact.Birthday);
        }

        if (contact.Note is not null)
        {
            Line("NOTE:" + Escape(contact.Note));
        }

        if (photo is { Length: > 0 })
        {
            Line("PHOTO;ENCODING=b;TYPE=JPEG:" + Convert.ToBase64String(photo));
        }

        Line("END:VCARD");

        // Known labels become TYPE parameters; the user's own become an Apple-style X-ABLabel on a grouped item.
        void Labeled(string property, string label, string? types, string value, bool escaped = false)
        {
            var text = escaped ? value : Escape(value);
            if (types is not null || IsGeneric(label))
            {
                Line(property + (types is null ? "" : ";TYPE=" + types) + ":" + text);
                return;
            }
            item++;
            Line($"item{item}.{property}:{text}");
            Line($"item{item}.X-ABLabel:{Escape(label)}");
        }
    }

    public static void WriteVCards(TextWriter writer, IEnumerable<Contact> contacts, Func<Contact, byte[]?>? photo = null)
    {
        foreach (var contact in contacts)
        {
            WriteVCard(writer, contact, photo?.Invoke(contact));
        }
    }

    /// <summary>CSV that Google Contacts, Outlook and Excel import; numbers and addresses get as many columns as the busiest contact needs.</summary>
    public static void WriteCsv(TextWriter writer, IReadOnlyList<Contact> contacts)
    {
        var phones = Math.Max(1, contacts.Select(c => c.Phones.Count).DefaultIfEmpty().Max());
        var emails = Math.Max(1, contacts.Select(c => c.Emails.Count).DefaultIfEmpty().Max());
        var addresses = Math.Max(1, contacts.Select(c => c.Addresses.Count).DefaultIfEmpty().Max());

        var header = new List<string> { "Name", "Given Name", "Additional Name", "Family Name", "Name Prefix", "Name Suffix", "Nickname", "Birthday", "Notes",
            "Organization 1 - Name", "Organization 1 - Department", "Organization 1 - Title" };
        for (var i = 1; i <= phones; i++)
        {
            header.AddRange([$"Phone {i} - Type", $"Phone {i} - Value"]);
        }

        for (var i = 1; i <= emails; i++)
        {
            header.AddRange([$"E-mail {i} - Type", $"E-mail {i} - Value"]);
        }

        for (var i = 1; i <= addresses; i++)
        {
            header.AddRange([$"Address {i} - Type", $"Address {i} - Street", $"Address {i} - City", $"Address {i} - Region", $"Address {i} - Postal Code", $"Address {i} - Country"]);
        }

        header.Add("Website 1 - Value");
        WriteRow(writer, header);

        foreach (var c in contacts)
        {
            var row = new List<string?> { c.DisplayName, c.FirstName, c.MiddleName, c.LastName, c.Prefix, c.Suffix, c.Nickname, c.Birthday, c.Note,
                c.Organization, c.Department, c.JobTitle };
            for (var i = 0; i < phones; i++)
            {
                row.AddRange(i < c.Phones.Count ? [CsvLabel(c.Phones[i].Label), c.Phones[i].Value] : [null, null]);
            }

            for (var i = 0; i < emails; i++)
            {
                row.AddRange(i < c.Emails.Count ? [CsvLabel(c.Emails[i].Label), c.Emails[i].Value] : [null, null]);
            }

            for (var i = 0; i < addresses; i++)
            {
                if (i < c.Addresses.Count)
                {
                    var a = c.Addresses[i];
                    row.AddRange([CsvLabel(a.Label), a.Street, a.City, a.Region, a.PostalCode, a.Country]);
                }
                else
                {
                    row.AddRange([null, null, null, null, null, null]);
                }
            }
            row.Add(c.Urls.FirstOrDefault()?.Value);
            WriteRow(writer, row);
        }
    }

    private static void WriteRow(TextWriter writer, IEnumerable<string?> cells)
    {
        writer.Write(string.Join(',', cells.Select(Quote)));
        writer.Write("\r\n");
    }

    private static string Quote(string? cell)
    {
        if (string.IsNullOrEmpty(cell))
        {
            return "";
        }
        // A leading = or @ would be run as a formula by Excel; + stays, phone numbers start with it.
        var safe = cell[0] is '=' or '@' ? "'" + cell : cell;
        return safe.IndexOfAny([',', '"', '\r', '\n']) >= 0 || safe != cell ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : safe;
    }

    private static string CsvLabel(string label) => label switch
    {
        "mobile" or "iphone" => "Mobile",
        "home" => "Home",
        "work" => "Work",
        "main" => "Main",
        "homeFax" => "Home Fax",
        "workFax" => "Work Fax",
        "otherFax" => "Other Fax",
        "pager" => "Pager",
        "other" or "phone" or "email" => "Other",
        "school" => "School",
        "homePage" => "Home Page",
        _ => label,
    };

    private static bool IsGeneric(string label) => label is "phone" or "email" or "other" or "homePage";

    private static string? StandardType(string label) => label switch
    {
        "home" => "HOME",
        "work" => "WORK",
        "other" => "OTHER",
        _ => null,
    };

    private static string? PhoneTypes(string label) => label switch
    {
        "mobile" => "CELL",
        "iphone" => "CELL,IPHONE",
        "home" => "HOME,VOICE",
        "work" => "WORK,VOICE",
        "main" => "MAIN",
        "homeFax" => "HOME,FAX",
        "workFax" => "WORK,FAX",
        "otherFax" => "FAX",
        "pager" => "PAGER",
        "other" => "OTHER",
        _ => null,
    };

    /// <summary>vCard 3.0 text escaping: backslash, comma, semicolon, newline.</summary>
    private static string Escape(string? text) => text is null
        ? ""
        : text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\n", StringComparison.Ordinal);

    /// <summary>Lines longer than 75 octets continue on the next line after a space, without splitting a UTF-8 character.</summary>
    private static void WriteFolded(TextWriter writer, string line)
    {
        var bytes = 0;
        var first = true;
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var width = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 4 : Encoding.UTF8.GetByteCount(line.AsSpan(i, 1));
            var limit = first ? 75 : 74;
            if (bytes + width > limit)
            {
                writer.Write(line.AsSpan(start, i - start));
                writer.Write("\r\n ");
                start = i;
                bytes = 0;
                first = false;
            }
            bytes += width;
            if (width == 4)
            {
                i++;
            }
        }
        writer.Write(line.AsSpan(start));
        writer.Write("\r\n");
    }
}
