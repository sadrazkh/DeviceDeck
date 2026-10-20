using System.Globalization;

namespace DeviceDeck.Core.Backups.Messages;

/// <summary>
/// One conversation as a self-contained HTML page: bubbles, day separators, sender names in groups, and attachments
/// from a folder next to the page (pictures and videos shown, other files linked). The same page is printed to PDF.
/// </summary>
public static class ConversationHtml
{
    /// <param name="attachmentHref">Relative link to an attachment's exported file, or null when it isn't available.</param>
    /// <param name="previewHref">A picture the page can show for it (a JPEG copy of a HEIC photo), else the file itself.</param>
    /// <param name="forPrint">Load every picture at once: printing doesn't wait for lazy ones.</param>
    public static void Write(TextWriter w, Conversation conversation, IReadOnlyList<Message> messages, string source,
        Func<MessageAttachment, string?> attachmentHref, Func<MessageAttachment, string?>? previewHref = null, TimeZoneInfo? zone = null, bool forPrint = false)
    {
        zone ??= TimeZoneInfo.Local;
        var culture = CultureInfo.InvariantCulture;
        var first = messages.FirstOrDefault(m => m.Date is not null)?.Date;
        var last = messages.LastOrDefault(m => m.Date is not null)?.Date;

        w.Write("<!doctype html>\n<html lang=\"und\">\n<head>\n<meta charset=\"utf-8\">\n");
        w.Write($"<title>{H(conversation.Title)}</title>\n");
        w.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        w.Write("<meta name=\"generator\" content=\"DeviceDeck\">\n");
        w.Write(Style);
        w.Write("</head>\n<body>\n<header>\n");
        w.Write($"<h1 dir=\"auto\">{H(conversation.Title)}</h1>\n");
        if (conversation.Members.Count > 0)
        {
            w.Write($"<p class=\"members\" dir=\"auto\">{H(string.Join(", ", conversation.Members.Select(m => m.Name is null ? m.Handle : $"{m.Name} ({m.Handle})")))}</p>\n");
        }

        var range = first is null ? "" : $" · {Local(first.Value, zone).ToString("d MMM yyyy", culture)} – {Local(last!.Value, zone).ToString("d MMM yyyy", culture)}";
        w.Write($"<p class=\"meta\">{messages.Count.ToString("N0", culture)} messages · {H(conversation.Service)}{H(range)}</p>\n");
        w.Write($"<p class=\"meta\">{H(source)}</p>\n</header>\n<main>\n");

        DateOnly? day = null;
        string? previousSender = null;
        foreach (var message in messages)
        {
            var local = message.Date is { } date ? Local(date, zone) : (DateTime?)null;
            if (local is { } l && DateOnly.FromDateTime(l) != day)
            {
                day = DateOnly.FromDateTime(l);
                w.Write($"<div class=\"day\">{H(l.ToString("dddd, d MMMM yyyy", culture))}</div>\n");
                previousSender = null;
            }

            if (message.Kind == MessageKind.Event)
            {
                w.Write($"<div class=\"event\" dir=\"auto\">{H(message.Text ?? "Group changed")}</div>\n");
                previousSender = null;
                continue;
            }

            var sender = message.FromMe ? "me" : message.Handle ?? "?";
            w.Write($"<div class=\"msg {(message.FromMe ? "out" : "in")}\">");
            if (!message.FromMe && conversation.IsGroup && sender != previousSender)
            {
                w.Write($"<div class=\"from\" dir=\"auto\">{H(message.SenderName ?? message.Handle ?? "")}</div>");
            }

            previousSender = sender;
            w.Write("<div class=\"bubble\">");
            foreach (var attachment in message.Attachments)
            {
                WriteAttachment(w, attachment, attachmentHref(attachment), previewHref?.Invoke(attachment), forPrint);
            }

            if (message.Text is { } text)
            {
                w.Write($"<div class=\"text\" dir=\"auto\">{H(text).Replace("\n", "<br>", StringComparison.Ordinal)}</div>");
            }

            w.Write("</div>");
            if (local is { } time)
            {
                w.Write($"<div class=\"time\">{H(time.ToString("HH:mm", culture))}</div>");
            }

            w.Write("</div>\n");
        }
        w.Write("</main>\n</body>\n</html>\n");
    }

