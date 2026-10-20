namespace DeviceDeck.Core.Toolbox;

// What the toolbox needs from Windows. The Shell supplies these (WIC, Media Foundation, WebView2);
// tests and the plain host use the managed fallbacks or report not_supported.

/// <summary>HEIC / HEIF photos on the PC to JPEG.</summary>
public interface IHeicConverter
{
    /// <summary>False when this PC can't decode HEIC (the HEIF Image Extensions from the Microsoft Store are missing).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    /// <summary>
    /// Writes <paramref name="destination"/> as a JPEG at <paramref name="quality"/> (1–100), upright, keeping the
    /// date taken, camera and location where the codec allows. Throws <see cref="IOException"/> or
    /// <see cref="InvalidDataException"/> for a file it can't read.
    /// </summary>
    Task ConvertAsync(string source, string destination, int quality, CancellationToken ct);
}

/// <summary>Cuts a piece out of an audio file on the PC.</summary>
public interface IAudioTrimmer
{
    /// <param name="extension">With the dot, any case (".mp3").</param>
    bool CanTrim(string extension);

    /// <summary>
    /// Writes <paramref name="start"/>..<paramref name="end"/> of <paramref name="source"/> to
    /// <paramref name="destinationWithoutExtension"/> plus the extension the trimmer chose, and returns that path.
    /// </summary>
    Task<string> TrimAsync(string source, TimeSpan start, TimeSpan end, string destinationWithoutExtension, CancellationToken ct);
}

/// <summary>HTML to a PDF file (the Shell prints it with WebView2).</summary>
public interface IPdfPrinter
{
    Task PrintAsync(string html, string pdfPath, CancellationToken ct);
}
