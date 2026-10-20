using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Transfers.Handlers;

/// <summary>PC-side and phone-side path helpers shared by the handlers.</summary>
internal static class TransferPaths
{
    public const string PartialSuffix = ".ddpart";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static void RequirePcFolder(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{what} must be a full folder path on this PC.");
        }
    }

    public static void RequireList<T>(IReadOnlyList<T>? list, string what)
    {
        if (list is null || list.Count == 0)
        {
            throw new ArgumentException($"Choose at least one {what}.");
        }
    }

    /// <summary>A phone file name made safe for Windows (':' and friends replaced, reserved names and trailing dots avoided).</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).TrimEnd(' ', '.');
        if (safe.Length == 0)
        {
            safe = "_";
        }

        var stem = Path.GetFileNameWithoutExtension(safe);
        return ReservedNames.Contains(stem) ? "_" + safe : safe;
    }

    /// <summary>"name (1).ext", "name (2).ext", … the first one free on the PC.</summary>
    public static string UniquePcPath(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Applies the conflict rule to a PC destination. Returns the path to write, or null to skip.
    /// Asking the user throws <see cref="TransferNeedsDecisionException"/>.
    /// </summary>
    public static string? ResolvePcDestination(TransferWorkContext context, TransferItem item, string destination)
    {
        if (Directory.Exists(destination))
        {
            return context.Decide(item, 0, item.Size) switch
            {
                ConflictChoice.Skip => null,
                ConflictChoice.KeepBoth => UniquePcPath(destination),
                _ => throw new DeviceException(DeviceErrorCodes.AlreadyExists, "A folder with that name is already there."),
            };
        }

        if (!File.Exists(destination))
        {
            return destination;
        }

        return context.Decide(item, new FileInfo(destination).Length, item.Size) switch
        {
            ConflictChoice.Skip => null,
            ConflictChoice.KeepBoth => UniquePcPath(destination),
            _ => destination,
        };
    }

    /// <summary>Writes through "&lt;name&gt;.ddpart" and renames at the end, so a half-copied file never carries the real name.</summary>
    public static async Task WritePcFileAsync(string destination, long expectedSize, Func<Stream, Task> copy, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + PartialSuffix;
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                await copy(file).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                if (expectedSize > 0 && file.Length != expectedSize)
                {
                    throw new DeviceException(DeviceErrorCodes.Protocol, "The copy came out the wrong size.",
                        new IOException($"Expected {expectedSize} bytes, wrote {file.Length}."));
                }
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string JoinDevice(string folder, string name) =>
        folder.EndsWith('/') ? folder + name : folder + "/" + name;

    public static async Task<string> UniqueDevicePathAsync(IDeviceFileSystem fs, string path, CancellationToken ct)
    {
        var cut = path.LastIndexOf('/');
        var folder = cut <= 0 ? "/" : path[..cut];
        var name = path[(cut + 1)..];
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            var candidate = JoinDevice(folder, $"{stem} ({i}){ext}");
            if (await fs.StatAsync(candidate, ct).ConfigureAwait(false) is null)
            {
                return candidate;
            }
        }
    }

    public static async Task EnsureDeviceFolderAsync(IDeviceFileSystem fs, string path, CancellationToken ct)
    {
        var existing = await fs.StatAsync(path, ct).ConfigureAwait(false);
        if (existing is null)
        {
            await fs.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
        }
        else if (!existing.IsDirectory)
        {
            throw new DeviceException(DeviceErrorCodes.AlreadyExists, $"A file named {existing.Name} is where a folder should go.");
        }
    }

    public static string Plural(int count, string one, string many) =>
        count == 1 ? $"1 {one}" : $"{count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {many}";
}
