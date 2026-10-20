using System.Text;
using DeviceDeck.Android.Adb;
using DeviceDeck.Android.Parsing;

namespace DeviceDeck.Android.Photos;

/// <summary>
/// Tells MediaStore about files adb created or removed. See README.md, "Media scan by Android version":
/// one explicit ACTION_MEDIA_SCANNER_SCAN_FILE broadcast to the MediaProvider package works on Android 10 to 16
/// (com.android.providers.media on 10, the mainline module com.[google.]android.providers.media.module from 11).
/// On 11+ writes through /sdcard (FUSE) are also indexed on close, so the broadcast is a second guarantee there.
/// </summary>
internal sealed class MediaScanner(IAdbChannel channel)
{
    public const string ScanAction = "android.intent.action.MEDIA_SCANNER_SCAN_FILE";

    private string? _providerPackage;
    private bool _providerLookedUp;

    public async Task ScanAsync(IReadOnlyCollection<string> devicePaths, CancellationToken ct)
    {
        if (devicePaths.Count == 0)
        {
            return;
        }
        string? package = await GetProviderPackageAsync(ct).ConfigureAwait(false);
        foreach (string script in Batches(devicePaths.Select(p => BroadcastCommand(p, package))))
        {
            // Best effort: a failed broadcast must not fail the copy that already happened.
            await channel.ShellAsync(script, ct).ConfigureAwait(false);
        }
    }

    public static string BroadcastCommand(string devicePath, string? providerPackage)
    {
        var command = new StringBuilder("am broadcast -a ").Append(ScanAction)
            .Append(" -d ").Append(ShellQuote.Quote(FileUri(CanonicalPath(devicePath))));
        if (providerPackage != null)
        {
            // Explicit package: an implicit broadcast to a manifest receiver is dropped on Android 8+.
            command.Append(" -p ").Append(providerPackage);
        }
        return command.Append(" >/dev/null").ToString();
    }

    /// <summary>/sdcard is a symlink; MediaStore keys rows by /storage/emulated/0/....</summary>
    public static string CanonicalPath(string devicePath)
    {
        foreach (string alias in new[] { "/sdcard", "/storage/self/primary", "/mnt/sdcard" })
        {
            if (devicePath == alias)
            {
                return "/storage/emulated/0";
            }
            if (devicePath.StartsWith(alias + "/", StringComparison.Ordinal))
            {
                return "/storage/emulated/0" + devicePath[alias.Length..];
            }
        }
        return devicePath;
    }

    /// <summary>file:// URI with each segment percent-encoded (Uri.getPath() on the phone decodes it back).</summary>
    public static string FileUri(string path) =>
        "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>The package that owns MediaProvider: the mainline module when present, else the legacy one.</summary>
    public static string? PickProviderPackage(string pmListOutput)
    {
        var names = PackageListParser.Parse(pmListOutput).Select(p => p.PackageName).ToList();
        return names.FirstOrDefault(n => n.EndsWith(".providers.media.module", StringComparison.Ordinal))
            ?? names.FirstOrDefault(n => n == "com.android.providers.media");
    }

    private async Task<string?> GetProviderPackageAsync(CancellationToken ct)
    {
        if (!_providerLookedUp)
        {
            var result = await channel.ShellAsync("pm list packages providers.media", ct).ConfigureAwait(false);
            _providerPackage = PickProviderPackage(result.Output);
            _providerLookedUp = true;
        }
        return _providerPackage;
    }

    private static IEnumerable<string> Batches(IEnumerable<string> commands)
    {
        var batch = new StringBuilder();
        foreach (string command in commands)
        {
            if (batch.Length > 0 && batch.Length + command.Length > 30_000)
            {
                yield return batch.ToString();
                batch.Clear();
            }
            batch.Append(command).Append("; ");
        }
        if (batch.Length > 0)
        {
            yield return batch.Append("true").ToString();
        }
    }
}
