using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups.Contacts;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.Backups.Messages;

/// <summary>
/// The Messages app's database from a backup (HomeDomain/Library/SMS/sms.db): chats, their members and messages,
/// and attachments, which live in MediaDomain under Library/SMS/Attachments. Tapbacks (reactions) are left out.
/// Columns differ between iOS versions, so only the ones present are read.
/// </summary>
public sealed class MessageStore
{
    public const string Domain = "HomeDomain";
    public const string DatabasePath = "Library/SMS/sms.db";
    public const string AttachmentDomain = "MediaDomain";

    private static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _db;
    private readonly ContactNames _names;
    private readonly Func<string, bool> _inBackup;
    private readonly HashSet<string> _messageColumns;
    private readonly bool _hasAttachments;
    private IReadOnlyList<Conversation>? _conversations;

    /// <param name="inBackup">Whether a MediaDomain path is in the backup.</param>
    public MessageStore(SqliteConnection db, IReadOnlyList<Contact> contacts, Func<string, bool> inBackup)
    {
        _db = db;
        _names = new ContactNames(contacts);
        _inBackup = inBackup;
        _messageColumns = AddressBookReader.Columns(db, "message");
        _hasAttachments = AddressBookReader.Columns(db, "attachment").Contains("filename")
                          && AddressBookReader.Columns(db, "message_attachment_join").Contains("message_id");
        if (!_messageColumns.Contains("date") || !AddressBookReader.Columns(db, "chat_message_join").Contains("chat_id"))
        {
            throw new DeviceException(BackupErrors.Corrupt, "The Messages database in this backup has a layout DeviceDeck doesn't know.");
        }
    }

    /// <summary>Every conversation, newest first.</summary>
    public IReadOnlyList<Conversation> Conversations() => _conversations ??= ReadConversations();

    public Conversation? Find(string conversationId) => Conversations().FirstOrDefault(c => c.Id == conversationId);

    /// <summary>Oldest first, as a conversation reads.</summary>
    public Page<Message> Messages(string conversationId, int offset, int limit)
    {
        var chat = ChatId(conversationId);
        using var count = _db.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM chat_message_join j JOIN message m ON m.ROWID = j.message_id WHERE j.chat_id = $chat AND {NotReaction}";
        count.Parameters.AddWithValue("$chat", chat);
        var total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new Page<Message>(Read(chat, offset, limit), total);
    }

    public IReadOnlyList<Message> AllMessages(string conversationId) => Read(ChatId(conversationId), 0, int.MaxValue);

