using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Adb;

/// <summary>
/// Finds adb.exe and keeps DeviceDeck's own adb server running on its private port (5137 by default), so it never
/// fights the default server on 5037 that Android Studio or another tool may own.
/// </summary>
internal sealed partial class AdbServerHost
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Func<CancellationToken, Task>? _starter;

    /// <param name="starter">Replaces running `adb start-server` (tests start a fake server this way).</param>
    public AdbServerHost(string? adbPath, int port, Func<CancellationToken, Task>? starter = null)
    {
        AdbPath = adbPath;
        Port = port;
        EndPoint = new IPEndPoint(IPAddress.Loopback, port);
        _starter = starter;
    }

    /// <summary>Null when adb.exe wasn't found.</summary>
    public string? AdbPath { get; }

    /// <summary>Whether <see cref="EnsureStartedAsync"/> can start a server when none answers.</summary>
    public bool CanStart => AdbPath != null || _starter != null;

    /// <summary>
    /// True once this host started the server itself, as opposed to finding one already running
    /// (another DeviceDeck window, a probe run). Only a server we started is ours to stop.
    /// </summary>
    public bool StartedServer { get; private set; }

    public int Port { get; }

    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// The configured path when given (null if that file is missing); otherwise third_party/platform-tools/adb.exe
    /// next to the app or in any parent folder (the dev layout), or the DEVICEDECK_ADB_PATH variable.
    /// </summary>
    public static string? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        }
        string? fromEnvironment = Environment.GetEnvironmentVariable("DEVICEDECK_ADB_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }
        string exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            foreach (string candidate in new[]
            {
                Path.Combine(dir.FullName, "third_party", "platform-tools", exe),
                Path.Combine(dir.FullName, "platform-tools", exe),
            })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>The adb server's protocol version (41 for current platform-tools), or null when nothing answers on the port.</summary>
    public async Task<int?> GetServerVersionAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = await AdbConnection.ConnectAsync(EndPoint, ct).ConfigureAwait(false);
            await connection.SendRequestAsync("host:version", ct).ConfigureAwait(false);
            string? hex = await connection.ReadLengthPrefixedStringAsync(ct).ConfigureAwait(false);
            return hex != null && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int version) ? version : null;
        }
        catch (Exception ex) when (ex is AdbServerUnavailableException or AdbConnectionLostException or AdbFailException)
        {
            return null;
        }
    }

    /// <summary>Starts the server on our port unless it already answers. Throws when adb is missing or won't start.</summary>
    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (await GetServerVersionAsync(ct).ConfigureAwait(false) != null)
        {
            return;
        }
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await GetServerVersionAsync(ct).ConfigureAwait(false) != null)
            {
                return;
            }
            if (_starter != null)
            {
                await _starter(ct).ConfigureAwait(false);
                if (await GetServerVersionAsync(ct).ConfigureAwait(false) == null)
                {
                    throw new InvalidOperationException($"The adb server on port {Port} didn't come up.");
                }
                StartedServer = true;
                return;
            }
            if (AdbPath == null)
            {
                throw new FileNotFoundException("adb.exe wasn't found; run tools/fetch-platform-tools.ps1.");
            }
            // adb itself restarts an older server it finds on the port ("server version doesn't match").
            var (exitCode, output) = await RunAdbAsync(["-P", Port.ToString(), "start-server"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            if (exitCode != 0 || await GetServerVersionAsync(ct).ConfigureAwait(false) == null)
            {
                throw new InvalidOperationException($"adb didn't start its server on port {Port}: {AdbErrors.FirstLine(output)}");
            }
            StartedServer = true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Stops the server on our port through its host:kill service (what `adb kill-server` sends), then waits up to
    /// two seconds for the port to go quiet.
    /// </summary>
    public async Task KillServerAsync(CancellationToken ct)
    {
        if (await GetServerVersionAsync(ct).ConfigureAwait(false) == null)
        {
            StartedServer = false;
            return;
        }
        await using (var connection = await AdbConnection.ConnectAsync(EndPoint, ct).ConfigureAwait(false))
        {
            try
            {
                await connection.SendRequestAsync("host:kill", ct).ConfigureAwait(false);
            }
            catch (AdbConnectionLostException)
            {
                // The server closes the socket as it exits.
            }
        }
        StartedServer = false;
        for (int i = 0; i < 20 && await GetServerVersionAsync(ct).ConfigureAwait(false) != null; i++)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    /// <summary>"37.0.1" from source.properties next to adb.exe, else from `adb version`.</summary>
    public async Task<string?> GetToolsVersionAsync(CancellationToken ct)
    {
        if (AdbPath == null)
        {
            return null;
        }
        string properties = Path.Combine(Path.GetDirectoryName(AdbPath)!, "source.properties");
        if (File.Exists(properties))
        {
            foreach (string line in await File.ReadAllLinesAsync(properties, ct).ConfigureAwait(false))
            {
                var match = Regex.Match(line, @"^\s*Pkg\.Revision\s*=\s*(\S+)");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        var (_, output) = await RunAdbAsync(["version"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        return ParseToolsVersion(output);
    }

    [GeneratedRegex(@"^Version\s+(?<v>[0-9][0-9.]*)", RegexOptions.Multiline)]
    private static partial Regex VersionLineRegex();

    internal static string? ParseToolsVersion(string adbVersionOutput)
    {
        var match = VersionLineRegex().Match(adbVersionOutput);
        return match.Success ? match.Groups["v"].Value.TrimEnd('.') : null;
    }

    /// <summary>Runs adb.exe with the private port in its environment; waits for the process, not for its pipes.</summary>
    private async Task<(int ExitCode, string Output)> RunAdbAsync(string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(AdbPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(AdbPath!)!,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment["ANDROID_ADB_SERVER_PORT"] = Port.ToString();

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The forked server may inherit the pipes and keep them open, so wait on the process handle only.
        bool exited = await Task.Run(() => process.WaitForExit((int)timeout.TotalMilliseconds), ct).ConfigureAwait(false);
        if (!exited)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new TimeoutException($"adb {string.Join(' ', arguments)} didn't finish in {timeout.TotalSeconds:0} s.");
        }
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }
}
