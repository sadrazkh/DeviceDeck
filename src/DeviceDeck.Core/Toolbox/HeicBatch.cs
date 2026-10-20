namespace DeviceDeck.Core.Toolbox;

/// <param name="Source">A HEIC file on the PC.</param>
/// <param name="Destination">The JPEG to write; never an existing file.</param>
public sealed record HeicItem(string Source, string Destination, long Size);

/// <summary>Turns dropped files and folders into HEIC → JPEG conversions. Originals are never touched.</summary>
public static class HeicBatch
{
    public static readonly IReadOnlyList<string> Extensions = [".heic", ".heif", ".hif"];

    public static bool IsHeic(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every HEIC file in <paramref name="inputs"/> (folders searched recursively), each with a free .jpg name:
    /// next to the original when <paramref name="destinationFolder"/> is null, else inside it (a dropped folder
    /// keeps its own sub-folders there).
    /// </summary>
    public static List<HeicItem> Plan(IEnumerable<string> inputs, string? destinationFolder)
    {
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<HeicItem>();

        void Add(string file, string targetFolder)
        {
            var full = Path.GetFullPath(file);
            if (!seen.Add(full))
            {
                return;
            }

            var stem = Path.GetFileNameWithoutExtension(full);
            var destination = FreeName(targetFolder, stem, planned);
            planned.Add(destination);
            items.Add(new HeicItem(full, destination, new FileInfo(full).Length));
        }

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                var root = Path.GetFullPath(input).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(IsHeic)
                    .Order(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var relative = Path.GetRelativePath(root, Path.GetDirectoryName(file)!);
                    var target = destinationFolder is null
                        ? Path.GetDirectoryName(file)!
                        : Path.GetFullPath(Path.Combine(destinationFolder, Path.GetFileName(root), relative));
                    Add(file, target);
                }
            }
            else if (File.Exists(input) && IsHeic(input))
            {
                Add(input, destinationFolder ?? Path.GetDirectoryName(Path.GetFullPath(input))!);
            }
        }

        return items;
    }

    /// <summary>"IMG_0001.jpg", or "IMG_0001 (2).jpg" when that is taken on disk or by an earlier item.</summary>
    public static string FreeName(string folder, string stem, IReadOnlySet<string> planned)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? stem + ".jpg" : $"{stem} ({n}).jpg";
            var path = Path.Combine(folder, name);
            if (!planned.Contains(path) && !File.Exists(path))
            {
                return path;
            }
        }
    }
}
