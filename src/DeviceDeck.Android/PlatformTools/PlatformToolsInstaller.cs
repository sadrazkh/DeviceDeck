using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.PlatformTools;

/// <summary>A download or install step failed; <see cref="Code"/> is one of <see cref="DriverDownloadErrors"/>.</summary>
internal sealed class PlatformToolsException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

/// <summary>
/// Downloads Google's platform-tools zip into DeviceDeck's tools folder and installs it as tools\platform-tools, only
/// when it is complete and checked:
/// <list type="number">
/// <item>the download goes to platform-tools.zip.part and is renamed only once every byte arrived;</item>
/// <item>the zip must hold platform-tools/adb.exe, AdbWinApi.dll and AdbWinUsbApi.dll, every entry inside platform-tools/;</item>
/// <item>it is unpacked into platform-tools.new, where the three files must be signed by Google LLC and adb.exe must run
/// (`adb version`);</item>
/// <item>only then does platform-tools.new become platform-tools.</item>
/// </list>
/// Anything that fails or is cancelled leaves nothing behind but the previous install.
/// </summary>
internal sealed class PlatformToolsInstaller(
    string toolsDirectory,
    Uri source,
    Func<string, string?> signerOf,
    Func<string, CancellationToken, Task<string?>> toolsVersionOf,
    HttpMessageHandler? handler = null)
{
    internal static readonly string[] Required = ["adb.exe", "AdbWinApi.dll", "AdbWinUsbApi.dll"];
    private const string Root = "platform-tools/";
    private const long MaxDownload = 200L * 1024 * 1024;
    private const long MaxUnpacked = 600L * 1024 * 1024;

    public string InstallDirectory => Path.Combine(toolsDirectory, "platform-tools");

    private string PartFile => Path.Combine(toolsDirectory, "platform-tools.zip.part");
    private string ZipFile => Path.Combine(toolsDirectory, "platform-tools.zip");
    private string Staging => Path.Combine(toolsDirectory, "platform-tools.new");

    /// <summary>Downloads, checks and installs. Returns the tools' version ("37.0.1").</summary>
    /// <param name="progress">Bytes received and the total when known, as the download goes.</param>
    /// <param name="stage">Called as it moves on to verifying and installing.</param>
    public async Task<string> InstallAsync(Action<long, long?> progress, Action<DriverDownloadState> stage, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(toolsDirectory);
            CleanUp();
            await DownloadAsync(progress, ct).ConfigureAwait(false);

            stage(DriverDownloadState.Verifying);
            Unpack(ct);
            string version = await VerifyAsync(ct).ConfigureAwait(false);

            stage(DriverDownloadState.Installing);
            Install();
            return version;
        }
        catch (IOException ex)
        {
            throw new PlatformToolsException(DriverDownloadErrors.Disk, "The Android tools couldn't be saved on this PC: " + ex.Message, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PlatformToolsException(DriverDownloadErrors.Disk, "Windows didn't let DeviceDeck save the Android tools: " + ex.Message, ex);
        }
        finally
        {
            CleanUp();
        }
    }

    private async Task DownloadAsync(Action<long, long?> progress, CancellationToken ct)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeviceDeck");
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PlatformToolsException(DriverDownloadErrors.Network, "Google's download server couldn't be reached: " + ex.Message, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new PlatformToolsException(DriverDownloadErrors.Network, $"Google's download server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            long? total = response.Content.Headers.ContentLength;
            if (total > MaxDownload)
            {
                throw new PlatformToolsException(DriverDownloadErrors.Invalid, "The download is far bigger than Google's platform-tools.");
            }

            progress(0, total);
            long received = 0;
            var buffer = new byte[81920];
            await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(PartFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await body.ReadAsync(buffer, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        throw new PlatformToolsException(DriverDownloadErrors.Network, "The download stopped part-way: " + ex.Message, ex);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (received > MaxDownload || received > total)
                    {
                        throw new PlatformToolsException(DriverDownloadErrors.Invalid, "The download is bigger than the server said.");
                    }

                    progress(received, total);
                }
            }

            if (total is { } expected && received != expected)
            {
                throw new PlatformToolsException(DriverDownloadErrors.Network, $"The download was cut short ({received:N0} of {expected:N0} bytes).");
            }
        }

        // Complete: only now does it get its real name.
        File.Move(PartFile, ZipFile);
    }

    private void Unpack(CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = System.IO.Compression.ZipFile.OpenRead(ZipFile);
        }
        catch (InvalidDataException ex)
        {
            throw new PlatformToolsException(DriverDownloadErrors.Corrupt, "The download isn't a readable zip file.", ex);
        }

        using (zip)
        {
            long unpacked = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(Root, StringComparison.OrdinalIgnoreCase) || name.Split('/').Any(part => part is ".." or "."))
                {
                    throw new PlatformToolsException(DriverDownloadErrors.Invalid, $"The zip has a file outside platform-tools ({entry.FullName}).");
                }

                unpacked += entry.Length;
                names.Add(name[Root.Length..]);
            }

            var missing = Required.Where(r => !names.Contains(r)).ToList();
            if (missing.Count > 0)
            {
                throw new PlatformToolsException(DriverDownloadErrors.Invalid, "The zip isn't Google's platform-tools: " + string.Join(", ", missing) + " missing.");
            }

            if (unpacked > MaxUnpacked)
            {
                throw new PlatformToolsException(DriverDownloadErrors.Invalid, "The zip unpacks to far more than Google's platform-tools.");
            }

            string staging = Path.GetFullPath(Staging) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(staging);
            try
            {
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace('\\', '/')[Root.Length..];
                    if (relative.Length == 0)
                    {
                        continue;
                    }

                    string target = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!target.StartsWith(staging, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PlatformToolsException(DriverDownloadErrors.Invalid, $"The zip has a file outside platform-tools ({entry.FullName}).");
                    }

                    if (relative.EndsWith('/'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: false);
                }
            }
            catch (InvalidDataException ex)
            {
                throw new PlatformToolsException(DriverDownloadErrors.Corrupt, "The zip is damaged: " + ex.Message, ex);
            }
        }
    }

    private async Task<string> VerifyAsync(CancellationToken ct)
    {
        foreach (string file in Required)
        {
            string path = Path.Combine(Staging, file);
            string? signer = signerOf(path);
            if (!string.Equals(signer, AuthenticodeSigner.Google, StringComparison.Ordinal))
            {
                throw new PlatformToolsException(DriverDownloadErrors.Unverified,
                    signer is null ? $"{file} isn't signed by Google." : $"{file} is signed by {signer}, not Google.");
            }
        }

        string? version;
        try
        {
            version = await toolsVersionOf(Path.Combine(Staging, "adb.exe"), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PlatformToolsException(DriverDownloadErrors.Unverified, "adb.exe didn't run on this PC: " + ex.Message, ex);
        }

        return version ?? throw new PlatformToolsException(DriverDownloadErrors.Unverified, "adb.exe didn't say its version, so it wasn't installed.");
    }

    private void Install()
    {
        string target = InstallDirectory;
        if (Directory.Exists(target))
        {
            string old = target + ".old-" + DateTime.UtcNow.Ticks;
            try
            {
                Directory.Move(target, old);
            }
            catch (IOException ex)
            {
                throw new PlatformToolsException(DriverDownloadErrors.Disk, "The Android tools DeviceDeck downloaded before are still in use; close what uses them and try again.", ex);
            }

            TryDelete(old);
        }

        Directory.Move(Staging, target);
    }

    private void CleanUp()
    {
        TryDelete(PartFile);
        TryDelete(ZipFile);
        TryDelete(Staging);
        if (Directory.Exists(toolsDirectory))
        {
            foreach (string old in Directory.EnumerateDirectories(toolsDirectory, "platform-tools.old-*"))
            {
                TryDelete(old);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            else if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next download's clean-up.
        }
    }

    /// <summary>Runs `adb.exe version` (no server is started) and reads "Version 37.0.1-…".</summary>
    public static async Task<string?> ReadVersionAsync(string adbPath, CancellationToken ct)
    {
        var info = new ProcessStartInfo(adbPath, "version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(adbPath)!,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("adb.exe didn't start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? AdbServerHost.ParseToolsVersion(output) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new TimeoutException("adb.exe version didn't finish in 15 s.");
        }
    }
}
