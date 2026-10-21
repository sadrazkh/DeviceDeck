using System.Globalization;
using System.Net;
using System.Text.Json;
using DeviceDeck.Bridge;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Hosting;

namespace DeviceDeck.Shell.Desktop;

/// <summary>A release as GitHub's API describes it. Version is the tag without its "v".</summary>
internal sealed record GithubRelease(string Version, string? Name, string? Body, DateTimeOffset? Published, bool Prerelease);

internal enum FeedOutcome
{
    /// <summary>GitHub answered with the release (200), or said the cached copy is still current (304).</summary>
    Fresh,

    /// <summary>GitHub has no such release (404).</summary>
    NotFound,

    /// <summary>GitHub asked to wait (its rate limit) until <see cref="FeedAnswer.RetryAt"/>. Release is the cached copy, if any.</summary>
    Waiting,

    /// <summary>GitHub couldn't be reached or answered oddly. Release is the cached copy, if any.</summary>
    Failed,
}

internal sealed record FeedAnswer(FeedOutcome Outcome, GithubRelease? Release, string? Error = null, DateTimeOffset? RetryAt = null);

/// <summary>What the feed keeps between runs (updates/github.json): each answer with its ETag, and GitHub's "wait until".</summary>
internal sealed record GithubFeedCache
{
    public DateTimeOffset? WaitUntil { get; init; }
    public Dictionary<string, GithubFeedEntry> Entries { get; init; } = new(StringComparer.Ordinal);
}

/// <param name="Release">Null for an answer that had no release (none published yet).</param>
internal sealed record GithubFeedEntry(string? ETag, DateTimeOffset Fetched, GithubRelease? Release);

/// <summary>
/// GitHub's releases API for DeviceDeck, unauthenticated (60 requests an hour per network). Every answer is cached with its
/// ETag and asked for again with If-None-Match (an unchanged release costs a 304). When GitHub says the limit is used up
/// (X-RateLimit-Remaining 0, or a 403/429, with Retry-After or X-RateLimit-Reset) nothing is asked until then, also after a restart.
/// </summary>
internal sealed class GithubReleaseFeed : IDisposable
{
    public static readonly Uri DefaultApi = new("https://api.github.com/repos/sadrazkh/DeviceDeck/");

    private const int MaxEntries = 16;

    // GitHub's advice for a 403/429 that says neither when nor how long: wait at least a minute.
    private static readonly TimeSpan UnsaidWait = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly Uri _api;
    private readonly string _cacheFile;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private GithubFeedCache _cache;

    /// <param name="api">The repository's API address (tests point it at a fake).</param>
    /// <param name="handler">For tests; null uses the system's (with its proxy settings).</param>
    public GithubReleaseFeed(Uri api, string cacheFile, string userAgent, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        _api = api.AbsoluteUri.EndsWith('/') ? api : new Uri(api.AbsoluteUri + "/");
        _cacheFile = cacheFile;
        _time = time ?? TimeProvider.System;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _cache = JsonFileStore.Read<GithubFeedCache>(cacheFile) ?? new GithubFeedCache();
    }

    /// <summary>Until when GitHub asked not to be asked again, while that is still ahead.</summary>
    public DateTimeOffset? WaitUntil
    {
        get
        {
            lock (_gate)
            {
                return _cache.WaitUntil is { } until && until > _time.GetUtcNow() ? until : null;
            }
        }
    }

    /// <summary>The newest release: /releases/latest, or with prereleases the newest of /releases.</summary>
    public Task<FeedAnswer> LatestAsync(bool includePrereleases, CancellationToken ct) =>
        includePrereleases ? GetAsync("releases?per_page=10", ParseNewest, ct) : GetAsync("releases/latest", ParseOne, ct);

    /// <summary>One version's release (its notes for "What's new").</summary>
    public Task<FeedAnswer> ReleaseAsync(string version, CancellationToken ct) =>
        ReleaseLinks.IsVersion(version)
            ? GetAsync($"releases/tags/v{version}", ParseOne, ct)
            : throw new ArgumentException($"\"{version}\" is not a DeviceDeck version.", nameof(version));

