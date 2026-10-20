using System.Runtime.InteropServices;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Bridge.Pc;

public enum PcRootKind { Home, Desktop, Downloads, Documents, Pictures, Videos, Music, Drive, RemovableDrive, NetworkDrive, OpticalDrive }

/// <param name="Label">A drive's volume label (may be empty) or the known folder's own name; the page localises by <paramref name="Kind"/>.</param>
public sealed record PcRoot(string Path, string Label, PcRootKind Kind, long? TotalBytes = null, long? FreeBytes = null);

/// <param name="Path">The full Windows path.</param>
public sealed record PcEntry(string Path, string Name, bool IsDirectory, long Size, DateTimeOffset? Modified, bool ReadOnly = false, bool Hidden = false);

/// <summary>pc.roots, pc.list, pc.mkdir and pc.rename: the PC side of the Files screen. Errors are DeviceExceptions with plain sentences.</summary>
public static class PcFolders
{
    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    public static IReadOnlyList<PcRoot> Roots()
    {
        var roots = new List<PcRoot>();
        void Known(PcRootKind kind, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && roots.All(r => !SamePath(r.Path, path)))
            {
                roots.Add(new PcRoot(path, System.IO.Path.GetFileName(path.TrimEnd('\\')), kind));
            }
        }

        Known(PcRootKind.Home, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Known(PcRootKind.Desktop, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Known(PcRootKind.Downloads, DownloadsPath());
        Known(PcRootKind.Documents, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Known(PcRootKind.Pictures, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Known(PcRootKind.Videos, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Known(PcRootKind.Music, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var kind = drive.DriveType switch
                {
                    DriveType.Removable => PcRootKind.RemovableDrive,
                    DriveType.Network => PcRootKind.NetworkDrive,
                    DriveType.CDRom => PcRootKind.OpticalDrive,
                    _ => PcRootKind.Drive,
                };
                roots.Add(new PcRoot(drive.RootDirectory.FullName, drive.VolumeLabel, kind, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive that went away while we looked, or one we may not read: leave it out.
            }
        }

        return roots;
    }

    /// <summary>Folders and files in <paramref name="path"/>. Hidden and system items only when <paramref name="showHidden"/>.</summary>
    public static IReadOnlyList<PcEntry> List(string path, bool showHidden)
    {
        var folder = RequireFullPath(path);
        return Guard(folder, () =>
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists)
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That folder isn't on this PC any more.");
            }

            var entries = new List<PcEntry>();
            foreach (var item in info.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
                ReturnSpecialDirectories = false,
            }))
            {
                entries.Add(ToEntry(item));
            }

            return (IReadOnlyList<PcEntry>)entries;
        });
    }

    public static PcEntry Mkdir(string path)
    {
        var full = RequireFullPath(path);
        return Guard(full, () =>
        {
            if (Directory.Exists(full) || File.Exists(full))
            {
                throw new DeviceException(DeviceErrorCodes.AlreadyExists, "Something with that name is already in this folder.");
            }

            return ToEntry(Directory.CreateDirectory(full));
        });
    }

    /// <summary>Renames a file or folder in place; <paramref name="name"/> is the new name only.</summary>
    public static PcEntry Rename(string path, string name)
    {
        var full = RequireFullPath(path);
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed is "." or ".." || trimmed.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("A name can't be empty or contain \\ / : * ? \" < > |.");
        }

        var parent = System.IO.Path.GetDirectoryName(full.TrimEnd('\\'))
                     ?? throw new ArgumentException("A drive can't be renamed here.");
        var target = System.IO.Path.Combine(parent, trimmed);
        return Guard(full, () =>
        {
            var isDirectory = Directory.Exists(full);
            if (!isDirectory && !File.Exists(full))
            {
                throw new DeviceException(DeviceErrorCodes.PathNotFound, "That file or folder isn't on this PC any more.");
            }

            var caseOnly = string.Equals(full, target, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
            {
                throw new DeviceException(DeviceErrorCodes.AlreadyExists, "Something with that name is already in this folder.");
            }

            if (isDirectory)
            {
                Directory.Move(full, target);
                return ToEntry(new DirectoryInfo(target));
            }

            File.Move(full, target);
            return ToEntry(new FileInfo(target));
        });
    }

    public static string RequireFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("path must be a full path on this PC.");
        }

        return System.IO.Path.GetFullPath(path);
    }

    private static PcEntry ToEntry(FileSystemInfo item)
    {
        var isDirectory = item is DirectoryInfo;
        DateTimeOffset? modified = null;
        try
        {
            modified = new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        var attributes = item.Attributes;
        return new PcEntry(
            item.FullName,
            item.Name,
            isDirectory,
            item is FileInfo file ? file.Length : 0,
            modified,
            ReadOnly: (attributes & FileAttributes.ReadOnly) != 0 && !isDirectory,
            Hidden: (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0);
    }

    private static T Guard<T>(string path, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Windows doesn't allow that here.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That folder isn't on this PC any more.", ex);
        }
        catch (FileNotFoundException ex)
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That file isn't on this PC any more.", ex);
        }
        catch (IOException ex)
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, $"Windows couldn't do that: {ex.Message}", new IOException(path, ex));
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static string? DownloadsPath()
    {
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(DownloadsFolder, 0, 0, out var buffer) == 0)
        {
            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, nint token, out nint path);
}
