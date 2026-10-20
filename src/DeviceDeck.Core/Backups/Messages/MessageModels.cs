namespace DeviceDeck.Core.Backups.Messages;

/// <param name="Id">Stable within one backup (the chat's row id).</param>
/// <param name="Title">The group's name, else the members' names or numbers.</param>
/// <param name="Service">"iMessage", "SMS", "RCS" as the phone recorded it.</param>
/// <param name="Preview">The newest message's text, shortened.</param>
public sealed record Conversation(
    string Id,
    string Title,
    IReadOnlyList<ConversationMember> Members,
    bool IsGroup,
    string Service,
    int MessageCount,
    int AttachmentCount,
    DateTimeOffset? LastMessageAt,
    string? Preview);

/// <param name="Handle">Phone number or email address.</param>
/// <param name="Name">The contact's name from the same backup's address book, when it has one.</param>
public sealed record ConversationMember(string Handle, string? Name);

public enum MessageKind
{
    Text,
    /// <summary>A group event (renamed, someone joined or left); <see cref="Message.Text"/> describes it when the phone did.</summary>
    Event,
}

/// <param name="Handle">Who sent it; null for messages from the phone's owner.</param>
public sealed record Message(
    string Id,
    MessageKind Kind,
    DateTimeOffset? Date,
    bool FromMe,
    string? Handle,
    string? SenderName,
    string? Text,
    string Service,
    IReadOnlyList<MessageAttachment> Attachments);

/// <param name="InBackup">The file is in the backup (attachments the phone offloaded to iCloud aren't).</param>
public sealed record MessageAttachment(string Id, string Name, string? MimeType, long Size, bool InBackup);
