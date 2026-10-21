using System.Globalization;
using System.Text;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Backups;
using DeviceDeck.Core.Backups.Contacts;
using Microsoft.Data.Sqlite;

namespace DeviceDeck.Core.WhatsApp;

/// <summary>
/// WhatsApp's chat database from an iPhone backup: ChatStorage.sqlite at the top of the app group
/// (AppDomainGroup-group.net.whatsapp.WhatsApp.shared, or …WhatsAppSMB… for WhatsApp Business). It is a Core Data store:
/// ZWACHATSESSION (chats), ZWAMESSAGE (messages), ZWAMEDIAITEM (files, locations, contact cards and reply links),
/// ZWAGROUPMEMBER (group members) and ZWAPROFILEPUSHNAME (the names people give themselves). Every table and column
/// is looked up first; one this WhatsApp version lacks reads as empty and is listed in <see cref="Missing"/>, so other
/// versions show less rather than fail. Media paths (ZMEDIALOCALPATH) are relative to the group's Message folder.
/// Written from the public descriptions of the schema; the meaning of a few type numbers is a best reading (see the
/// round 4 notes), and what isn't known is shown as "other" rather than guessed.
/// </summary>
public sealed class WhatsAppStore
{
    public const string WhatsAppDomain = "AppDomainGroup-group.net.whatsapp.WhatsApp.shared";
    public const string BusinessDomain = "AppDomainGroup-group.net.whatsapp.WhatsAppSMB.shared";
    public const string WhatsAppBundle = "net.whatsapp.WhatsApp";
    public const string BusinessBundle = "net.whatsapp.WhatsAppSMB";
    public const string DatabasePath = "ChatStorage.sqlite";
    public const string ContactsPath = "ContactsV2.sqlite";

    private static readonly DateTimeOffset Epoch2001 = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const int PreviewLength = 140;

    private readonly SqliteConnection _db;
    private readonly Func<string, BackupEntry?> _find;
    private readonly Names _names;
    private readonly SortedSet<string> _missing = new(StringComparer.Ordinal);
    private readonly Table _chat;
    private readonly Table _message;
    private readonly Table _media;
    private readonly Table _member;
    private readonly string _joins;
    private readonly string _order;
    private readonly string _statsSql;
    private readonly string _previewSql;
    private readonly string _chatsSql;
    private readonly string _rowsSql;
    private readonly string _mediaSql;
    private IReadOnlyList<WhatsAppChat>? _chats;
    private (int Media, int InBackup, long Bytes)? _mediaTotals;

