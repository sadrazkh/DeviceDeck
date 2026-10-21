namespace DeviceDeck.Core.Photos;

/// <summary>Free and total bytes of the drive a folder is on.</summary>
public sealed record PcDiskSpace(long FreeBytes, long TotalBytes);

/// <summary>
/// Room for a backup on the destination's drive. <see cref="NeededBytes"/> is what the new photos take (sizes the phone
/// reported, an estimate for the rest, HEIC counted bigger when it becomes JPG) plus a margin.
/// </summary>
/// <param name="Root">The drive ("E:\") or share the destination is on.</param>
/// <param name="FreeBytes">Null when the drive isn't there, or can't say (a network share may not).</param>
/// <param name="NearlyFull">Under <see cref="PhotoBackupSpaces.NearlyFullBytes"/> left, or under a tenth of the drive.</param>
/// <param name="DriveMissing">The destination's drive (or share) isn't there: an unplugged disk, a share that is offline.</param>
public sealed record PhotoBackupSpace(string Root, long? FreeBytes, long? TotalBytes, long NeededBytes, bool Enough, bool NearlyFull, bool DriveMissing);

public static class PhotoBackupSpaces
{
    /// <summary>Kept free beyond the photos themselves: at least 1 GB, or a tenth of what is copied.</summary>
    public const long MinimumMargin = 1L << 30;

    public const long NearlyFullBytes = 20L * 1_000_000_000;

    /// <summary>A JPEG made from a HEIC is about 60% bigger.</summary>
    public const double JpegGrowth = 1.6;

    public static long Margin(long bytes) => Math.Max(MinimumMargin, bytes / 10);

    /// <summary>The drive of <paramref name="folder"/> through <see cref="DriveInfo"/>; null when it isn't there or can't say.</summary>
    public static PcDiskSpace? Probe(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null; // a share: Windows answers for drives only here
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? new PcDiskSpace(drive.AvailableFreeSpace, drive.TotalSize) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>True when the folder's drive or share is there (the folder itself may still have to be made).</summary>
    public static bool RootExists(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The space verdict for copying <paramref name="bytes"/> into <paramref name="folder"/>; 0 bytes needs no room.</summary>
    public static PhotoBackupSpace Check(string folder, long bytes, Func<string, PcDiskSpace?> probe, Func<string, bool> rootExists)
    {
        var root = SafeRoot(folder);
        var missing = !rootExists(folder);
        var space = missing ? null : probe(folder);
        var needed = bytes <= 0 ? 0 : bytes + Margin(bytes);
        var nearlyFull = space is { } s && (s.FreeBytes < NearlyFullBytes || (s.TotalBytes > 0 && s.FreeBytes < s.TotalBytes / 10));
        var enough = !missing && (space is null || needed == 0 || space.FreeBytes >= needed);
        return new PhotoBackupSpace(root, space?.FreeBytes, space?.TotalBytes, needed, enough, nearlyFull, missing);
    }

    private static string SafeRoot(string folder)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
        }
        catch (ArgumentException)
        {
            return folder;
        }
    }
}
