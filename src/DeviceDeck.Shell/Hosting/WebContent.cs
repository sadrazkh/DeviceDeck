namespace DeviceDeck.Shell.Hosting;

/// <summary>
/// Where the page comes from. DEVICEDECK_DEV=1: the Vite dev server on http://localhost:5173 (or DEVICEDECK_DEV_URL).
/// Otherwise a folder mapped to https://app.devicedeck/: DEVICEDECK_WEB_ROOT, then "web" next to the exe
/// (published layout), then the repo's web/dist, then the Shell's own placeholder page.
/// DEVICEDECK_PLACEHOLDER=1 forces the placeholder.
/// </summary>
internal sealed record WebContent(Uri StartUri, string? Folder, string Description)
{
    public const string AppHost = "app.devicedeck";
    public const string DevServer = "http://localhost:5173/";

    public bool IsTrustedSource(Uri source) =>
        Uri.Compare(source, StartUri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public static WebContent Resolve(bool devMode)
    {
        if (devMode)
        {
            // DEVICEDECK_DEV_URL: another dev server's address (parallel worktrees each run their own).
            var server = Environment.GetEnvironmentVariable("DEVICEDECK_DEV_URL") is { Length: > 0 } url ? url : DevServer;
            return new WebContent(new Uri(server), null, "Vite dev server");
        }

        var start = new Uri($"https://{AppHost}/index.html");
        var exeFolder = AppContext.BaseDirectory;
        if (Environment.GetEnvironmentVariable("DEVICEDECK_PLACEHOLDER") != "1")
        {
            foreach (var candidate in Candidates(exeFolder))
            {
                if (File.Exists(Path.Combine(candidate, "index.html")))
                {
                    return new WebContent(start, candidate, candidate);
                }
            }
        }

        var placeholder = Path.Combine(exeFolder, "Placeholder");
        return new WebContent(start, placeholder, "placeholder page");
    }

    private static IEnumerable<string> Candidates(string exeFolder)
    {
        if (Environment.GetEnvironmentVariable("DEVICEDECK_WEB_ROOT") is { Length: > 0 } configured)
        {
            yield return configured;
        }

        yield return Path.Combine(exeFolder, "web");

        // Running from the build output inside the repo: find <repo>/web/dist.
        for (var folder = new DirectoryInfo(exeFolder); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "DeviceDeck.slnx")))
            {
                yield return Path.Combine(folder.FullName, "web", "dist");
                yield break;
            }
        }
    }
}
