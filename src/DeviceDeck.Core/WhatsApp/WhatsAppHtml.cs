using System.Globalization;

namespace DeviceDeck.Core.WhatsApp;

/// <summary>
/// One WhatsApp chat as a self-contained HTML page in WhatsApp's own look: bubbles, day separators, the sender in groups,
/// replies with what they quote, and media from the "&lt;chat&gt; files" folder next to the page (pictures and videos
/// shown, voice notes playable, other files linked). The same page is printed to PDF.
/// </summary>
public static class WhatsAppHtml
{
    /// <param name="mediaHref">Relative link to a media file's exported copy, or null when the backup doesn't hold it.</param>
    /// <param name="forPrint">Load every picture at once: printing doesn't wait for lazy ones.</param>
    public static void Write(TextWriter w, WhatsAppChat chat, IReadOnlyList<WhatsAppMessage> messages, string source,
        Func<WhatsAppMedia, string?> mediaHref, TimeZoneInfo? zone = null, bool forPrint = false)
    {
        zone ??= TimeZoneInfo.Local;
        var culture = CultureInfo.InvariantCulture;
        var first = messages.FirstOrDefault(m => m.Date is not null)?.Date;
        var last = messages.LastOrDefault(m => m.Date is not null)?.Date;
        var app = chat.Account == WhatsAppAccount.Business ? "WhatsApp Business" : "WhatsApp";

        w.Write("<!doctype html>\n<html lang=\"und\">\n<head>\n<meta charset=\"utf-8\">\n");
        w.Write($"<title>{H(chat.Title)}</title>\n");
        w.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        w.Write("<meta name=\"generator\" content=\"DeviceDeck\">\n");
        w.Write(Style);
        w.Write("</head>\n<body>\n<header>\n");
        w.Write($"<h1 dir=\"auto\">{H(chat.Title)}</h1>\n");
        if (chat.Phone is { } phone && phone != chat.Title)
        {
            w.Write($"<p class=\"meta\" dir=\"ltr\">{H(phone)}</p>\n");
        }

        var range = first is null ? "" : $" · {Local(first.Value, zone).ToString("d MMM yyyy", culture)} – {Local(last!.Value, zone).ToString("d MMM yyyy", culture)}";
        w.Write($"<p class=\"meta\">{messages.Count.ToString("N0", culture)} messages · {H(app)}{(chat.IsGroup ? " group" : "")}{H(range)}</p>\n");
        w.Write($"<p class=\"meta\">{H(source)}</p>\n</header>\n<main>\n");

        DateOnly? day = null;
        string? previousSender = null;
        foreach (var message in messages)
        {
            var local = message.Date is { } date ? Local(date, zone) : (DateTime?)null;
            if (local is { } l && DateOnly.FromDateTime(l) != day)
            {
                day = DateOnly.FromDateTime(l);
                w.Write($"<div class=\"day\"><span>{H(l.ToString("dddd, d MMMM yyyy", culture))}</span></div>\n");
                previousSender = null;
            }

            if (message.Kind == WhatsAppMessageKind.Event)
            {
                w.Write($"<div class=\"event\" dir=\"auto\"><span>{H(message.Text ?? "The group changed")}</span></div>\n");
                previousSender = null;
                continue;
            }

            var sender = message.FromMe ? "me" : message.SenderJid ?? "?";
            w.Write($"<div class=\"msg {(message.FromMe ? "out" : "in")}\"><div class=\"bubble\">");
            if (!message.FromMe && chat.IsGroup && sender != previousSender)
            {
                w.Write($"<div class=\"from\" dir=\"auto\">{H(message.SenderName ?? message.SenderJid ?? "")}</div>");
            }

            previousSender = sender;
            if (message.Quote is { } quote)
            {
                var who = quote.FromMe ? "You" : quote.SenderName ?? chat.Title;
                var what = quote.Text ?? (quote.MediaKind is { } kind ? Label(kind) : "Message");
                w.Write($"<div class=\"quote\"><div class=\"quote-from\" dir=\"auto\">{H(who)}</div><div dir=\"auto\">{H(what)}</div></div>");
            }

            switch (message.Kind)
            {
                case WhatsAppMessageKind.Deleted:
                    w.Write("<div class=\"note\">🚫 This message was deleted</div>");
                    break;
                case WhatsAppMessageKind.Other:
                    w.Write("<div class=\"note\">A message DeviceDeck can't show (a poll, a call, or something newer)</div>");
                    break;
            }

            if (message.Media is { } media)
            {
                WriteMedia(w, media, mediaHref(media), forPrint);
            }

            if (message.Text is { } text)
            {
                w.Write($"<div class=\"text\" dir=\"auto\">{H(text).Replace("\n", "<br>", StringComparison.Ordinal)}</div>");
            }

            if (local is { } time)
            {
                w.Write($"<div class=\"time\">{(message.Starred ? "★ " : "")}{H(time.ToString("HH:mm", culture))}</div>");
            }

            w.Write("</div></div>\n");
        }
        w.Write("</main>\n</body>\n</html>\n");
    }