    private static void WriteAttachment(TextWriter w, MessageAttachment attachment, string? href, string? preview, bool forPrint)
    {
        var size = FormatSize(attachment.Size);
        if (href is null)
        {
            w.Write($"<div class=\"file missing\" dir=\"auto\">📎 {H(attachment.Name)}{(size is null ? "" : " · " + size)} — not in this backup</div>");
            return;
        }
        var kind = Kind(attachment);
        var shown = preview ?? href;
        switch (kind)
        {
            case "image" when preview is not null || IsBrowserImage(attachment):
                w.Write($"<a href=\"{A(href)}\"><img src=\"{A(shown)}\" alt=\"{H(attachment.Name)}\"{(forPrint ? "" : " loading=\"lazy\"")}></a>");
                return;
            case "video":
                w.Write($"<video controls preload=\"metadata\" src=\"{A(href)}\"></video>");
                break;
            case "audio":
                w.Write($"<audio controls preload=\"none\" src=\"{A(href)}\"></audio>");
                break;
        }
        w.Write($"<a class=\"file\" dir=\"auto\" href=\"{A(href)}\">📎 {H(attachment.Name)}{(size is null ? "" : " · " + size)}</a>");
    }

    public static string Kind(MessageAttachment attachment)
    {
        var mime = attachment.MimeType ?? "";
        var ext = Path.GetExtension(attachment.Name).ToLowerInvariant();
        if (mime.StartsWith("image/", StringComparison.Ordinal) || ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".heic" or ".heif" or ".webp")
        {
            return "image";
        }

        if (mime.StartsWith("video/", StringComparison.Ordinal) || ext is ".mov" or ".mp4" or ".m4v")
        {
            return "video";
        }

        if (mime.StartsWith("audio/", StringComparison.Ordinal) || ext is ".m4a" or ".mp3" or ".caf" or ".amr" or ".aac")
        {
            return "audio";
        }

        return "file";
    }

    /// <summary>Pictures a browser shows as they are; HEIC needs a JPEG copy.</summary>
    public static bool IsBrowserImage(MessageAttachment attachment) =>
        Path.GetExtension(attachment.Name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";

    private static DateTime Local(DateTimeOffset date, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(date, zone).DateTime;

    private static string? FormatSize(long bytes) => bytes switch
    {
        <= 0 => null,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace(',', '.'),
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB".Replace(',', '.'),
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB".Replace(',', '.'),
    };

    /// <summary>Escapes markup only; the page is UTF-8, so Persian and emoji stay as they are.</summary>
    private static string H(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    /// <summary>A relative link: each path segment percent-encoded, so names with spaces or '#' work.</summary>
    private static string A(string href) => H(string.Join('/', href.Split('/').Select(Uri.EscapeDataString)));

    private const string Style = """
        <style>
        :root { color-scheme: light; --out: #0b84ff; --in: #e9e9eb; --ink: #1c1c1e; --muted: #6e6e73; }
        * { box-sizing: border-box; }
        body { margin: 0; background: #fff; color: var(--ink); font: 15px/1.4 "Segoe UI", "Vazirmatn", Tahoma, system-ui, sans-serif, "Segoe UI Emoji"; }
        header { padding: 24px 24px 12px; border-bottom: 1px solid #ddd; }
        h1 { margin: 0 0 4px; font-size: 22px; font-weight: 600; }
        .members, .meta { margin: 2px 0; color: var(--muted); font-size: 13px; }
        main { padding: 12px 24px 32px; max-width: 820px; margin: 0 auto; }
        .day { text-align: center; color: var(--muted); font-size: 12px; margin: 20px 0 8px; break-after: avoid; }
        .event { text-align: center; color: var(--muted); font-size: 12px; margin: 8px 0; }
        .msg { display: flex; flex-direction: column; margin: 2px 0; break-inside: avoid; }
        .msg.out { align-items: flex-end; }
        .msg.in { align-items: flex-start; }
        .from { font-size: 12px; color: var(--muted); margin: 8px 12px 2px; }
        .bubble { max-width: 75%; padding: 7px 12px; border-radius: 18px; background: var(--in); overflow-wrap: anywhere; }
        .out .bubble { background: var(--out); color: #fff; }
        .bubble img, .bubble video { display: block; max-width: 100%; max-height: 420px; border-radius: 12px; margin: 4px 0; }
        .bubble audio { display: block; max-width: 100%; margin: 4px 0; }
        .file { display: block; color: inherit; font-size: 13px; margin: 4px 0; text-decoration: underline; }
        .file.missing { text-decoration: none; opacity: .7; }
        .time { font-size: 11px; color: var(--muted); margin: 1px 12px 4px; }
        @media print { body { font-size: 12px; } main { max-width: none; } .bubble img { max-height: 300px; } }
        </style>

        """;
}
