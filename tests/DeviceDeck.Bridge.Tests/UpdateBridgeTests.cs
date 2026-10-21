using System.Text.Json;
using System.Text.Json.Nodes;
using DeviceDeck.Bridge.Images;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Bridge.Tests.Support;

namespace DeviceDeck.Bridge.Tests;

/// <summary>Updates you can see (round 4): app.updateStatus's shape, app.releaseNotes / openReleasePage / whatsNewShown, app.updateChanged.</summary>
public sealed class UpdateBridgeTests
{
    [Fact]
    public async Task Update_status_carries_how_this_copy_runs_and_what_the_last_check_found()
    {
        await using var host = await TestHost.StartAsync();
        var updates = new FakeUpdates
        {
            Status = new UpdateStatus(UpdateState.Available, "0.3.0", NewVersion: "0.4.0", LastChecked: new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
                Install: UpdateInstall.Portable, Architecture: "arm64", LastResult: UpdateCheckResult.NewVersion, WhatsNew: "0.3.0"),
        };
        using var desktop = new DesktopBridge(host, updates);

        var status = (await desktop.ResultAsync("app.updateStatus"))!.AsObject();

        Assert.Equal("available", (string?)status["state"]);
        Assert.Equal("0.3.0", (string?)status["currentVersion"]);
        Assert.Equal("0.4.0", (string?)status["newVersion"]);
        Assert.Equal("portable", (string?)status["install"]);
        Assert.Equal("stable", (string?)status["channel"]);
        Assert.Equal("arm64", (string?)status["architecture"]);
        Assert.Equal("newVersion", (string?)status["lastResult"]);
        Assert.Null(status["retryAt"]);
        Assert.Equal("0.3.0", (string?)status["whatsNew"]);
        Assert.True(status.ContainsKey("lastChecked"));

        updates.Raise(updates.Status with { State = UpdateState.Checking });
        await Wait.UntilAsync(() => desktop.Transport.EventsNamed("app.updateChanged").Any(), "app.updateChanged");
        Assert.Equal("checking", (string?)desktop.Transport.EventsNamed("app.updateChanged").Single()["params"]!["state"]);
    }

    [Fact]
    public async Task Release_notes_are_asked_for_a_version()
    {
        await using var host = await TestHost.StartAsync();
        var updates = new FakeUpdates();
        using var desktop = new DesktopBridge(host, updates);

        var notes = (await desktop.ResultAsync("app.releaseNotes", new { version = "0.4.0" }))!.AsObject();

        Assert.Equal("0.4.0", Assert.Single(updates.NotesAskedFor));
        Assert.Equal("0.4.0", (string?)notes["version"]);
        Assert.Equal("https://github.com/sadrazkh/DeviceDeck/releases/tag/v0.4.0", (string?)notes["url"]);
        Assert.Equal("# Notes", (string?)notes["markdown"]);
        Assert.Equal("github", (string?)notes["source"]);

        foreach (var bad in new object[] { new { version = "v0.4.0" }, new { version = "../x" }, new { } })
        {
            var refused = await desktop.CallAsync("app.releaseNotes", bad);
            Assert.Equal(-32602, (int?)refused["error"]!["code"]);
        }

        Assert.Single(updates.NotesAskedFor);
    }

    [Fact]
    public async Task Only_a_version_or_nothing_opens_a_release_page()
    {
        await using var host = await TestHost.StartAsync();
        var updates = new FakeUpdates();
        using var desktop = new DesktopBridge(host, updates);

        await desktop.ResultAsync("app.openReleasePage", new { version = "0.4.0" });
        await desktop.ResultAsync("app.openReleasePage", new { });
        var refused = await desktop.CallAsync("app.openReleasePage", new { version = "https://evil.example/" });

        Assert.Equal(-32602, (int?)refused["error"]!["code"]);
        Assert.Equal(["0.4.0", null], updates.PagesOpened);
    }

    [Fact]
    public async Task Whats_new_shown_is_passed_on()
    {
        await using var host = await TestHost.StartAsync();
        var updates = new FakeUpdates();
        using var desktop = new DesktopBridge(host, updates);

        await desktop.ResultAsync("app.whatsNewShown", new { version = "0.4.0" });

        Assert.Equal("0.4.0", Assert.Single(updates.WhatsNewShown));
    }

    [Fact]
    public async Task Without_the_window_the_new_methods_are_not_supported()
    {
        await using var host = await TestHost.StartAsync();
        foreach (var (method, parameters) in new (string, object)[]
                 {
                     ("app.releaseNotes", new { version = "0.4.0" }),
                     ("app.openReleasePage", new { }),
                     ("app.whatsNewShown", new { version = "0.4.0" }),
                 })
        {
            var answer = await host.CallAsync(method, parameters);
            Assert.Equal("not_supported", (string?)answer["error"]!["data"]!["code"]);
        }
    }

    /// <summary>A second bridge over the test host's phones and queue, with a desktop shell whose updates are <see cref="FakeUpdates"/>.</summary>
    private sealed class DesktopBridge : IDisposable
    {
        private readonly BridgeHost _bridge;
        private int _nextId;

        public DesktopBridge(TestHost host, IUpdateService updates)
        {
            _bridge = new BridgeHost(Transport, new BridgeServices
            {
                Hub = host.Hub,
                Transfers = host.Queue,
                Settings = host.Settings,
                Shell = host.Shell,
                Screenshots = new ScreenshotStore(),
                Desktop = new FakeDesktop(updates),
            });
        }

        public MemoryTransport Transport { get; } = new();

        public Task<JsonObject> CallAsync(string method, object? parameters = null)
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters is null ? null : JsonSerializer.SerializeToNode(parameters, Core.Serialization.DeviceDeckJson.Options),
            };
            return Transport.RequestAsync(request.ToJsonString(), id, []);
        }

        public async Task<JsonNode?> ResultAsync(string method, object? parameters = null)
        {
            var response = await CallAsync(method, parameters);
            Assert.True(response.ContainsKey("result"), $"{method} failed: {response.ToJsonString()}");
            return response["result"];
        }

        public void Dispose() => _bridge.Dispose();
    }

    private sealed class FakeDesktop(IUpdateService updates) : IDesktopShell
    {
        public IUpdateService Updates => updates;

        public Task<DragOutResult> DragOutAsync(DragOutParams request, CancellationToken ct) => throw new NotSupportedException();

        public void OpenFile(string path) => throw new NotSupportedException();
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public UpdateStatus Status { get; set; } = new(UpdateState.Idle, "0.3.0");

        public List<string> NotesAskedFor { get; } = [];

        public List<string?> PagesOpened { get; } = [];

        public List<string> WhatsNewShown { get; } = [];

        public event EventHandler<UpdateStatus>? Changed;

        public void Raise(UpdateStatus status) => Changed?.Invoke(this, Status = status);

        public Task<UpdateStatus> CheckAsync(CancellationToken ct) => Task.FromResult(Status);

        public void ApplyAndRestart() => throw new InvalidOperationException("No update has been downloaded yet.");

        public Task<ReleaseNotes> GetReleaseNotesAsync(string version, CancellationToken ct)
        {
            NotesAskedFor.Add(version);
            return Task.FromResult(new ReleaseNotes(version, ReleaseLinks.ReleasePage(version), $"DeviceDeck v{version}", "# Notes", null, "github"));
        }

        public void OpenReleasePage(string? version) => PagesOpened.Add(version);

        public void MarkWhatsNewShown(string version) => WhatsNewShown.Add(version);
    }
}