    private static void WriteMedia(TextWriter w, WhatsAppMedia media, string? href, bool forPrint)
    {
        var size = FormatSize(media.Size);
        switch (media.Kind)
        {
            case WhatsAppMediaKind.Location when media.Latitude is { } lat && media.Longitude is { } lon:
                var at = $"{lat.ToString("0.#####", CultureInfo.InvariantCulture)},{lon.ToString("0.#####", CultureInfo.InvariantCulture)}";
                var map = $"https://www.openstreetmap.org/?mlat={lat.ToString(CultureInfo.InvariantCulture)}&mlon={lon.ToString(CultureInfo.InvariantCulture)}#map=16/{lat.ToString(CultureInfo.InvariantCulture)}/{lon.ToString(CultureInfo.InvariantCulture)}";
                w.Write($"<a class=\"file\" href=\"{H(map)}\">📍 {H(media.Name == "Location" ? at : $"{media.Name} ({at})")}</a>");
                return;
            case WhatsAppMediaKind.Contact:
                w.Write($"<div class=\"file\" dir=\"auto\">👤 {H(media.Name)}</div>");
                return;
        }

        if (href is null)
        {
            w.Write($"<div class=\"file missing\" dir=\"auto\">{Icon(media.Kind)} {H(media.Name)}{(size is null ? "" : " · " + size)} — not in this backup</div>");
            return;
        }

        var link = A(href);
        switch (media.Kind)
        {
            case WhatsAppMediaKind.Image or WhatsAppMediaKind.Sticker when IsBrowserImage(media.Name):
                w.Write($"<a href=\"{link}\"><img class=\"{(media.Kind == WhatsAppMediaKind.Sticker ? "sticker" : "photo")}\" src=\"{link}\" alt=\"{H(media.Name)}\"{(forPrint ? "" : " loading=\"lazy\"")}></a>");
                return;
            case WhatsAppMediaKind.Video or WhatsAppMediaKind.Gif:
                w.Write(media.Kind == WhatsAppMediaKind.Gif
                    ? $"<video autoplay loop muted playsinline src=\"{link}\"></video>"
                    : $"<video controls preload=\"metadata\" src=\"{link}\"></video>");
                break;
            case WhatsAppMediaKind.Voice or WhatsAppMediaKind.Audio:
                w.Write($"<audio controls preload=\"none\" src=\"{link}\"></audio>");
                break;
        }
        var duration = media.DurationSeconds is { } seconds ? $" · {(int)seconds / 60}:{(int)seconds % 60:00}" : "";
        w.Write($"<a class=\"file\" dir=\"auto\" href=\"{link}\">{Icon(media.Kind)} {H(media.Name)}{duration}{(size is null ? "" : " · " + size)}</a>");
    }

    /// <summary>What a quoted media message is called in a reply.</summary>
    private static string Label(WhatsAppMediaKind kind) => kind switch
    {
        WhatsAppMediaKind.Image => "📷 Photo",
        WhatsAppMediaKind.Video => "🎥 Video",
        WhatsAppMediaKind.Voice => "🎤 Voice message",
        WhatsAppMediaKind.Audio => "🎵 Audio",
        WhatsAppMediaKind.Document => "📄 Document",
        WhatsAppMediaKind.Sticker => "Sticker",
        WhatsAppMediaKind.Gif => "GIF",
        WhatsAppMediaKind.Location => "📍 Location",
        WhatsAppMediaKind.Contact => "👤 Contact",
        _ => "Attachment",
    };

    private static string Icon(WhatsAppMediaKind kind) => kind switch
    {
        WhatsAppMediaKind.Image or WhatsAppMediaKind.Sticker => "📷",
        WhatsAppMediaKind.Video or WhatsAppMediaKind.Gif => "🎥",
        WhatsAppMediaKind.Voice => "🎤",
        WhatsAppMediaKind.Audio => "🎵",
        _ => "📎",
    };

    private static bool IsBrowserImage(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp";

    private static DateTime Local(DateTimeOffset date, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(date, zone).DateTime;

    private static string? FormatSize(long bytes) => bytes switch
    {
        <= 0 => null,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / 1024.0 / 1024).ToString("0.#", CultureInfo.InvariantCulture)} MB",
        _ => $"{(bytes / 1024.0 / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture)} GB",
    };

    /// <summary>Escapes markup only; the page is UTF-8, so Persian and emoji stay as they are.</summary>
    private static string H(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#39;", StringComparison.Ordinal);

    /// <summary>A relative link: each path segment percent-encoded, so names with spaces or '#' work.</summary>
    private static string A(string href) => H(string.Join('/', href.Split('/').Select(Uri.EscapeDataString)));

    private const string Style = """
        <style>
        :root { color-scheme: light; --paper: #efeae2; --out: #d9fdd3; --in: #ffffff; --ink: #111b21; --muted: #667781; --accent: #008069; }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--paper); color: var(--ink); font: 15px/1.4 "Segoe UI", "Vazirmatn", Tahoma, system-ui, sans-serif, "Segoe UI Emoji"; }
        header { padding: 24px 24px 12px; background: #fff; border-bottom: 1px solid #d1d7db; }
        h1 { margin: 0 0 4px; font-size: 22px; font-weight: 600; }
        .meta { margin: 2px 0; color: var(--muted); font-size: 13px; }
        main { padding: 12px 24px 32px; max-width: 860px; margin: 0 auto; }
        .day, .event { text-align: center; margin: 14px 0 8px; break-after: avoid; }
        .day span, .event span { display: inline-block; padding: 4px 10px; border-radius: 8px; background: #fff; color: var(--muted); font-size: 12px; box-shadow: 0 1px .5px rgba(11,20,26,.13); }
        .event span { background: #fff8c5; color: #54656f; }
        .msg { display: flex; margin: 2px 0; break-inside: avoid; }
        .msg.out { justify-content: flex-end; }
        .bubble { max-width: 75%; padding: 6px 9px 4px; border-radius: 8px; background: var(--in); box-shadow: 0 1px .5px rgba(11,20,26,.13); overflow-wrap: anywhere; }
        .out .bubble { background: var(--out); }
        .from { font-size: 13px; font-weight: 600; color: var(--accent); margin-bottom: 2px; }
        .quote { border-inline-start: 4px solid var(--accent); background: rgba(11,20,26,.05); border-radius: 6px; padding: 4px 8px; margin: 2px 0 4px; font-size: 13px; color: #3b4a54; }
        .quote-from { font-weight: 600; color: var(--accent); }
        .bubble img.photo, .bubble video { display: block; max-width: 100%; max-height: 420px; border-radius: 6px; margin: 2px 0; }
        .bubble img.sticker { display: block; width: 160px; height: auto; margin: 2px 0; }
        .bubble audio { display: block; max-width: 100%; margin: 4px 0; }
        .file { display: block; color: inherit; font-size: 13px; margin: 4px 0; text-decoration: underline; }
        .file.missing, div.file { text-decoration: none; }
        .file.missing { opacity: .7; }
        .note { font-style: italic; color: var(--muted); }
        .time { text-align: end; font-size: 11px; color: var(--muted); margin-top: 2px; }
        @media print { body { font-size: 12px; background: #fff; } main { max-width: none; } .bubble { box-shadow: none; border: 1px solid #d1d7db; } .bubble img.photo { max-height: 300px; } }
        </style>

        """;
}
