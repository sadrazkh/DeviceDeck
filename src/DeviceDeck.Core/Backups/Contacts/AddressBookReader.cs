using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.Backups.Contacts;

/// <summary>
/// Reads the iPhone's address book from a backup: HomeDomain/Library/AddressBook/AddressBook.sqlitedb (people, and their
/// numbers, addresses and links in ABMultiValue) and AddressBookImages.sqlitedb (photos). Columns differ between iOS
/// versions, so only the ones present are read.
/// </summary>
public static class AddressBookReader
{
    public const string Domain = "HomeDomain";
    public const string DatabasePath = "Library/AddressBook/AddressBook.sqlitedb";
    public const string ImagesPath = "Library/AddressBook/AddressBookImages.sqlitedb";

    // ABMultiValue.property
    private const int PropertyPhone = 3;
    private const int PropertyEmail = 4;
    private const int PropertyAddress = 5;
    private const int PropertyUrl = 22;

    private static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <param name="images">AddressBookImages.sqlitedb, when the backup has it: marks who has a photo.</param>
    public static IReadOnlyList<Contact> Read(SqliteConnection db, SqliteConnection? images = null)
    {
        var columns = Columns(db, "ABPerson");
        if (columns.Count == 0)
        {
            return [];
        }

        var withPhoto = images is null ? [] : PeopleWithPhotos(images);
        var multi = ReadMultiValues(db);

        string Col(string name) => columns.Contains(name) ? name : "NULL";
        using var command = db.CreateCommand();
        command.CommandText = $"""
            SELECT ROWID, {Col("First")}, {Col("Middle")}, {Col("Last")}, {Col("Prefix")}, {Col("Suffix")}, {Col("Nickname")},
                   {Col("Organization")}, {Col("Department")}, {Col("JobTitle")}, {Col("Note")}, {Col("Birthday")}, {Col("ModificationDate")}
            FROM ABPerson
            """;
        var contacts = new List<Contact>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var values = multi.GetValueOrDefault(id) ?? new MultiValues();
            string? Text(int i) => reader.IsDBNull(i) ? null : Clean(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture));
            var first = Text(1);
            var middle = Text(2);
            var last = Text(3);
            var organization = Text(7);
            var name = string.Join(' ', new[] { Text(4), first, middle, last, Text(5) }.Where(p => p is not null));
            var display = name.Length > 0 ? name
                : organization ?? values.Phones.FirstOrDefault()?.Value ?? values.Emails.FirstOrDefault()?.Value ?? "";
            contacts.Add(new Contact(
                Id: id.ToString(CultureInfo.InvariantCulture),
                DisplayName: display,
                FirstName: first,
                MiddleName: middle,
                LastName: last,
                Prefix: Text(4),
                Suffix: Text(5),
                Nickname: Text(6),
                Organization: organization,
                Department: Text(8),
                JobTitle: Text(9),
                Note: Text(10),
                Birthday: Birthday(reader.IsDBNull(11) ? null : reader.GetValue(11)),
                Phones: values.Phones,
                Emails: values.Emails,
                Urls: values.Urls,
                Addresses: values.Addresses,
                HasPhoto: withPhoto.Contains(id),
                Modified: Seconds2001(reader.IsDBNull(12) ? null : reader.GetValue(12))));
        }
        return contacts
            .Where(c => c.DisplayName.Length > 0)
            .OrderBy(c => c.LastName ?? c.FirstName ?? c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The largest photo of one person (JPEG as the phone stored it), or null.</summary>
    public static byte[]? Photo(SqliteConnection images, string contactId)
    {
        foreach (var table in new[] { "ABFullSizeImage", "ABThumbnailImage" })
        {
            if (!Columns(images, table).Contains("data"))
            {
                continue;
            }

            using var command = images.CreateCommand();
            command.CommandText = $"SELECT data FROM {table} WHERE record_id = $id AND data IS NOT NULL ORDER BY length(data) DESC LIMIT 1";
            command.Parameters.AddWithValue("$id", long.Parse(contactId, CultureInfo.InvariantCulture));
            if (command.ExecuteScalar() is byte[] { Length: > 0 } data)
            {
                return data;
            }
        }
        return null;
    }

    private sealed class MultiValues
    {
        public List<LabeledValue> Phones { get; } = [];
        public List<LabeledValue> Emails { get; } = [];
        public List<LabeledValue> Urls { get; } = [];
        public List<PostalAddress> Addresses { get; } = [];
    }

    private static Dictionary<long, MultiValues> ReadMultiValues(SqliteConnection db)
    {
        var result = new Dictionary<long, MultiValues>();
        var columns = Columns(db, "ABMultiValue");
        if (!columns.Contains("record_id") || !columns.Contains("property"))
        {
            return result;
        }

        var labels = new Dictionary<long, string>();
        if (Columns(db, "ABMultiValueLabel").Contains("value"))
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT ROWID, value FROM ABMultiValueLabel";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                {
                    labels[reader.GetInt64(0)] = reader.GetString(1);
                }
            }
        }

        var addressParts = ReadAddressParts(db);
        using (var command = db.CreateCommand())
        {
            var uid = columns.Contains("UID") ? "UID" : "ROWID";
            var label = columns.Contains("label") ? "label" : "NULL";
            var identifier = columns.Contains("identifier") ? "identifier" : "0";
            command.CommandText = $"SELECT {uid}, record_id, property, {label}, value FROM ABMultiValue ORDER BY record_id, {identifier}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var person = reader.GetInt64(1);
                if (!result.TryGetValue(person, out var values))
                {
                    result[person] = values = new MultiValues();
                }

                var text = labels.GetValueOrDefault(reader.IsDBNull(3) ? -1 : reader.GetInt64(3));
                var labelKey = NormalizeLabel(text);
                var value = reader.IsDBNull(4) ? null : Clean(Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture));
                switch (reader.GetInt32(2))
                {
                    case PropertyPhone when value is not null:
                        values.Phones.Add(new LabeledValue(labelKey ?? "phone", value));
                        break;
                    case PropertyEmail when value is not null:
                        values.Emails.Add(new LabeledValue(labelKey ?? "email", value));
                        break;
                    case PropertyUrl when value is not null:
                        values.Urls.Add(new LabeledValue(labelKey ?? "homePage", value));
                        break;
                    case PropertyAddress:
                        if (addressParts.TryGetValue(reader.GetInt64(0), out var parts))
                        {
                            values.Addresses.Add(new PostalAddress(labelKey ?? "home",
                                parts.GetValueOrDefault("Street"),
                                parts.GetValueOrDefault("City"),
                                parts.GetValueOrDefault("State"),
                                parts.GetValueOrDefault("ZIP"),
                                parts.GetValueOrDefault("Country")));
                        }
                        break;
                }
            }
        }
        return result;
    }

    /// <summary>ABMultiValueEntry: one row per address part, keyed through ABMultiValueEntryKey.</summary>
    private static Dictionary<long, Dictionary<string, string>> ReadAddressParts(SqliteConnection db)
    {
        var result = new Dictionary<long, Dictionary<string, string>>();
        if (!Columns(db, "ABMultiValueEntry").Contains("parent_id") || !Columns(db, "ABMultiValueEntryKey").Contains("value"))
        {
            return result;
        }

        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT e.parent_id, k.value, e.value
            FROM ABMultiValueEntry e JOIN ABMultiValueEntryKey k ON k.ROWID = e.key
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2) || Clean(reader.GetValue(2).ToString()) is not { } value)
            {
                continue;
            }

            var parent = reader.GetInt64(0);
            if (!result.TryGetValue(parent, out var parts))
            {
                result[parent] = parts = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            parts[reader.GetString(1)] = value;
        }
        return result;
    }

    private static HashSet<long> PeopleWithPhotos(SqliteConnection images)
    {
        var people = new HashSet<long>();
        foreach (var table in new[] { "ABFullSizeImage", "ABThumbnailImage" })
        {
            if (!Columns(images, table).Contains("record_id"))
            {
                continue;
            }

            using var command = images.CreateCommand();
            command.CommandText = $"SELECT DISTINCT record_id FROM {table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                people.Add(reader.GetInt64(0));
            }
        }
        return people;
    }

    /// <summary>"_$!&lt;Mobile&gt;!$_" → "mobile"; the user's own labels stay as typed.</summary>
    public static string? NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        var inner = label.StartsWith("_$!<", StringComparison.Ordinal) && label.EndsWith(">!$_", StringComparison.Ordinal) ? label[4..^4] : label;
        return inner.ToLowerInvariant() switch
        {
            "mobile" => "mobile",
            "iphone" => "iphone",
            "home" => "home",
            "work" => "work",
            "main" => "main",
            "homefax" => "homeFax",
            "workfax" => "workFax",
            "otherfax" => "otherFax",
            "pager" => "pager",
            "other" => "other",
            "school" => "school",
            "homepage" => "homePage",
            _ => inner,
        };
    }

    /// <summary>Seconds since 2001 as text or a number; year 1604 is how iOS stores a birthday without a year.</summary>
    private static string? Birthday(object? value)
    {
        if (Seconds2001(value) is not { } when)
        {
            return null;
        }

        var date = when.UtcDateTime;
        return date.Year == 1604
            ? date.ToString("--MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? Seconds2001(object? value)
    {
        var seconds = value switch
        {
            double d => d,
            long l => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => double.NaN,
        };
        if (double.IsNaN(seconds) || Math.Abs(seconds) > 2e10)
        {
            return null;
        }
        // Birthdays are stored at noon GMT, so the date part is the same in every time zone.
        return Epoch2001.AddSeconds(seconds);
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    internal static HashSet<string> Columns(SqliteConnection db, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = db.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "", StringComparison.Ordinal)}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