    /// <summary>Where an attachment's file is in the backup (MediaDomain path), or null.</summary>
    public (string Domain, string Path)? AttachmentPath(string attachmentId)
    {
        if (!_hasAttachments || !long.TryParse(attachmentId, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        using var command = _db.CreateCommand();
        command.CommandText = "SELECT filename FROM attachment WHERE ROWID = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string filename && BackupPathOf(filename) is { } path ? (AttachmentDomain, path) : null;
    }

    private string NotReaction => _messageColumns.Contains("associated_message_type") ? "COALESCE(m.associated_message_type, 0) = 0" : "1";

    private static long ChatId(string conversationId) =>
        long.TryParse(conversationId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new DeviceException(DeviceErrorCodes.PathNotFound, "That conversation isn't in this backup.");

    private List<Conversation> ReadConversations()
    {
        var chatColumns = AddressBookReader.Columns(_db, "chat");
        string Col(string name) => chatColumns.Contains(name) ? "c." + name : "NULL";

        var members = new Dictionary<long, List<ConversationMember>>();
        if (AddressBookReader.Columns(_db, "chat_handle_join").Contains("handle_id"))
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT j.chat_id, h.id FROM chat_handle_join j JOIN handle h ON h.ROWID = j.handle_id ORDER BY j.chat_id, h.ROWID";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                var chat = reader.GetInt64(0);
                if (!members.TryGetValue(chat, out var list))
                {
                    members[chat] = list = [];
                }

                var handle = reader.GetString(1);
                list.Add(new ConversationMember(handle, _names.Find(handle)));
            }
        }

        var attachments = new Dictionary<long, int>();
        if (_hasAttachments)
        {
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT j.chat_id, COUNT(*) FROM chat_message_join j JOIN message_attachment_join a ON a.message_id = j.message_id GROUP BY j.chat_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                attachments[reader.GetInt64(0)] = reader.GetInt32(1);
            }
        }

        var conversations = new List<Conversation>();
        using (var command = _db.CreateCommand())
        {
            command.CommandText = $"""
                SELECT c.ROWID, {Col("chat_identifier")}, {Col("display_name")}, {Col("service_name")}, {Col("style")},
                       (SELECT COUNT(*) FROM chat_message_join j JOIN message m ON m.ROWID = j.message_id WHERE j.chat_id = c.ROWID AND {NotReaction}),
                       (SELECT MAX(m.date) FROM chat_message_join j JOIN message m ON m.ROWID = j.message_id WHERE j.chat_id = c.ROWID AND {NotReaction})
                FROM chat c
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt64(0);
                var count = reader.GetInt32(5);
                if (count == 0)
                {
                    continue;
                }

                var identifier = reader.IsDBNull(1) ? null : reader.GetString(1);
                var people = members.GetValueOrDefault(id) ?? (identifier is null ? [] : [new ConversationMember(identifier, _names.Find(identifier))]);
                var isGroup = (!reader.IsDBNull(4) && reader.GetInt64(4) == 43) || people.Count > 1;
                var displayName = reader.IsDBNull(2) ? null : reader.GetString(2);
                var title = !string.IsNullOrWhiteSpace(displayName) ? displayName
                    : people.Count > 0 ? string.Join(", ", people.Select(p => p.Name ?? p.Handle))
                    : identifier ?? "?";
                conversations.Add(new Conversation(
                    id.ToString(CultureInfo.InvariantCulture),
                    title,
                    people,
                    isGroup,
                    reader.IsDBNull(3) ? "iMessage" : reader.GetString(3),
                    count,
                    attachments.GetValueOrDefault(id),
                    reader.IsDBNull(6) ? null : AppleTime(reader.GetInt64(6)),
                    Preview(id)));
            }
        }
        return conversations.OrderByDescending(c => c.LastMessageAt ?? DateTimeOffset.MinValue).ToList();
    }

    private string? Preview(long chat)
    {
        var body = _messageColumns.Contains("attributedBody") ? "m.attributedBody" : "NULL";
        using var command = _db.CreateCommand();
        command.CommandText = $"""
            SELECT m.text, {body} FROM chat_message_join j JOIN message m ON m.ROWID = j.message_id
            WHERE j.chat_id = $chat AND {NotReaction} ORDER BY m.date DESC, m.ROWID DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$chat", chat);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var text = TextOf(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1));
        return text is null ? null : text.Length <= 140 ? text : text[..140].TrimEnd() + "…";
    }

    private List<Message> Read(long chat, int offset, int limit)
    {
        string Col(string name) => _messageColumns.Contains(name) ? "m." + name : "NULL";
        using var command = _db.CreateCommand();
        command.CommandText = $"""
            SELECT m.ROWID, m.text, {Col("attributedBody")}, m.date, {Col("is_from_me")}, h.id, {Col("service")}, {Col("item_type")}, {Col("group_title")}
            FROM chat_message_join j JOIN message m ON m.ROWID = j.message_id
            LEFT JOIN handle h ON h.ROWID = m.handle_id
            WHERE j.chat_id = $chat AND {NotReaction}
            ORDER BY m.date, m.ROWID
            LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$chat", chat);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var rows = new List<(long Id, string? Text, DateTimeOffset? Date, bool FromMe, string? Handle, string Service, bool Event)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var itemType = reader.IsDBNull(7) ? 0 : reader.GetInt64(7);
                var text = itemType == 0
                    ? TextOf(reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2))
                    : reader.IsDBNull(8) ? null : reader.GetString(8);
                rows.Add((reader.GetInt64(0), text, reader.IsDBNull(3) ? null : AppleTime(reader.GetInt64(3)),
                    !reader.IsDBNull(4) && reader.GetInt64(4) != 0, reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? "iMessage" : reader.GetString(6), itemType != 0));
            }
        }

        var files = AttachmentsFor(rows.Select(r => r.Id).ToList());
        return rows.Select(r => new Message(
            r.Id.ToString(CultureInfo.InvariantCulture),
            r.Event ? MessageKind.Event : MessageKind.Text,
            r.Date,
            r.FromMe,
            r.FromMe ? null : r.Handle,
            r.FromMe || r.Handle is null ? null : _names.Find(r.Handle),
            r.Text,
            r.Service,
            files.GetValueOrDefault(r.Id) ?? (IReadOnlyList<MessageAttachment>)[])).ToList();
    }

    private Dictionary<long, List<MessageAttachment>> AttachmentsFor(List<long> messageIds)
    {
        var result = new Dictionary<long, List<MessageAttachment>>();
        if (!_hasAttachments || messageIds.Count == 0)
        {
            return result;
        }

        var columns = AddressBookReader.Columns(_db, "attachment");
        string Col(string name) => columns.Contains(name) ? "a." + name : "NULL";
        foreach (var chunk in messageIds.Chunk(500))
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"""
                SELECT j.message_id, a.ROWID, a.filename, {Col("mime_type")}, {Col("transfer_name")}, {Col("total_bytes")}
                FROM message_attachment_join j JOIN attachment a ON a.ROWID = j.attachment_id
                WHERE j.message_id IN ({string.Join(',', chunk)})
                ORDER BY j.message_id, a.ROWID
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var filename = reader.IsDBNull(2) ? null : reader.GetString(2);
                var path = filename is null ? null : BackupPathOf(filename);
                var name = reader.IsDBNull(4) ? null : reader.GetString(4);
                name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(filename ?? "") : name;
                var message = reader.GetInt64(0);
                if (!result.TryGetValue(message, out var list))
                {
                    result[message] = list = [];
                }

                list.Add(new MessageAttachment(
                    reader.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                    string.IsNullOrEmpty(name) ? "attachment" : name,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                    path is not null && _inBackup(path)));
            }
        }
        return result;
    }

    /// <summary>"~/Library/SMS/Attachments/ab/…" or "/var/mobile/Library/SMS/…" → "Library/SMS/…" in MediaDomain.</summary>
    public static string? BackupPathOf(string filename)
    {
        var path = filename.Replace('\\', '/');
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = path[2..];
        }
        else if (path.StartsWith("/var/mobile/", StringComparison.Ordinal))
        {
            path = path["/var/mobile/".Length..];
        }
        else if (path.StartsWith("/private/var/mobile/", StringComparison.Ordinal))
        {
            path = path["/private/var/mobile/".Length..];
        }

        return path.StartsWith("Library/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>Nanoseconds since 2001 (iOS 11 and later) or seconds (earlier).</summary>
    public static DateTimeOffset? AppleTime(long value)
    {
        if (value <= 0)
        {
            return null;
        }

        return value > 100_000_000_000L ? Epoch2001.AddTicks(value / 100) : Epoch2001.AddSeconds(value);
    }

    /// <summary>The plain text, or the text inside attributedBody (iOS 16 and later often leave "text" empty).</summary>
    public static string? TextOf(string? text, byte[]? attributedBody)
    {
        var value = !string.IsNullOrEmpty(text) ? text : attributedBody is null ? null : TypedStreamText(attributedBody);
        if (value is null)
        {
            return null;
        }
        // U+FFFC marks where an attachment sat in the text.
        value = value.Replace("￼", "", StringComparison.Ordinal).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The string in an NSAttributedString archived with NSArchiver ("streamtyped"): after the NSString class name
    /// comes '+', a length (one byte, or 0x81 and two bytes, or 0x82 and four, little-endian) and UTF-8 text.
    /// </summary>
    public static string? TypedStreamText(ReadOnlySpan<byte> blob)
    {
        var at = blob.IndexOf("NSString"u8);
        if (at < 0)
        {
            return null;
        }

        var rest = blob[(at + 8)..];
        var plus = rest[..Math.Min(rest.Length, 16)].IndexOf((byte)'+');
        if (plus < 0 || plus + 2 > rest.Length)
        {
            return null;
        }

        rest = rest[(plus + 1)..];
        int length;
        switch (rest[0])
        {
            case 0x81 when rest.Length >= 3:
                length = BinaryPrimitives.ReadUInt16LittleEndian(rest[1..]);
                rest = rest[3..];
                break;
            case 0x82 when rest.Length >= 5:
                length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(rest[1..]), int.MaxValue);
                rest = rest[5..];
                break;
            case < 0x80:
                length = rest[0];
                rest = rest[1..];
                break;
            default:
                return null;
        }
        return length <= rest.Length ? Encoding.UTF8.GetString(rest[..length]) : null;
    }

    /// <summary>Phone numbers and addresses to names from the same backup's address book.</summary>
    private sealed class ContactNames
    {
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

        public ContactNames(IReadOnlyList<Contact> contacts)
        {
            foreach (var contact in contacts)
            {
                foreach (var phone in contact.Phones)
                {
                    if (Key(phone.Value) is { } key)
                    {
                        _byKey.TryAdd(key, contact.DisplayName);
                    }
                }
                foreach (var email in contact.Emails)
                {
                    _byKey.TryAdd(email.Value.Trim().ToLowerInvariant(), contact.DisplayName);
                }
            }
        }

        public string? Find(string handle) =>
            handle.Contains('@') ? _byKey.GetValueOrDefault(handle.Trim().ToLowerInvariant())
            : Key(handle) is { } key ? _byKey.GetValueOrDefault(key) : null;

        /// <summary>The last ten digits, so "+98 912 …" and "0912 …" match.</summary>
        private static string? Key(string number)
        {
            var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length < 5)
            {
                return null;
            }

            return digits.Length > 10 ? digits[^10..] : digits;
        }
    }
}
