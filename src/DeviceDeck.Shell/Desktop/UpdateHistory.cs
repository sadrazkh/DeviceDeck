using DeviceDeck.Bridge;
using DeviceDeck.Core.Settings;
using DeviceDeck.Shell.Hosting;

namespace DeviceDeck.Shell.Desktop;

/// <summary>What the update service remembers between runs (updates/state.json).</summary>
internal sealed record UpdateHistoryData
{
    public DateTimeOffset? LastChecked { get; init; }

    public UpdateCheckResult? LastResult { get; init; }

    /// <summary>The newest version the last check that reached GitHub saw.</summary>
    public string? LatestVersion { get; init; }

    /// <summary>The version that ran last: a newer one now is the first start after an update ("What's new").</summary>
    public string? LastRunVersion { get; init; }

    public string? WhatsNewShown { get; init; }

    /// <summary>The version a Windows notification was shown for (once per version).</summary>
    public string? NotifiedVersion { get; init; }
}

internal sealed class UpdateHistory(string file)
{
    private readonly Lock _gate = new();
    private UpdateHistoryData _data = JsonFileStore.Read<UpdateHistoryData>(file) ?? new UpdateHistoryData();

    public UpdateHistoryData Current
    {
        get
        {
            lock (_gate)
            {
                return _data;
            }
        }
    }

    public void Update(Func<UpdateHistoryData, UpdateHistoryData> change)
    {
        lock (_gate)
        {
            _data = change(_data);
            try
            {
                JsonFileStore.Write(file, _data);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShellLog.Warn($"The update history could not be saved: {ex.Message}");
            }
        }
    }
}
