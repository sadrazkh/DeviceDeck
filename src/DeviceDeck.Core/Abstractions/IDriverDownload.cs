namespace DeviceDeck.Core.Abstractions;

/// <summary>Idle: nothing asked yet. Downloading → Verifying → Installing → Done; or Failed / Cancelled.</summary>
public enum DriverDownloadState { Idle, Downloading, Verifying, Installing, Done, Failed, Cancelled }

/// <param name="ReceivedBytes">Bytes downloaded so far.</param>
/// <param name="TotalBytes">The download's size when the server said it.</param>
/// <param name="Error">One plain English sentence while <see cref="DriverDownloadState.Failed"/>.</param>
/// <param name="ErrorCode">One of <see cref="DriverDownloadErrors"/> while Failed: the page words it.</param>
/// <param name="Version">The tools' version once installed ("37.0.1").</param>
public sealed record DriverDownloadStatus(
    string ProviderId,
    DriverDownloadState State,
    long ReceivedBytes = 0,
    long? TotalBytes = null,
    string? Error = null,
    string? ErrorCode = null,
    string? Version = null);

/// <summary>Values of <see cref="DriverDownloadStatus.ErrorCode"/>.</summary>
public static class DriverDownloadErrors
{
    /// <summary>The download couldn't be fetched (no connection, HTTP error, cut short).</summary>
    public const string Network = "network";

    /// <summary>What arrived isn't a readable zip.</summary>
    public const string Corrupt = "corrupt";

    /// <summary>The zip is readable but isn't Google's platform-tools (files missing, odd paths, too big).</summary>
    public const string Invalid = "invalid";

    /// <summary>adb.exe or its DLLs aren't signed by Google, or adb.exe didn't run.</summary>
    public const string Unverified = "unverified";

    /// <summary>The files couldn't be written (disk full, folder in use).</summary>
    public const string Disk = "disk";
}

/// <summary>
/// A provider that can download its driver's files itself (v0.6: Android's platform-tools from Google, into DeviceDeck's
/// data folder). The download runs in the background; <see cref="DownloadChanged"/> reports it.
/// </summary>
public interface IDriverDownload
{
    /// <summary>The download now (or the last one this run).</summary>
    DriverDownloadStatus DownloadStatus { get; }

    /// <summary>Raised as the download moves (at most a few times a second) and when it ends.</summary>
    event EventHandler<DriverDownloadStatus>? DownloadChanged;

    /// <summary>Starts the download, or returns the one already running.</summary>
    DriverDownloadStatus StartDownload();

    /// <summary>Stops a running download; nothing is left behind. No-op when none runs.</summary>
    void CancelDownload();
}