    /// <param name="find">A path in this account's app group ("Message/Media/…") → the backup's entry, or null.</param>
    /// <param name="whatsAppContacts">ContactsV2.sqlite from the same group (WhatsApp's own copy of the address book), when present.</param>
    /// <param name="addressBook">The iPhone's contacts from the same backup, to name numbers the way the phone does.</param>
    public WhatsAppStore(WhatsAppAccount account, SqliteConnection db, Func<string, BackupEntry?> find, SqliteConnection? whatsAppContacts, IReadOnlyList<Contact> addressBook)
    {
        Account = account;
        Prefix = account == WhatsAppAccount.Business ? "b" : "w";
        _db = db;
        _find = find;
        _chat = new Table(db, "ZWACHATSESSION", "s", _missing);
        _message = new Table(db, "ZWAMESSAGE", "m", _missing);
        _media = new Table(db, "ZWAMEDIAITEM", "i", _missing);
        _member = new Table(db, "ZWAGROUPMEMBER", "g", _missing);
        if (!_chat.Has("Z_PK") || !_message.Has("Z_PK") || !_message.Has("ZCHATSESSION"))
        {
            throw new DeviceException(BackupErrors.Corrupt, "The WhatsApp database in this backup has a layout DeviceDeck doesn't know.");
        }

        // Media: the message points at its item (ZMEDIAITEM), or the item at its message (ZMESSAGE).
        var mediaJoin = !_media.Exists ? ""
            : _message.Has("ZMEDIAITEM") ? " LEFT JOIN ZWAMEDIAITEM i ON i.Z_PK = m.ZMEDIAITEM"
            : _media.Has("ZMESSAGE") ? " LEFT JOIN ZWAMEDIAITEM i ON i.Z_PK = (SELECT MIN(x.Z_PK) FROM ZWAMEDIAITEM x WHERE x.ZMESSAGE = m.Z_PK)"
            : "";
        if (_media.Exists && mediaJoin.Length == 0)
        {
            _missing.Add("ZWAMESSAGE.ZMEDIAITEM");
            _media.Disable();
        }

        var memberJoin = _member.Exists && _message.Has("ZGROUPMEMBER") ? " LEFT JOIN ZWAGROUPMEMBER g ON g.Z_PK = m.ZGROUPMEMBER" : "";
        if (memberJoin.Length == 0)
        {
            if (_member.Exists)
            {
                _missing.Add("ZWAMESSAGE.ZGROUPMEMBER");
            }

            _member.Disable();
        }

        _joins = memberJoin + mediaJoin;
        var date = _message.Col("ZMESSAGEDATE");
        _order = date == "NULL" ? "m.Z_PK" : $"{date}, m.Z_PK";
        var hasFile = _media.Has("ZMEDIALOCALPATH") ? "(i.ZMEDIALOCALPATH IS NOT NULL AND i.ZMEDIALOCALPATH <> '')" : "0";

        _statsSql = $"""
            SELECT m.ZCHATSESSION, COUNT(*), SUM(CASE WHEN {hasFile} THEN 1 ELSE 0 END), MAX({date})
            FROM ZWAMESSAGE m{mediaJoin}
            WHERE m.ZCHATSESSION IS NOT NULL
            GROUP BY m.ZCHATSESSION
            """;
        var mediaColumns = $"{_media.Col("Z_PK")}, {_media.Col("ZMEDIALOCALPATH")}, {_media.Col("ZTITLE")}, {_media.Col("ZLATITUDE")}, {_media.Col("ZLONGITUDE")}, {_media.Col("ZVCARDNAME")}, {_media.Col("ZVCARDSTRING")}";
        var newestFirst = date == "NULL" ? "m.Z_PK DESC" : $"{date} DESC, m.Z_PK DESC";
        _previewSql = $"""
            SELECT ZCHATSESSION, T, X, MPK, PATH, TITLE, LAT, LON, VNAME, VSTRING FROM (
              SELECT m.ZCHATSESSION, {_message.Col("ZMESSAGETYPE")} AS T, {_message.Col("ZTEXT")} AS X,
                     {_media.Col("Z_PK")} AS MPK, {_media.Col("ZMEDIALOCALPATH")} AS PATH, {_media.Col("ZTITLE")} AS TITLE,
                     {_media.Col("ZLATITUDE")} AS LAT, {_media.Col("ZLONGITUDE")} AS LON, {_media.Col("ZVCARDNAME")} AS VNAME, {_media.Col("ZVCARDSTRING")} AS VSTRING,
                     ROW_NUMBER() OVER (PARTITION BY m.ZCHATSESSION ORDER BY {newestFirst}) AS RN
              FROM ZWAMESSAGE m{mediaJoin}
              WHERE m.ZCHATSESSION IS NOT NULL
            ) WHERE RN = 1
            """;
        _chatsSql = $"""
            SELECT s.Z_PK, {_chat.Col("ZCONTACTJID")}, {_chat.Col("ZPARTNERNAME")}, {_chat.Col("ZSESSIONTYPE")}, {_chat.Col("ZARCHIVED")}
            FROM ZWACHATSESSION s
            """;
        _rowsSql = $"""
            SELECT m.Z_PK, {_message.Col("ZMESSAGETYPE")}, {date}, {_message.Col("ZISFROMME")}, {_message.Col("ZFROMJID")}, {_message.Col("ZTEXT")},
                   {_message.Col("ZSTARRED")}, {_message.Col("ZPUSHNAME")}, {_message.Col("ZSTANZAID")},
                   {_member.Col("ZMEMBERJID")}, {_member.Col("ZCONTACTNAME")}, {_member.Col("ZFIRSTNAME")},
                   {mediaColumns}, {_media.Col("ZFILESIZE")}, {_media.Col("ZMOVIEDURATION")}, {_media.Col("ZMETADATA")},
                   {_media.Col("ZXMPPTHUMBPATH")}, {_media.Col("ZTHUMBNAILLOCALPATH")}
            FROM ZWAMESSAGE m{_joins}
            """;
        _mediaSql = $"SELECT {_media.Col("Z_PK")}, {_media.Col("ZMEDIALOCALPATH")}, {_media.Col("ZXMPPTHUMBPATH")}, {_media.Col("ZTHUMBNAILLOCALPATH")} FROM ZWAMEDIAITEM i";
        _names = new Names(db, whatsAppContacts, addressBook, _missing);
    }

