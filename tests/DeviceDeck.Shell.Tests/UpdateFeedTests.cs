using System.Net;
using DeviceDeck.Bridge;
using DeviceDeck.Shell.Desktop;

namespace DeviceDeck.Shell.Tests;

/// <summary>GitHub's releases API through a fake (no network): ETags, the rate limit, failures, drafts and prereleases.</summary>
public sealed class UpdateFeedTests
{
    [Fact]
    public async Task The_latest_release_is_read_with_GitHubs_headers()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/latest", "0.4.0", "# 🚀 DeviceDeck v0.4.0\r\n\r\n- **Updates you can see**");
        using var feed = bench.Feed();

        var answer = await feed.LatestAsync(includePrereleases: false, CancellationToken.None);

        Assert.Equal(FeedOutcome.Fresh, answer.Outcome);
        Assert.Equal("0.4.0", answer.Release!.Version);
        Assert.Equal("DeviceDeck v0.4.0", answer.Release.Name);
        Assert.StartsWith("# 🚀 DeviceDeck v0.4.0", answer.Release.Body);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero), answer.Release.Published);
        var request = Assert.Single(bench.Github.Requests);
        Assert.Equal("releases/latest", request.Path);
        Assert.Null(request.IfNoneMatch);
        Assert.StartsWith("DeviceDeck/", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Equal("2022-11-28", request.ApiVersion);
    }

    [Fact]
    public async Task An_unchanged_release_is_asked_with_its_ETag_and_comes_from_the_cache_also_after_a_restart()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/latest", "0.4.0");
        bench.Github.Remaining = 10;
        using (var first = bench.Feed())
        {
            await first.LatestAsync(false, CancellationToken.None);
        }

        using var second = bench.Feed(); // a new run reads the cache file
        var answer = await second.LatestAsync(false, CancellationToken.None);

        Assert.Equal(FeedOutcome.Fresh, answer.Outcome);
        Assert.Equal("0.4.0", answer.Release!.Version);
        var requests = bench.Github.Requests;
        Assert.Equal(2, requests.Count);
        Assert.NotNull(requests[1].IfNoneMatch);
        Assert.Equal(9, bench.Github.Remaining); // the 304 cost nothing
    }

    [Fact]
    public async Task When_the_limit_is_used_up_nothing_is_asked_until_it_resets_also_after_a_restart()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/latest", "0.4.0");
        bench.Github.Release("releases/tags/v0.4.0", "0.4.0");
        bench.Github.Remaining = 1;
        bench.Github.Reset = bench.Time.GetUtcNow().AddMinutes(40);
        using (var feed = bench.Feed())
        {
            Assert.Equal(FeedOutcome.Fresh, (await feed.LatestAsync(false, CancellationToken.None)).Outcome); // the last allowed request
            Assert.Equal(bench.Github.Reset, feed.WaitUntil);
        }

        using var restarted = bench.Feed();
        var waiting = await restarted.LatestAsync(false, CancellationToken.None);
        Assert.Equal(FeedOutcome.Waiting, waiting.Outcome);
        Assert.Equal(bench.Github.Reset, waiting.RetryAt);
        Assert.Equal("0.4.0", waiting.Release!.Version); // what GitHub said last time
        Assert.Equal(FeedOutcome.Waiting, (await restarted.ReleaseAsync("0.4.0", CancellationToken.None)).Outcome);
        Assert.Single(bench.Github.Requests);

        bench.Time.Advance(TimeSpan.FromMinutes(41));
        bench.Github.Remaining = 60;
        Assert.Equal(FeedOutcome.Fresh, (await restarted.LatestAsync(false, CancellationToken.None)).Outcome);
        Assert.Equal(2, bench.Github.Requests.Count);
    }

    [Fact]
    public async Task A_403_or_429_waits_as_long_as_GitHub_says_or_a_minute()
    {
        using var bench = new UpdateBench();
        using var feed = bench.Feed();
        bench.Github.Override = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        };
        var told = await feed.LatestAsync(false, CancellationToken.None);
        Assert.Equal(FeedOutcome.Waiting, told.Outcome);
        Assert.Equal(bench.Time.GetUtcNow().AddSeconds(90), told.RetryAt);

        bench.Time.Advance(TimeSpan.FromSeconds(91));
        bench.Github.Override = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var unsaid = await feed.LatestAsync(false, CancellationToken.None);
        Assert.Equal(FeedOutcome.Waiting, unsaid.Outcome);
        Assert.Equal(bench.Time.GetUtcNow().AddMinutes(1), unsaid.RetryAt);
        Assert.Equal(2, bench.Github.Requests.Count);
    }

    [Fact]
    public async Task Offline_the_cached_copy_comes_back_as_a_failure()
    {
        using var bench = new UpdateBench();
        bench.Github.Release("releases/tags/v0.4.0", "0.4.0", "Cached notes");
        using var feed = bench.Feed();
        await feed.ReleaseAsync("0.4.0", CancellationToken.None);

        bench.Github.Override = _ => throw new HttpRequestException("No such host is known.");
        var offline = await feed.ReleaseAsync("0.4.0", CancellationToken.None);

        Assert.Equal(FeedOutcome.Failed, offline.Outcome);
        Assert.Equal("Cached notes", offline.Release!.Body);
        Assert.Contains("No such host", offline.Error);

        bench.Github.Override = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        Assert.Equal(FeedOutcome.Failed, (await feed.ReleaseAsync("0.4.0", CancellationToken.None)).Outcome);
        bench.Github.Override = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>captive portal</html>") };
        Assert.Equal(FeedOutcome.Failed, (await feed.ReleaseAsync("0.4.0", CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task No_release_yet_and_unknown_tags_are_not_found()
    {
        using var bench = new UpdateBench();
        using var feed = bench.Feed();
        Assert.Equal(FeedOutcome.NotFound, (await feed.LatestAsync(false, CancellationToken.None)).Outcome);
        Assert.Equal(FeedOutcome.NotFound, (await feed.ReleaseAsync("9.9.9", CancellationToken.None)).Outcome);
        await Assert.ThrowsAsync<ArgumentException>(() => feed.ReleaseAsync("../../evil", CancellationToken.None));
    }

    [Fact]
    public async Task With_prereleases_the_newest_published_version_wins_and_drafts_never_count()
    {
        using var bench = new UpdateBench();
        bench.Github.Route("releases?per_page=10", "[" + string.Join(",",
            FakeGithub.ReleaseJson("0.5.0", draft: true),
            FakeGithub.ReleaseJson("0.4.0-beta.2", prerelease: true),
            FakeGithub.ReleaseJson("0.4.0-beta.10", prerelease: true),
            FakeGithub.ReleaseJson("0.3.0")) + "]");
        using var feed = bench.Feed();

        var answer = await feed.LatestAsync(includePrereleases: true, CancellationToken.None);

        Assert.Equal("0.4.0-beta.10", answer.Release!.Version);
        Assert.True(answer.Release.Prerelease);
    }

    [Theory]
    [InlineData("0.4.0", "0.3.0", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("0.4.0-beta.1", "0.4.0", -1)]
    [InlineData("0.4.0-beta.10", "0.4.0-beta.2", 1)]
    [InlineData("0.4.0-beta", "0.4.0-beta.1", -1)]
    [InlineData("0.4.0-1", "0.4.0-alpha", -1)]
    public void Versions_are_ordered_like_semantic_versions(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(ReleaseLinks.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(ReleaseLinks.Compare(b, a)));
    }

    [Theory]
    [InlineData("0.4.0", true)]
    [InlineData("0.4.0-beta.1", true)]
    [InlineData("v0.4.0", false)]
    [InlineData("0.4", false)]
    [InlineData("0.4.0/../../x", false)]
    [InlineData("0.4.0-", false)]
    [InlineData("", false)]
    public void Only_versions_make_release_pages(string version, bool ok)
    {
        Assert.Equal(ok, ReleaseLinks.IsVersion(version));
        if (ok)
        {
            Assert.Equal($"https://github.com/sadrazkh/DeviceDeck/releases/tag/v{version}", ReleaseLinks.ReleasePage(version));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => ReleaseLinks.ReleasePage(version));
        }

        Assert.Equal("https://github.com/sadrazkh/DeviceDeck/releases/latest", ReleaseLinks.ReleasePage(null));
    }
}
