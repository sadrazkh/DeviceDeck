namespace DeviceDeck.Android.Adb;

/// <summary>Result of one shell command run over the shell v2 protocol (separate stdout/stderr, real exit code).</summary>
internal sealed record ShellResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Everything the device classes need from adb for one phone. The real one talks to the adb server;
/// tests replay fixtures through a fake.
/// </summary>
internal interface IAdbChannel
{
    string Serial { get; }

    /// <summary>Runs <paramref name="command"/> with sh -c on the phone, no PTY. Output is decoded as UTF-8.</summary>
    Task<ShellResult> ShellAsync(string command, CancellationToken ct);

    /// <summary>Like <see cref="ShellAsync"/> but streams stdout's raw bytes (screencap, file ranges).</summary>
    Task<ShellResult> ShellToStreamAsync(string command, Stream stdout, CancellationToken ct);

    /// <summary>
    /// Runs an exec: service, feeding <paramref name="input"/> as raw stdin (streamed installs), and returns
    /// the combined output once the phone closes the stream.
    /// </summary>
    Task<string> ExecAsync(string command, Stream? input, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Opens a sync-service session (push/pull). Dispose it to release the connection.</summary>
    Task<IAdbSyncSession> OpenSyncAsync(CancellationToken ct);

    /// <summary>"" = normal reboot, "recovery", "bootloader".</summary>
    Task RebootAsync(string target, CancellationToken ct);
}

internal interface IAdbSyncSession : IAsyncDisposable
{
    Task PullAsync(string remotePath, Stream destination, IProgress<long>? progress, CancellationToken ct);

    Task PushAsync(Stream source, string remotePath, DateTimeOffset modified, IProgress<long>? progress, CancellationToken ct);
}

internal static class AdbChannelExtensions
{
    /// <summary>Pull through a short-lived sync session.</summary>
    public static async Task PullAsync(this IAdbChannel channel, string remotePath, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        await using var sync = await channel.OpenSyncAsync(ct).ConfigureAwait(false);
        await sync.PullAsync(remotePath, destination, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Push through a short-lived sync session.</summary>
    public static async Task PushAsync(this IAdbChannel channel, Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct)
    {
        await using var sync = await channel.OpenSyncAsync(ct).ConfigureAwait(false);
        await sync.PushAsync(source, remotePath, DateTimeOffset.UtcNow, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a command and returns stdout; a non-zero exit becomes a mapped <see cref="Core.Abstractions.DeviceException"/>.</summary>
    public static async Task<string> ShellCheckedAsync(this IAdbChannel channel, string command, CancellationToken ct)
    {
        var result = await channel.ShellAsync(command, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw AdbErrors.FromShell(command, result);
        }
        return result.Output;
    }

    /// <summary>Stdout of a command whose failure only means "not available here" (optional props, sysfs).</summary>
    public static async Task<string?> ShellOptionalAsync(this IAdbChannel channel, string command, CancellationToken ct)
    {
        var result = await channel.ShellAsync(command, ct).ConfigureAwait(false);
        return result.Succeeded ? result.Output : null;
    }
}