    public WhatsAppAccount Account { get; }

    /// <summary>"w" or "b": the start of every id this store hands out.</summary>
    public string Prefix { get; }

    /// <summary>"ZWAMESSAGE.ZSTARRED", "ZWAGROUPMEMBER": what this WhatsApp version doesn't have, so isn't shown.</summary>
    public IReadOnlyCollection<string> Missing => _missing;

    public string Domain => Account == WhatsAppAccount.Business ? BusinessDomain : WhatsAppDomain;

    /// <summary>Every chat with at least one message, newest first. Status updates (status@broadcast) are left out.</summary>
    public IReadOnlyList<WhatsAppChat> Chats() => _chats ??= ReadChats();

    public WhatsAppChat? Find(string chatId) => Chats().FirstOrDefault(c => c.Id == chatId);

    /// <summary>The account's line on the status: chats, messages, media and what of it the backup holds.</summary>
    public WhatsAppAccountInfo Summary()
    {
        var chats = Chats();
        _mediaTotals ??= MediaTotals();
        var (_, inBackup, bytes) = _mediaTotals.Value;
        return new WhatsAppAccountInfo(Account, Account == WhatsAppAccount.Business ? "WhatsApp Business" : "WhatsApp", chats.Count,
            chats.Sum(c => c.MessageCount), chats.Sum(c => c.MediaCount), inBackup, bytes);
    }

    /// <summary>Oldest first, as a chat reads.</summary>
    public Page<WhatsAppMessage> Messages(string chatId, int offset, int limit)
    {
        var chat = Find(chatId) ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That chat isn't in this backup.");
        return new Page<WhatsAppMessage>(Read(chat, offset, limit), chat.MessageCount);
    }

    public IReadOnlyList<WhatsAppMessage> AllMessages(string chatId)
    {
        var chat = Find(chatId) ?? throw new DeviceException(DeviceErrorCodes.PathNotFound, "That chat isn't in this backup.");
        return Read(chat, 0, int.MaxValue);
    }

    /// <summary>Where a media item's file and thumbnail are in the backup; paths inside <see cref="Domain"/>, null when it holds none.</summary>
    public (string? File, string? Thumbnail)? Locate(string mediaId)
    {
        if (RowOf(mediaId) is not { } row || !_media.Exists)
        {
            return null;
        }

        using var command = _db.CreateCommand();
        command.CommandText = _mediaSql + " WHERE i.Z_PK = $id";
        command.Parameters.AddWithValue("$id", row);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var file = BackupPathOf(ReadText(reader, 1));
        return (file is not null && _find(file) is { IsDirectory: false } ? file : null, ThumbnailOf(file, ReadText(reader, 2), ReadText(reader, 3)));
    }

    // ---------------------------------------------------------------- reading

