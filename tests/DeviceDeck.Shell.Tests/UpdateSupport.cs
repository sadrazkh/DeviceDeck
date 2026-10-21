using System.Net;
using System.Text;
using System.Text.Json;
using DeviceDeck.Bridge;
using DeviceDeck.Core.Notifications;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Desktop;
using Microsoft.Extensions.Time.Testing;

namespace DeviceDeck.Shell.Tests;

/// <summary>
/// GitHub's releases API, faked: never the network. Routes are paths under the repository ("releases/latest",
/// "releases/tags/v0.4.0"); each answer has an ETag and an unchanged one is answered 304 to If-None-Match.
/// </summary>
internal sealed class FakeGithub : HttpMessageHandler
{
    public const string Api = "https://api.github.test/repos/sadrazkh/DeviceDeck/";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);
    private readonly List<FakeRequest> _requests = [];

    /// <summary>When set, answers every request instead of the routes (a 403, a dropped connection…).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Override { get; set; }

    /// <summary>X-RateLimit-Remaining on each answer (null: no rate-limit headers); a 200 uses one up.</summary>
    public int? Remaining { get; set; }

    public DateTimeOffset Reset { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

    public IReadOnlyList<FakeRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public void Route(string path, string json)
    {
        lock (_gate)
        {
            _routes[path] = json;
        }
    }

    public void Release(string path, string version, string? body = "Notes", bool prerelease = false, bool draft = false) =>
        Route(path, ReleaseJson(version, body, prerelease, draft));

    public static string ReleaseJson(string version, string? body = "Notes", bool prerelease = false, bool draft = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = $"v{version}",
            ["name"] = $"DeviceDeck v{version}",
            ["html_url"] = $"https://github.com/sadrazkh/DeviceDeck/releases/tag/v{version}",
            ["body"] = body,
            ["draft"] = draft,
            ["prerelease"] = prerelease,
            ["published_at"] = "2026-11-02T10:00:00Z",
            ["assets"] = Array.Empty<object>(),
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsoluteUri[Api.Length..];
        lock (_gate)
        {
            _requests.Add(new FakeRequest(path,
                request.Headers.TryGetValues("If-None-Match", out var etags) ? etags.Single() : null,
                request.Headers.UserAgent.ToString(),
                request.Headers.Accept.ToString(),
                request.Headers.TryGetValues("X-GitHub-Api-Version", out var api) ? api.Single() : null));
        }

        if (Override is { } answer)
        {
            return Task.FromResult(answer(request));
        }

        string? json;
        lock (_gate)
        {
            _routes.TryGetValue(path, out json);
        }

        if (json is null)
        {
            return Task.FromResult(Limited(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"Not Found\"}") }, used: true));
        }

        var etag = $"W/\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16]}\"";
        if (request.Headers.TryGetValues("If-None-Match", out var sent) && sent.Contains(etag))
        {
            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
            notModified.Headers.TryAddWithoutValidation("ETag", etag);
            return Task.FromResult(Limited(notModified, used: false));
        }

        var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        ok.Headers.TryAddWithoutValidation("ETag", etag);
        return Task.FromResult(Limited(ok, used: true));
    }

    private HttpResponseMessage Limited(HttpResponseMessage response, bool used)
    {
        if (Remaining is { } left)
        {
            Remaining = used ? Math.Max(0, left - 1) : left;
            response.Headers.TryAddWithoutValidation("X-RateLimit-Limit", "60");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", Remaining.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", Reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return response;
    }
}

internal sealed record FakeRequest(string Path, string? IfNoneMatch, string UserAgent, string Accept, string? ApiVersion);

/// <summary>Velopack, faked: CheckAsync offers <see cref="Next"/>; DownloadAsync reports <see cref="Steps"/> and can be held.</summary>
internal sealed class FakeUpdateEngine : IUpdateEngine
{
    private readonly Lock _gate = new();
    private readonly List<UpdateOffer> _applied = [];

    public bool IsInstalled { get; init; } = true;

    public UpdateOffer? Pending { get; init; }

