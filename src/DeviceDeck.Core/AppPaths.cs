namespace DeviceDeck.Core;

/// <summary>Where DeviceDeck keeps its own data: %LocalAppData%\DeviceDeck unless a root is given (tests).</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "DeviceDeck");
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string WindowFile => Path.Combine(Root, "window.json");
    public string TransfersDatabase => Path.Combine(Root, "transfers.db");
    public string ThumbnailCache => Path.Combine(Root, "cache", "thumbs");
    public string ScreenshotFolder => Path.Combine(Root, "cache", "shots");
    public string WebViewData => Path.Combine(Root, "WebView2");
    public string Logs => Path.Combine(Root, "logs");

    public string Ensure(string folder)
    {
        Directory.CreateDirectory(folder);
        return folder;
    }
}