    private List<WhatsAppChat> ReadChats()
    {
        var stats = new Dictionary<long, (int Count, int Media, DateTimeOffset? Last)>();
        using (var command = _db.CreateCommand())
        {
            command.CommandText = _statsSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                stats[reader.GetInt64(0)] = (reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2), DateOf(reader, 3));
            }
        }

        var previews = new Dictionary<long, (string? Text, WhatsAppMediaKind? Media)>();
        using (var command = _db.CreateCommand())
        {
            command.CommandText = _previewSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var text = Clean(ReadText(reader, 2));
                WhatsAppMediaKind? media = null;
                if (!reader.IsDBNull(3))
                {
                    var file = ReadText(reader, 4);
                    var (lat, lon) = (Number(reader, 6), Number(reader, 7));
                    var kind = KindOf(Long(reader, 1), file, MimeOf(ReadText(reader, 9)), lat is not null and not 0 || lon is not null and not 0, IsVCard(ReadText(reader, 9)));
                    media = !string.IsNullOrEmpty(file) || kind is WhatsAppMediaKind.Location or WhatsAppMediaKind.Contact ? kind : null;
                }
                previews[reader.GetInt64(0)] = (text is null ? null : text.Length <= PreviewLength ? text : text[..PreviewLength].TrimEnd() + "…", media);
            }
        }

        var chats = new List<WhatsAppChat>();
        using (var command = _db.CreateCommand())
        {
            command.CommandText = _chatsSql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var pk = reader.GetInt64(0);
                var jid = ReadText(reader, 1);
                if (!stats.TryGetValue(pk, out var stat) || stat.Count == 0 || jid is "status@broadcast")
                {
                    continue;
                }

                var isGroup = jid?.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) == true || Long(reader, 3) == 1;
                var phone = isGroup ? null : PhoneOf(jid);
                var partner = Clean(ReadText(reader, 2));
                var title = partner ?? (isGroup ? null : _names.Find(jid)) ?? phone ?? UserOf(jid) ?? "?";
                var preview = previews.GetValueOrDefault(pk);
                chats.Add(new WhatsAppChat($"{Prefix}:{pk.ToString(CultureInfo.InvariantCulture)}", Account, title, jid, phone, isGroup, stat.Count, stat.Media,
                    stat.Last, preview.Text, preview.Media, Long(reader, 4) is > 0));
            }
        }
        return chats.OrderByDescending(c => c.LastMessageAt ?? DateTimeOffset.MinValue).ToList();
    }

    private List<WhatsAppMessage> Read(WhatsAppChat chat, int offset, int limit)
    {
        var chatRow = RowOf(chat.Id)!.Value;
        using var command = _db.CreateCommand();
        command.CommandText = _rowsSql + $" WHERE m.ZCHATSESSION = $chat ORDER BY {_order} LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$chat", chatRow);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var rows = new List<Row>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(Row.From(reader));
            }
        }

        var quotes = Quotes(chat, chatRow, rows);
        return rows.Select(r => ToMessage(chat, r, quotes)).ToList();
    }

    private WhatsAppMessage ToMessage(WhatsAppChat chat, Row r, IReadOnlyDictionary<string, WhatsAppQuote> quotes)
    {
        var text = Clean(r.Text);
        var media = MediaOf(r);
        var kind = r.Type switch
        {
            6 => WhatsAppMessageKind.Event,
            14 when text is null && media is null => WhatsAppMessageKind.Deleted,
            _ when media is not null => WhatsAppMessageKind.Media,
            _ when text is not null => WhatsAppMessageKind.Text,
            _ => WhatsAppMessageKind.Other,
        };
        // A photo's or video's caption may sit in the media item's title rather than the message.
        if (kind == WhatsAppMessageKind.Media && text is null && media!.Kind is WhatsAppMediaKind.Image or WhatsAppMediaKind.Video or WhatsAppMediaKind.Gif)
        {
            text = Clean(r.Title);
        }

        string? senderJid = null;
        string? senderName = null;
        if (!r.FromMe && chat.IsGroup && kind != WhatsAppMessageKind.Event)
        {
            senderJid = r.MemberJid ?? (r.FromJid is { } from && !from.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ? from : null);
            senderName = _names.Find(senderJid, Clean(r.MemberContactName) ?? Clean(r.MemberFirstName), Clean(r.PushName));
        }

        var quote = QuotedId(r.Metadata) is { } quoted ? quotes.GetValueOrDefault(quoted) : null;
        return new WhatsAppMessage($"{Prefix}:{r.Pk.ToString(CultureInfo.InvariantCulture)}", kind, r.Date, r.FromMe, senderJid, senderName,
            kind is WhatsAppMessageKind.Deleted or WhatsAppMessageKind.Other ? null : text, r.Starred, quote, kind == WhatsAppMessageKind.Media ? media : null);
    }

    private WhatsAppMedia? MediaOf(Row r)
    {
        if (r.MediaPk is null)
        {
            return null;
        }

        var hasLocation = r.Latitude is not null and not 0 || r.Longitude is not null and not 0;
        var vcard = IsVCard(r.VCardString);
        var file = BackupPathOf(r.LocalPath);
        if (file is null && !hasLocation && !vcard)
        {
            // A text message's reply link lives in a media item without a file.
            return null;
        }

        var kind = KindOf(r.Type, r.LocalPath, MimeOf(r.VCardString), hasLocation, vcard);
        var entry = file is null ? null : _find(file) is { IsDirectory: false } found ? found : null;
        var name = kind switch
        {
            WhatsAppMediaKind.Contact => Clean(r.VCardName) ?? Clean(r.Title) ?? "Contact",
            WhatsAppMediaKind.Location => Clean(r.Title) ?? "Location",
            WhatsAppMediaKind.Document => Clean(r.Title) ?? FileName(r.LocalPath) ?? "Document",
            _ => FileName(r.LocalPath) ?? Clean(r.Title) ?? kind.ToString(),
        };
        var thumbnail = entry is not null && kind is WhatsAppMediaKind.Image or WhatsAppMediaKind.Sticker
                        || ThumbnailOf(file, r.XmppThumbPath, r.ThumbnailPath) is not null;
        return new WhatsAppMedia(
            $"{Prefix}:{r.MediaPk.Value.ToString(CultureInfo.InvariantCulture)}",
            kind,
            name,
            MimeOf(r.VCardString) ?? MimeOfName(name) ?? MimeOfName(r.LocalPath),
            r.FileSize is > 0 ? r.FileSize.Value : entry?.Size ?? 0,
            r.Duration is > 0 ? r.Duration : null,
            entry is not null,
            thumbnail,
            hasLocation ? r.Latitude : null,
            hasLocation ? r.Longitude : null);
    }

    /// <summary>The messages the page's rows reply to, by stanza id, looked up in one query (a prefix match for ids WhatsApp shortened).</summary>
    private Dictionary<string, WhatsAppQuote> Quotes(WhatsAppChat chat, long chatRow, List<Row> rows)
    {
        var result = new Dictionary<string, WhatsAppQuote>(StringComparer.Ordinal);
        var wanted = rows.Select(r => QuotedId(r.Metadata)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0 || !_message.Has("ZSTANZAID"))
        {
            return result;
        }

        foreach (var chunk in wanted.Chunk(200))
        {
            using var command = _db.CreateCommand();
            var names = chunk.Select((_, n) => "$q" + n.ToString(CultureInfo.InvariantCulture)).ToList();
            command.CommandText = _rowsSql + $" WHERE m.ZCHATSESSION = $chat AND m.ZSTANZAID IN ({string.Join(", ", names)})";
            command.Parameters.AddWithValue("$chat", chatRow);
            for (var n = 0; n < chunk.Length; n++)
            {
                command.Parameters.AddWithValue(names[n], chunk[n]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = Row.From(reader);
                if (row.StanzaId is { } id)
                {
                    result.TryAdd(id, QuoteOf(chat, row));
                }
            }
        }

        foreach (var id in wanted.Where(id => !result.ContainsKey(id) && id.Length >= 12))
        {
            using var command = _db.CreateCommand();
            command.CommandText = _rowsSql + " WHERE m.ZCHATSESSION = $chat AND substr(m.ZSTANZAID, 1, $length) = $id LIMIT 1";
            command.Parameters.AddWithValue("$chat", chatRow);
            command.Parameters.AddWithValue("$length", id.Length);
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                result[id] = QuoteOf(chat, Row.From(reader));
            }
        }
        return result;
    }

    private WhatsAppQuote QuoteOf(WhatsAppChat chat, Row r)
    {
        var media = MediaOf(r);
        var text = Clean(r.Text) ?? (media?.Kind is WhatsAppMediaKind.Image or WhatsAppMediaKind.Video ? Clean(r.Title) : null);
        string? sender = null;
        if (!r.FromMe)
        {
            sender = chat.IsGroup ? _names.Find(r.MemberJid, Clean(r.MemberContactName) ?? Clean(r.MemberFirstName), Clean(r.PushName)) : chat.Title;
        }

        return new WhatsAppQuote($"{Prefix}:{r.Pk.ToString(CultureInfo.InvariantCulture)}", r.FromMe, sender,
            text is null ? null : text.Length <= PreviewLength ? text : text[..PreviewLength].TrimEnd() + "…", media?.Kind);
    }

    private (int Media, int InBackup, long Bytes) MediaTotals()
    {
        if (!_media.Has("ZMEDIALOCALPATH"))
        {
            return (0, 0, 0);
        }

        var media = 0;
        var inBackup = 0;
        var bytes = 0L;
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT ZMEDIALOCALPATH FROM ZWAMEDIAITEM WHERE ZMEDIALOCALPATH IS NOT NULL AND ZMEDIALOCALPATH <> ''";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            media++;
            if (BackupPathOf(ReadText(reader, 0)) is { } path && _find(path) is { IsDirectory: false } entry)
            {
                inBackup++;
                bytes += entry.Size;
            }
        }
        return (media, inBackup, bytes);
    }

    private string? ThumbnailOf(string? file, string? xmppThumb, string? localThumb)
    {
        foreach (var candidate in new[] { BackupPathOf(xmppThumb), BackupPathOf(localThumb), file is null ? null : Path.ChangeExtension(file, ".thumb") })
        {
            if (candidate is not null && _find(candidate) is { IsDirectory: false, Size: > 0 })
            {
                return candidate;
            }
        }
        return null;
    }

    private long? RowOf(string id) =>
        id.Length > Prefix.Length + 1 && id.StartsWith(Prefix + ":", StringComparison.Ordinal)
        && long.TryParse(id.AsSpan(Prefix.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var row) ? row : null;

    // ---------------------------------------------------------------- shapes of WhatsApp's values

    /// <summary>
    /// A media path as WhatsApp stores it ("Media/989…@s.whatsapp.net/a/b/x.jpg", relative to the group's Message folder,
    /// or an absolute path from an older version) → the path inside the app group domain ("Message/Media/…").
    /// </summary>
    public static string? BackupPathOf(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        var path = stored.Replace('\\', '/').Trim();
        var inside = path.IndexOf("/Message/", StringComparison.Ordinal);
        if (inside >= 0)
        {
            path = path[(inside + 1)..];
        }
        else if (!path.StartsWith("Message/", StringComparison.Ordinal))
        {
            path = "Message/" + path.TrimStart('/');
        }

        return path.Split('/').Any(s => s is ".." or ".") ? null : path;
    }

    /// <summary>
    /// What a media message holds. ZMESSAGETYPE as WhatsApp numbers it (1 image, 2 video, 3 audio, 4 contact card,
    /// 5 location, 8 document, 11 GIF, 15 sticker); anything else is told by the file's extension.
    /// </summary>
    public static WhatsAppMediaKind KindOf(long? type, string? file, string? mime, bool hasLocation, bool isVCard)
    {
        var ext = Path.GetExtension(file ?? "").ToLowerInvariant();
        switch (type)
        {
            case 1: return WhatsAppMediaKind.Image;
            case 2: return WhatsAppMediaKind.Video;
            case 3: return ext is ".opus" or ".ogg" ? WhatsAppMediaKind.Voice : WhatsAppMediaKind.Audio;
            case 4: return WhatsAppMediaKind.Contact;
            case 5: return WhatsAppMediaKind.Location;
            case 8: return WhatsAppMediaKind.Document;
            case 11: return WhatsAppMediaKind.Gif;
            case 15: return WhatsAppMediaKind.Sticker;
        }

        if (string.IsNullOrEmpty(file))
        {
            return hasLocation ? WhatsAppMediaKind.Location : isVCard ? WhatsAppMediaKind.Contact : WhatsAppMediaKind.Other;
        }

        return ext switch
        {
            ".jpg" or ".jpeg" or ".png" or ".heic" or ".gif" => WhatsAppMediaKind.Image,
            ".webp" => WhatsAppMediaKind.Sticker,
            ".mp4" or ".mov" or ".m4v" or ".3gp" => WhatsAppMediaKind.Video,
            ".opus" or ".ogg" => WhatsAppMediaKind.Voice,
            ".m4a" or ".mp3" or ".aac" or ".amr" or ".wav" or ".caf" => WhatsAppMediaKind.Audio,
            _ => mime?.StartsWith("image/", StringComparison.Ordinal) == true ? WhatsAppMediaKind.Image
                : mime?.StartsWith("video/", StringComparison.Ordinal) == true ? WhatsAppMediaKind.Video
                : WhatsAppMediaKind.Document,
        };
    }

    /// <summary>
    /// The id of the message a reply quotes. ZWAMEDIAITEM.ZMETADATA is a protocol-buffers message whose field 5 holds the
    /// quoted message's stanza id (ZWAMESSAGE.ZSTANZAID); null when there is none or the blob looks different.
    /// </summary>
    public static string? QuotedId(byte[]? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        var at = 0;
        while (at < metadata.Length)
        {
            if (!Varint(metadata, ref at, out var key))
            {
                return null;
            }

            switch (key & 7)
            {
                case 0:
                    if (!Varint(metadata, ref at, out _))
                    {
                        return null;
                    }

                    break;
                case 1:
                    at += 8;
                    break;
                case 5:
                    at += 4;
                    break;
                case 2:
                    if (!Varint(metadata, ref at, out var length) || length > (ulong)(metadata.Length - at))
                    {
                        return null;
                    }

                    if (key >> 3 == 5)
                    {
                        var id = Encoding.ASCII.GetString(metadata, at, (int)length);
                        return id.Length is > 0 and <= 128 && id.All(char.IsAsciiLetterOrDigit) ? id : null;
                    }

                    at += (int)length;
                    break;
                default:
                    return null;
            }
        }
        return null;
    }

    private static bool Varint(byte[] data, ref int at, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 64 && at < data.Length; shift += 7)
        {
            var b = data[at++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>"989121234567@s.whatsapp.net" → "+989121234567"; null for groups, broadcast lists and hidden ids (@lid).</summary>
    public static string? PhoneOf(string? jid)
    {
        if (jid is null || !(jid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) || jid.EndsWith("@c.us", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var user = jid[..jid.IndexOf('@')];
        var colon = user.IndexOf(':');
        if (colon >= 0)
        {
            user = user[..colon];
        }

        return user.Length >= 5 && user.All(char.IsAsciiDigit) ? "+" + user : null;
    }

    private static string? UserOf(string? jid) => jid is null ? null : jid.IndexOf('@') is var at and > 0 ? jid[..at] : jid;

    private static bool IsVCard(string? value) => value?.Contains("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>For files, ZVCARDSTRING holds the MIME type ("application/pdf"); for contact cards, the vCard.</summary>
    private static string? MimeOf(string? vcardString) =>
        vcardString is { Length: > 2 and < 128 } value && value.Contains('/') && !value.Contains('\n') && !value.Contains(' ') ? value.Trim().ToLowerInvariant() : null;

    private static string? MimeOfName(string? name) => Path.GetExtension(name ?? "").ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".opus" or ".ogg" => "audio/ogg",
        ".m4a" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".aac" => "audio/aac",
        ".pdf" => "application/pdf",
        _ => null,
    };

    private static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? null : name;
    }

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>A text column; some versions keep text in BLOB columns, read as UTF-8.</summary>
    private static string? ReadText(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetValue(i) switch
    {
        byte[] blob => Encoding.UTF8.GetString(blob),
        var value => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static long? Long(SqliteDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetValue(i) switch { long l => l, double d => (long)d, string s when long.TryParse(s, CultureInfo.InvariantCulture, out var v) => v, _ => null };

    private static double? Number(SqliteDataReader reader, int i) =>
        reader.IsDBNull(i) ? null : reader.GetValue(i) switch { long l => l, double d => d, string s when double.TryParse(s, CultureInfo.InvariantCulture, out var v) => v, _ => null };

    /// <summary>Core Data dates: seconds since 1 January 2001 (UTC), often with a fraction.</summary>
    private static DateTimeOffset? DateOf(SqliteDataReader reader, int i) =>
        Number(reader, i) is { } seconds && seconds > 0 && seconds < 4e9 ? Epoch2001.AddSeconds(seconds) : null;

    private sealed record Row(
        long Pk, long? Type, DateTimeOffset? Date, bool FromMe, string? FromJid, string? Text, bool Starred, string? PushName, string? StanzaId,
        string? MemberJid, string? MemberContactName, string? MemberFirstName,
        long? MediaPk, string? LocalPath, string? Title, double? Latitude, double? Longitude, string? VCardName, string? VCardString,
        long? FileSize, double? Duration, byte[]? Metadata, string? XmppThumbPath, string? ThumbnailPath)
    {
        public static Row From(SqliteDataReader r) => new(
            r.GetInt64(0), Long(r, 1), DateOf(r, 2), Long(r, 3) is > 0, ReadText(r, 4), ReadText(r, 5), Long(r, 6) is > 0, ReadText(r, 7), ReadText(r, 8),
            ReadText(r, 9), ReadText(r, 10), ReadText(r, 11),
            Long(r, 12), ReadText(r, 13), ReadText(r, 14), Number(r, 15), Number(r, 16), ReadText(r, 17), ReadText(r, 18),
            Long(r, 19), Number(r, 20), r.IsDBNull(21) || r.GetValue(21) is not byte[] blob ? null : blob, ReadText(r, 22), ReadText(r, 23));
    }

    /// <summary>One table's columns; a column asked for that isn't there reads as NULL and is noted as missing.</summary>
    private sealed class Table
    {
        private readonly string _name;
        private readonly string _alias;
        private readonly ISet<string> _missing;
        private HashSet<string> _columns;

        public Table(SqliteConnection db, string name, string alias, ISet<string> missing)
        {
            _name = name;
            _alias = alias;
            _missing = missing;
            _columns = AddressBookReader.Columns(db, name);
            if (_columns.Count == 0)
            {
                missing.Add(name);
            }
        }

        public bool Exists => _columns.Count > 0;

        public bool Has(string column) => _columns.Contains(column);

        /// <summary>Stops reading the table (it can't be joined); its columns read as NULL.</summary>
        public void Disable() => _columns = [];

        public string Col(string column)
        {
            if (_columns.Contains(column))
            {
                return _alias + "." + column;
            }

            if (Exists)
            {
                _missing.Add($"{_name}.{column}");
            }

            return "NULL";
        }
    }

    /// <summary>
    /// Names for WhatsApp addresses, best first: the iPhone's own contacts (by the number's last ten digits), WhatsApp's
    /// copy of them (ContactsV2.sqlite), the name a group member was saved under, then the name people gave themselves
    /// (ZWAPROFILEPUSHNAME, or the push name on the message), then the number.
    /// </summary>
    private sealed class Names
    {
        private readonly Dictionary<string, string> _phoneBook = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _whatsAppBook = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pushNames = new(StringComparer.OrdinalIgnoreCase);

        public Names(SqliteConnection db, SqliteConnection? contacts, IReadOnlyList<Contact> addressBook, ISet<string> missing)
        {
            foreach (var contact in addressBook)
            {
                foreach (var phone in contact.Phones)
                {
                    if (Key(phone.Value) is { } key)
                    {
                        _phoneBook.TryAdd(key, contact.DisplayName);
                    }
                }
            }

            var push = AddressBookReader.Columns(db, "ZWAPROFILEPUSHNAME");
            if (push.Contains("ZJID") && push.Contains("ZPUSHNAME"))
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT ZJID, ZPUSHNAME FROM ZWAPROFILEPUSHNAME WHERE ZJID IS NOT NULL AND ZPUSHNAME IS NOT NULL";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (Clean(ReadText(reader, 1)) is { } name)
                    {
                        _pushNames.TryAdd(ReadText(reader, 0)!, name);
                    }
                }
            }
            else
            {
                missing.Add("ZWAPROFILEPUSHNAME");
            }

            if (contacts is null)
            {
                return;
            }

            var columns = AddressBookReader.Columns(contacts, "ZWAADDRESSBOOKCONTACT");
            if (!columns.Contains("ZFULLNAME") || !(columns.Contains("ZWHATSAPPID") || columns.Contains("ZPHONENUMBER")))
            {
                missing.Add("ContactsV2.ZWAADDRESSBOOKCONTACT");
                return;
            }

            using (var command = contacts.CreateCommand())
            {
                command.CommandText = $"SELECT ZFULLNAME, {(columns.Contains("ZWHATSAPPID") ? "ZWHATSAPPID" : "NULL")}, {(columns.Contains("ZPHONENUMBER") ? "ZPHONENUMBER" : "NULL")} FROM ZWAADDRESSBOOKCONTACT";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (Clean(ReadText(reader, 0)) is not { } name)
                    {
                        continue;
                    }

                    if (Key(UserOf(ReadText(reader, 1)) ?? ReadText(reader, 2) ?? "") is { } key)
                    {
                        _whatsAppBook.TryAdd(key, name);
                    }
                }
            }
        }

        public string? Find(string? jid, string? savedAs = null, string? pushName = null)
        {
            var key = PhoneOf(jid) is { } phone ? Key(phone) : null;
            return (key is null ? null : _phoneBook.GetValueOrDefault(key) ?? _whatsAppBook.GetValueOrDefault(key))
                   ?? savedAs
                   ?? (jid is null ? null : _pushNames.GetValueOrDefault(jid))
                   ?? pushName
                   ?? PhoneOf(jid)
                   ?? UserOf(jid);
        }

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
