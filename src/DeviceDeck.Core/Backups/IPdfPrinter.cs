namespace DeviceDeck.Core.Backups;

/// <summary>
/// Turns an HTML file into a PDF (the Shell prints it with WebView2, which shapes Persian text and embeds pictures).
/// Implemented by the host's shell services; without it, messages export to HTML only.
/// </summary>
public interface IPdfPrinter
{
    /// <summary>Prints <paramref name="htmlPath"/> (relative links resolve next to it) to <paramref name="pdfPath"/>.</summary>
    Task PrintToPdfAsync(string htmlPath, string pdfPath, CancellationToken ct);
}