    public UpdateOffer? Next { get; set; }

    public Exception? CheckFails { get; set; }

    public int[] Steps { get; set; } = [0, 30, 60, 100];

    /// <summary>When set, the download waits for it after its first step.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public int Checks { get; private set; }

    public IReadOnlyList<UpdateOffer> Applied
    {
        get
        {
            lock (_gate)
            {
                return _applied.ToArray();
            }
        }
    }

    public Task<UpdateOffer?> CheckAsync(CancellationToken ct)
    {
        Checks++;
        return CheckFails is { } error ? Task.FromException<UpdateOffer?>(error) : Task.FromResult(Next);
    }

    public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
    {
        for (var i = 0; i < Steps.Length; i++)
        {
            progress(Steps[i]);
            if (i == 0 && Hold is { } hold)
            {
                await hold.Task.WaitAsync(ct);
            }
        }
    }

    public void ApplyOnExit(UpdateOffer offer)
    {
        lock (_gate)
        {
            _applied.Add(offer);
        }
    }
}

/// <summary>Every notification shown, in order (the recording service keeps only the last one per tag).</summary>
internal sealed class NotificationLog : INotificationService
{
    private readonly Lock _gate = new();
    private readonly List<AppNotification> _shown = [];

    public bool IsAvailable => true;

    public IReadOnlyList<AppNotification> Shown
    {
        get
        {
            lock (_gate)
            {
                return _shown.ToArray();
            }
        }
    }

    public void Show(AppNotification notification)
    {
        lock (_gate)
        {
            _shown.Add(notification);
        }
    }

    public void Remove(string tag)
    {
    }

    public event EventHandler<NotificationActivation>? Activated;

    public void Click(AppNotification notification, string command) =>
        Activated?.Invoke(this, new NotificationActivation(command, notification.DeviceId, notification.Tag, notification.Args));
}

/// <summary>A data folder with settings, the update history and the feed's cache; each <see cref="Start"/> is one run of the app.</summary>
internal sealed class UpdateBench : IDisposable
{
    private readonly List<UpdateService> _services = [];

    public UpdateBench()
    {
        Root = Path.Combine(Path.GetTempPath(), "DeviceDeckShellTests", "updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Settings = new SettingsStore(Path.Combine(Root, "settings.json"));
    }

    public string Root { get; }

    public SettingsStore Settings { get; }

    public FakeGithub Github { get; } = new();

    public NotificationLog Notifications { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    public List<string> Opened { get; } = [];

    public int Quits { get; private set; }

    public string HistoryFile => Path.Combine(Root, "updates", "state.json");

    public string CacheFile => Path.Combine(Root, "updates", "github.json");

    public GithubReleaseFeed Feed() => new(new Uri(FakeGithub.Api), CacheFile, "DeviceDeck/0.3.0 (+https://github.com/sadrazkh/DeviceDeck)", Time, Github);

    /// <summary>One run of DeviceDeck at <paramref name="version"/>.</summary>
    public UpdateService Start(string version, IUpdateEngine? engine = null, Func<UpdateServiceOptions, UpdateServiceOptions>? options = null)
    {
        var defaults = new UpdateServiceOptions
        {
            Time = Time,
            OpenUrl = url =>
            {
                lock (Opened)
                {
                    Opened.Add(url);
                }
            },
        };
        var service = new UpdateService(version, Settings, Notifications, () => Quits++, engine ?? new FakeUpdateEngine { IsInstalled = false },
            Feed(), new UpdateHistory(HistoryFile), options?.Invoke(defaults) ?? defaults);
        _services.Add(service);
        return service;
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class UpdateWait
{
    /// <summary>Polls until <paramref name="done"/> holds (timer callbacks run checks on the thread pool).</summary>
    public static async Task UntilAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    public static UpdateOffer Offer(string version, string? notes = null) => new(version, notes);

    public static bool IsReleasePage(string url) => url.StartsWith(ReleaseLinks.Releases + "/", StringComparison.Ordinal);
}
