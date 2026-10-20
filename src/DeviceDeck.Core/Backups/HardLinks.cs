using System.Runtime.InteropServices;

namespace DeviceDeck.Core.Backups;

/// <summary>
/// Dated copies of a backup as hard links: every unchanged file is shared, so keeping several backups of a phone costs
/// little more disk than one. Needs NTFS (or ReFS); on exFAT, FAT32 and most network shares it isn't available.
/// </summary>
internal static partial class HardLinks
{
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    /// <summary>Whether a folder's drive can hard-link, tried once with two scratch files.</summary>
    public static bool IsSupported(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        Directory.CreateDirectory(folder);
        var probe = Path.Combine(folder, ".devicedeck-link-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, [1]);
            var linked = CreateHardLink(Extended(probe + ".link"), Extended(probe), IntPtr.Zero);
            if (linked)
            {
                File.Delete(probe + ".link");
            }

            return linked;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Mirrors <paramref name="source"/> into <paramref name="target"/> with hard links. Returns false when the drive
    /// can't link (nothing useful was made); other failures throw.
    /// </summary>
    public static bool Mirror(string source, string target, Func<string, bool> skip, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            if (skip(file))
            {
                continue;
            }

            var link = Path.Combine(target, Path.GetFileName(file));
            if (!TryLink(link, file))
            {
                return false;
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if (skip(directory))
            {
                continue;
            }

            if (!Mirror(directory, Path.Combine(target, Path.GetFileName(directory)), skip, ct))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>One link; false when the drive doesn't support links.</summary>
    public static bool TryLink(string link, string existing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (CreateHardLink(Extended(link), Extended(existing), IntPtr.Zero))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorInvalidFunction or ErrorNotSupported or ErrorNotSameDevice or ErrorInvalidParameter)
        {
            return false;
        }

        throw new IOException($"Could not link {link}: {Marshal.GetPInvokeErrorMessage(error)}", error);
    }

    /// <summary>
    /// The path with "\\?\" in front, so it may be longer than 260 characters. .NET's own file calls add it
    /// themselves; a backup's files sit a few levels below a folder the user chose, which can already be deep.
    /// </summary>
    internal static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }

        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFile, string existingFile, IntPtr security);
}
