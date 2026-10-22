using System.Diagnostics;
using DeviceDeck.Bridge.Methods;

namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// The one way the app opens anything outside itself: web pages (http, https), mail (mailto) and a product page in the
/// Microsoft Store (ms-windows-store://pdp/?ProductId=…, for Apple Devices). Anything else is refused, so a link in the page
/// can never start a program or open a file.
/// </summary>
internal static class ShellLinks
{
    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme is "https" or "http" or "mailto" || IsStoreProductPage(uri));

    /// <summary>ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K: a product page, with a plain product id and nothing else.</summary>
    private static bool IsStoreProductPage(Uri uri)
    {
        if (uri.Scheme != "ms-windows-store" || !uri.Host.Equals("pdp", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath is not ("/" or ""))
        {
            return false;
        }

        const string prefix = "?ProductId=";
        string query = uri.Query;
        return query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && query[prefix.Length..] is { Length: >= 9 and <= 16 } id
            && id.All(char.IsAsciiLetterOrDigit);
    }

    /// <summary>Opens an allowed link with its default app; false (and nothing opened) for any other.</summary>
    public static bool Open(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && Open(parsed);

    public static bool Open(Uri uri)
    {
        if (!IsAllowed(uri))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return true;
    }
}

/// <summary>drivers.openLink through <see cref="ShellLinks"/>.</summary>
internal sealed class ShellLinkOpener : ILinkOpener
{
    public void Open(Uri uri)
    {
        if (!ShellLinks.Open(uri))
        {
            throw new ArgumentException($"DeviceDeck doesn't open links like {uri.Scheme}:.");
        }
    }
}
