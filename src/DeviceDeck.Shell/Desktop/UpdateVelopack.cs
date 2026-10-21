using System.Diagnostics;
using System.Runtime.InteropServices;
using DeviceDeck.Bridge;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Shell.Hosting;
using Velopack;
using Velopack.Sources;

namespace DeviceDeck.Shell.Desktop;

/// <summary>Velopack with GitHub Releases as <see cref="IUpdateEngine"/>. Only a copy installed by the Setup has a manager.</summary>
internal sealed class VelopackUpdateEngine : IUpdateEngine
{
    private readonly UpdateManager? _manager;

    public VelopackUpdateEngine(bool prerelease)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleaseLinks.Repository, null, prerelease));
            if (manager.IsInstalled)
            {
                _manager = manager;
                Pending = manager.UpdatePendingRestart is { } asset ? new UpdateOffer(asset.Version.ToString(), asset.NotesMarkdown, asset) : null;
            }
        }
        catch (Exception ex)
        {
            ShellLog.Error("The update manager could not start.", ex);
        }
    }

    public bool IsInstalled => _manager is not null;

    public UpdateOffer? Pending { get; }

    public async Task<UpdateOffer?> CheckAsync(CancellationToken ct)
    {
        var update = await Manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        return update is null ? null : new UpdateOffer(update.TargetFullRelease.Version.ToString(), update.TargetFullRelease.NotesMarkdown, update);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct) =>
        Manager.DownloadUpdatesAsync((UpdateInfo)offer.Handle!, progress, ct);

    public void ApplyOnExit(UpdateOffer offer)
    {
        var asset = offer.Handle switch
        {
            UpdateInfo update => update.TargetFullRelease,
            VelopackAsset pending => pending,
            _ => throw new InvalidOperationException("That update wasn't downloaded by Velopack."),
        };
        Manager.WaitExitThenApplyUpdates(asset, silent: false, restart: true);
    }

    private UpdateManager Manager => _manager ?? throw new InvalidOperationException("This copy of DeviceDeck wasn't installed by the Setup.");
}

/// <summary>Builds the window's <see cref="UpdateService"/>.</summary>
internal static class UpdateSetup
{
    private const string BundledNotesResource = "DeviceDeck.ReleaseNotes.md";

    /// <summary>
    /// Call before the window makes its WebView2 folder: that folder already being there means DeviceDeck ran on this PC
    /// before. Environment: DEVICEDECK_UPDATE_FEED=off (no automatic checks) or a URL in place of GitHub's API for the
    /// repository (a test server; checks then run in a dev build too); DEVICEDECK_WHATSNEW=1 (show "What's new" now) or 0.
    /// A dev build (DEVICEDECK_DEV) checks only when asked and never opens "What's new" by itself.
    /// </summary>
    public static UpdateService Create(HostServices host, INotificationService notifications, Action quit)
    {
        var version = HostServices.Version;
        var ranBefore = Directory.Exists(host.Paths.WebViewData);
        var feedSetting = Environment.GetEnvironmentVariable("DEVICEDECK_UPDATE_FEED");
        var custom = Uri.TryCreate(feedSetting, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null;
        var automatic = feedSetting == "off" ? false : custom is not null || !host.DevMode;
        bool? whatsNew = Environment.GetEnvironmentVariable("DEVICEDECK_WHATSNEW") switch
        {
            "1" => true,
            "0" => false,
            _ => host.DevMode ? false : null,
        };

        var folder = host.Paths.Ensure(Path.Combine(host.Paths.Root, "updates"));
        var feed = new GithubReleaseFeed(custom ?? GithubReleaseFeed.DefaultApi, Path.Combine(folder, "github.json"), $"DeviceDeck/{version} (+{ReleaseLinks.Repository})");
        if (custom is not null)
        {
            ShellLog.Info($"Release feed: {custom}.");
        }

        return new UpdateService(version, host.Settings, notifications, quit, new VelopackUpdateEngine(prerelease: version.Contains('-')), feed,
            new UpdateHistory(Path.Combine(folder, "state.json")), new UpdateServiceOptions
            {
                AutomaticChecks = automatic,
                WhatsNew = whatsNew,
                RanBefore = ranBefore,
                Architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64",
                BundledNotes = ReadBundledNotes(),
                OpenUrl = OpenInBrowser,
            });
    }

    private static string? ReadBundledNotes()
    {
        try
        {
            using var stream = typeof(UpdateSetup).Assembly.GetManifestResourceStream(BundledNotesResource);
            return stream is null ? null : new StreamReader(stream).ReadToEnd();
        }
        catch (IOException ex)
        {
            ShellLog.Warn($"This version's release notes couldn't be read: {ex.Message}");
            return null;
        }
    }

    private static void OpenInBrowser(string url)
    {
        if (url.StartsWith(ReleaseLinks.Releases + "/", StringComparison.Ordinal))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
    }
}