    private async Task<FeedAnswer> GetAsync(string path, Func<JsonElement, GithubRelease?> parse, CancellationToken ct)
    {
        GithubFeedEntry? cached;
        DateTimeOffset? waitUntil;
        lock (_gate)
        {
            _cache.Entries.TryGetValue(path, out cached);
            waitUntil = _cache.WaitUntil;
        }

        if (waitUntil is { } waiting && waiting > _time.GetUtcNow())
        {
            return new FeedAnswer(FeedOutcome.Waiting, cached?.Release, "GitHub asked DeviceDeck to wait before checking again.", waiting);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, path));
        if (cached?.ETag is { } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var retryAt = RateLimit(response);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    GithubRelease? release;
                    try
                    {
                        using var json = JsonDocument.Parse(body);
                        release = parse(json.RootElement);
                    }
                    catch (JsonException)
                    {
                        return new FeedAnswer(FeedOutcome.Failed, cached?.Release, "GitHub's answer wasn't readable.");
                    }

                    Store(path, new GithubFeedEntry(response.Headers.ETag?.ToString(), _time.GetUtcNow(), release), retryAt);
                    return new FeedAnswer(release is null ? FeedOutcome.NotFound : FeedOutcome.Fresh, release);
                }

                case HttpStatusCode.NotModified when cached is not null:
                    Store(path, cached with { Fetched = _time.GetUtcNow() }, retryAt);
                    return new FeedAnswer(cached.Release is null ? FeedOutcome.NotFound : FeedOutcome.Fresh, cached.Release);

                case HttpStatusCode.NotFound:
                    Store(path, null, retryAt);
                    return new FeedAnswer(FeedOutcome.NotFound, null);

                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                {
                    var until = retryAt ?? _time.GetUtcNow() + UnsaidWait;
                    Store(path, cached, until);
                    ShellLog.Warn($"GitHub's API answered {(int)response.StatusCode} for {path}; asking again after {until:HH:mm:ss}.");
                    return new FeedAnswer(FeedOutcome.Waiting, cached?.Release, "GitHub asked DeviceDeck to wait before checking again.", until);
                }

                default:
                    Store(path, cached, retryAt);
                    return new FeedAnswer(FeedOutcome.Failed, cached?.Release, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Offline, a proxy that refused, or the 20-second timeout.
            return new FeedAnswer(FeedOutcome.Failed, cached?.Release, ex.Message);
        }
    }

    /// <summary>When an answer says the limit is used up: Retry-After, or X-RateLimit-Reset once X-RateLimit-Remaining is 0.</summary>
    private DateTimeOffset? RateLimit(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            return retry.Delta is { } delta ? _time.GetUtcNow() + delta : retry.Date;
        }

        if (Header(response, "X-RateLimit-Remaining") == "0" &&
            long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset))
        {
            return DateTimeOffset.FromUnixTimeSeconds(reset);
        }

        return null;
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <param name="entry">Null forgets the path's answer.</param>
    private void Store(string path, GithubFeedEntry? entry, DateTimeOffset? waitUntil)
    {
        lock (_gate)
        {
            var entries = new Dictionary<string, GithubFeedEntry>(_cache.Entries, StringComparer.Ordinal);
            if (entry is null)
            {
                entries.Remove(path);
            }
            else
            {
                entries[path] = entry;
            }

            foreach (var old in entries.OrderByDescending(e => e.Value.Fetched).Skip(MaxEntries).Select(e => e.Key).ToList())
            {
                entries.Remove(old);
            }

            _cache = new GithubFeedCache { WaitUntil = waitUntil ?? _cache.WaitUntil, Entries = entries };
            try
            {
                JsonFileStore.Write(_cacheFile, _cache);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShellLog.Warn($"The GitHub release cache could not be saved: {ex.Message}");
            }
        }
    }

    /// <summary>A release object; drafts and tags that aren't versions don't count.</summary>
    internal static GithubRelease? ParseOne(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var version = tag.GetString()!.TrimStart('v', 'V');
        if (!ReleaseLinks.IsVersion(version))
        {
            return null;
        }

        return new GithubRelease(
            version,
            Text(release, "name"),
            Text(release, "body"),
            DateTimeOffset.TryParse(Text(release, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? published : null,
            release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True);
    }

    /// <summary>The newest version in a list of releases.</summary>
    internal static GithubRelease? ParseNewest(JsonElement releases) =>
        releases.ValueKind != JsonValueKind.Array
            ? null
            : releases.EnumerateArray().Select(ParseOne).OfType<GithubRelease>()
                .Aggregate((GithubRelease?)null, (best, r) => best is null || ReleaseLinks.IsNewer(r.Version, best.Version) ? r : best);

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose() => _http.Dispose();
}
