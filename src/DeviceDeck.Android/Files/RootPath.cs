using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Android.Files;

/// <summary>Root-relative paths: always start with '/', no trailing '/', never escape the root.</summary>
internal static class RootPath
{
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }
        if (path.Contains('\0') || path.Contains('\n') || path.Contains('\r'))
        {
            throw new DeviceException(DeviceErrorCodes.PathNotFound, "That path has characters Android can't store.");
        }
        var segments = new List<string>();
        foreach (string segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                throw new DeviceException(DeviceErrorCodes.PermissionDenied, "Paths can't climb out of the storage root.");
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    public static string ToDevice(string basePath, string normalizedRelative) =>
        normalizedRelative == "/" ? basePath : basePath + normalizedRelative;

    public static string Join(string normalizedDirectory, string name) =>
        normalizedDirectory == "/" ? "/" + name : normalizedDirectory + "/" + name;
}
