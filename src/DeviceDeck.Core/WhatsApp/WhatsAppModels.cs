namespace DeviceDeck.Core.WhatsApp;

// The bridge shapes of docs/bridge-contract.md, "WhatsApp (round 4)" (camelCase JSON, enums as camelCase strings).

/// <summary>WhatsApp or WhatsApp Business: each keeps its chats in its own app group, so a backup may hold both.</summary>
public enum WhatsAppAccount { WhatsApp, Business }

/// <param name="Found">The backup holds WhatsApp's chat database (either app).</param>
/// <param name="Installed">The backup lists WhatsApp (or WhatsApp Business) among the phone's apps.</param>
/// <param name="Missing">
/// Tables and columns the reader looked for and didn't find ("ZWAMESSAGE.ZSTARRED"): another WhatsApp version stores
/// them differently, so what they hold isn't shown. Empty when everything was there.
/// </param>
public sealed record WhatsAppStatus(string BackupId, bool Found, bool Installed, IReadOnlyList<WhatsAppAccountInfo> Accounts, IReadOnlyList<string> Missing);

/// <param name="Media">Messages with a media file (photo, video, voice note, document, sticker).</param>
/// <param name="MediaInBackup">Of those, the files the backup holds (WhatsApp may have offloaded the rest).</param>
/// <param name="MediaBytes">What those files take.</param>
public sealed record WhatsAppAccountInfo(WhatsAppAccount Account, string Name, int Chats, int Messages, int Media, int MediaInBackup, long MediaBytes);

/// <param name="Id">"w:&lt;row&gt;" for WhatsApp, "b:&lt;row&gt;" for WhatsApp Business; stable within one backup.</param>
/// <param name="Title">The group's subject, else the contact's name, else the number.</param>
/// <param name="Jid">WhatsApp's address of the chat: "989121234567@s.whatsapp.net", "…@g.us" for a group.</param>
/// <param name="Phone">"+989121234567" for a one-to-one chat with a phone number.</param>
/// <param name="MessageCount">Everything the chat holds, group events included (what the thread pages over).</param>
/// <param name="Preview">The newest message's text, shortened; null when it was media only.</param>
/// <param name="PreviewMedia">What the newest message was when it carried media.</param>
public sealed record WhatsAppChat(
    string Id,
    WhatsAppAccount Account,
    string Title,
    string? Jid,
    string? Phone,
    bool IsGroup,
    int MessageCount,
    int MediaCount,
    DateTimeOffset? LastMessageAt,
    string? Preview,
    WhatsAppMediaKind? PreviewMedia,
    bool Archived);

public enum WhatsAppMessageKind
{
    Text,
    /// <summary>A photo, video, voice note, document, sticker, location or contact card; see <see cref="WhatsAppMessage.Media"/>.</summary>
    Media,
    /// <summary>A group event (created, renamed, someone joined or left); <see cref="WhatsAppMessage.Text"/> when WhatsApp kept words for it.</summary>
    Event,
    /// <summary>"This message was deleted".</summary>
    Deleted,
    /// <summary>Something this reader doesn't know how to show (a poll, a call note).</summary>
    Other,
}

/// <param name="SenderName">In a group: the member's name from the address book, WhatsApp's contacts or their own push name; else null.</param>
/// <param name="SenderJid">In a group: the member's address. Null for messages from the phone's owner.</param>
/// <param name="Text">The message, or a photo's caption.</param>
/// <param name="Quote">The message this one replies to, when WhatsApp kept the link.</param>
public sealed record WhatsAppMessage(
    string Id,
    WhatsAppMessageKind Kind,
    DateTimeOffset? Date,
    bool FromMe,
    string? SenderJid,
    string? SenderName,
    string? Text,
    bool Starred,
    WhatsAppQuote? Quote,
    WhatsAppMedia? Media);

/// <param name="MessageId">The quoted message's id in the same chat, when it is still there.</param>
/// <param name="Text">Its text, shortened; null for a media message (see <see cref="MediaKind"/>).</param>
public sealed record WhatsAppQuote(string? MessageId, bool FromMe, string? SenderName, string? Text, WhatsAppMediaKind? MediaKind);

public enum WhatsAppMediaKind { Image, Video, Audio, Voice, Document, Sticker, Gif, Location, Contact, Other }

/// <param name="Id">"w:&lt;media row&gt;" or "b:&lt;media row&gt;": pass it to whatsapp.thumbnail and whatsapp.media.</param>
/// <param name="Name">The file's name (a document's own title), or the contact's name for a contact card.</param>
/// <param name="InBackup">The file is in the backup. False when WhatsApp offloaded it or never downloaded it.</param>
/// <param name="HasThumbnail">A picture to show for it: WhatsApp's own thumbnail, or the photo itself.</param>
public sealed record WhatsAppMedia(
    string Id,
    WhatsAppMediaKind Kind,
    string Name,
    string? MimeType,
    long Size,
    double? DurationSeconds,
    bool InBackup,
    bool HasThumbnail,
    double? Latitude,
    double? Longitude);

/// <summary>whatsapp.media: one file's bytes, for playing a voice note in the page.</summary>
public sealed record WhatsAppMediaFile(string MimeType, long Size, byte[] Bytes);

// ---------------------------------------------------------------- Android

/// <summary>What DeviceDeck can see of WhatsApp on an Android phone (Android/media/com.whatsapp/WhatsApp).</summary>
/// <param name="Apps">WhatsApp and WhatsApp Business, each when its folder is on the phone.</param>
public sealed record WhatsAppAndroidInfo(string DeviceId, IReadOnlyList<WhatsAppAndroidApp> Apps);

/// <param name="Folder">On the phone: "/Android/media/com.whatsapp/WhatsApp", or "/WhatsApp" for versions before 2021.</param>
/// <param name="HasFolder">The folder is there. False for an app that is installed but hasn't saved anything yet.</param>
/// <param name="Installed">The app is on the phone (from its app list); null when the list couldn't be read.</param>
/// <param name="MediaFiles">Files under Media; null when the phone couldn't say.</param>
/// <param name="ChatBackups">WhatsApp's own encrypted chat backups in Databases (msgstore.db.crypt14/15), newest first.</param>
public sealed record WhatsAppAndroidApp(
    string Package,
    WhatsAppAccount Account,
    string Name,
    string Folder,
    bool HasFolder,
    bool? Installed,
    int? MediaFiles,
    long? MediaBytes,
    IReadOnlyList<WhatsAppAndroidBackupFile> ChatBackups);

public sealed record WhatsAppAndroidBackupFile(string Name, long Size, DateTimeOffset? Modified);

/// <summary>whatsapp.backupAndroid: the transfer job, and the dated folder it copies into.</summary>
public sealed record WhatsAppAndroidBackupResult(string JobId, string Folder);
