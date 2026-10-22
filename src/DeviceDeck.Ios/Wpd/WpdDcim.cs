using DeviceDeck.Ios.Device.Photos;

namespace DeviceDeck.Ios.Wpd;

/// <summary>One folder under DCIM as Windows lists it, with its files.</summary>
public sealed record WpdFolder(string Id, string Name, IReadOnlyList<WpdObject> Files);

/// <summary>Internal Storage › DCIM as Windows lists it.</summary>
/// <param name="StorageCount">Storages Windows shows for the phone (0 while it is locked).</param>
public sealed record WpdDcim(string StorageId, string? StorageName, string DcimId, IReadOnlyList<WpdFolder> Folders, int StorageCount);

/// <summary>Walks This PC › Apple iPhone › Internal Storage › DCIM. Never looks anywhere else.</summary>
public static class WpdDcimWalker
{
    /// <summary>Why <see cref="FindDcim"/> found nothing.</summary>
    public enum Missing { None, NoStorage, EmptyStorage, NoDcim }

    /// <summary>
    /// The storage that holds the camera roll and the id of the object its folders hang off: DCIM, or the storage itself
    /// when the phone shows its camera-roll folders straight under Internal Storage (seen on iOS 26: "202510_a" with no
    /// DCIM level). Null, with why, when Windows shows no storage or no camera roll (a locked or untrusted phone).
    /// </summary>
    public static (string StorageId, string? StorageName, string DcimId, int StorageCount)? FindDcim(IWpdConnection c, CancellationToken ct, out Missing missing)
    {
        missing = Missing.NoStorage;
        var storages = c.ChildIds(WpdIds.DeviceObjectId, ct)
            .Select(c.Get)
            .Where(o => o is { Kind: WpdObjectKind.Functional })
            .Select(o => o!)
            .ToList();
        foreach (var storage in storages)
        {
            ct.ThrowIfCancellationRequested();
            var children = c.ChildIds(storage.Id, ct).Select(c.Get).Where(o => o is not null).Select(o => o!).ToList();
            if (children.Count == 0)
            {
                missing = missing == Missing.NoStorage ? Missing.EmptyStorage : missing;
                continue;
            }

            if (children.FirstOrDefault(o => o.Kind == WpdObjectKind.Folder && string.Equals(o.FileName, "DCIM", StringComparison.OrdinalIgnoreCase)) is { } dcim)
            {
                missing = Missing.None;
                return (storage.Id, storage.FileName, dcim.Id, storages.Count);
            }

            if (children.All(o => o.Kind == WpdObjectKind.Folder && IsCameraFolderName(o.FileName)))
            {
                missing = Missing.None;
                return (storage.Id, storage.FileName, storage.Id, storages.Count);
            }

            missing = Missing.NoDcim;
        }

        return null;
    }

    /// <summary>A camera-roll folder name: eight characters starting with three digits (100APPLE, 202510_a, 2021__06).</summary>
    public static bool IsCameraFolderName(string? name) =>
        name is { Length: 8 } && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) && char.IsAsciiDigit(name[2]);

    /// <summary>The camera-roll folders under DCIM (100APPLE…, or the month folders recent iOS shows the PC).</summary>
    public static IReadOnlyList<WpdObject> Folders(IWpdConnection c, string dcimId, CancellationToken ct) =>
        c.ChildIds(dcimId, ct).Select(c.Get).Where(o => o is { Kind: WpdObjectKind.Folder }).Select(o => o!).ToList();

    /// <summary>The files in one DCIM folder (at most <paramref name="limit"/>); sub-folders are left out.</summary>
    public static IReadOnlyList<WpdObject> Files(IWpdConnection c, string folderId, CancellationToken ct, int limit = int.MaxValue)
    {
        var files = new List<WpdObject>();
        foreach (var id in c.ChildIds(folderId, ct))
        {
            if (files.Count >= limit)
            {
                break;
            }

            ct.ThrowIfCancellationRequested();
            if (c.Get(id) is { Kind: WpdObjectKind.File } file)
            {
                files.Add(file);
            }
        }

        return files;
    }

    /// <summary>All of DCIM with every file, or null when Windows shows no DCIM.</summary>
    public static WpdDcim? Walk(IWpdConnection c, CancellationToken ct, out Missing missing)
    {
        if (FindDcim(c, ct, out missing) is not { } found)
        {
            return null;
        }

        var folders = Folders(c, found.DcimId, ct)
            .Select(f => new WpdFolder(f.Id, f.FileName ?? "", Files(c, f.Id, ct)))
            .ToList();
        return new WpdDcim(found.StorageId, found.StorageName, found.DcimId, folders, found.StorageCount);
    }
}

public enum WpdMapProblem
{
    None,
    /// <summary>Not a file directly in a DCIM folder.</summary>
    NotDcim,
    /// <summary>Windows lists no folder of that name (the phone shows the PC its own folders).</summary>
    NoFolder,
    /// <summary>Windows doesn't list the file.</summary>
    Absent,
    /// <summary>More than one object could be it.</summary>
    Ambiguous,
    SizeDiffers,
    /// <summary>The object is a folder or something else that is not a file.</summary>
    NotAFile,
    /// <summary>The phone marks it read-only for the PC.</summary>
    ReadOnly,
}

/// <param name="Object">The one object it maps to; null with a problem.</param>
public sealed record WpdMapResult(WpdObject? Object, WpdMapProblem Problem, string Message)
{
    public bool Ok => Problem == WpdMapProblem.None;
}

/// <summary>A photo-library asset as the renamed match needs it: its file, its size and when it was taken.</summary>
public sealed record WpdPhoneAsset(string Path, long Size, DateTimeOffset? Taken);

/// <summary>
/// Maps the phone's DCIM paths ("DCIM/100APPLE/IMG_0001.HEIC", the asset ids) to the objects Windows lists, exactly
/// or not at all. When Windows shows the phone's own folders, a file maps by folder + name + size. When it shows other
/// folders (month folders), by name + size, and only when the name is unique both on the phone and in Windows' list.
/// A picture saved into Photos from elsewhere is shown to the PC under a name of the phone's making ("PZHI6539.PNG" for
/// IMG_7414.PNG, seen on iOS 27): it maps by extension + size + the time it was taken, when exactly one object and
/// exactly one asset share those, and that object's name is no file of the phone's.
/// </summary>
public sealed class WpdDcimMap
{
    private readonly WpdDcim _dcim;
    private readonly Dictionary<string, int> _phoneNames;
    private readonly IReadOnlyCollection<WpdPhoneAsset>? _phoneAssets;

    /// <summary>How far apart the phone's "taken" and Windows' "created" may be: PTP dates are whole seconds.</summary>
    private static readonly TimeSpan SameTime = TimeSpan.FromSeconds(1);

    /// <param name="phoneFiles">Every media file in the phone's DCIM (AFC), as "DCIM/&lt;folder&gt;/&lt;name&gt;".</param>
    /// <param name="phoneAssets">The library's assets with known sizes, for the renamed match; null turns it off.</param>
    /// <param name="folderNames">Every folder Windows lists, when <paramref name="dcim"/> holds only some of them (a narrowed walk).</param>
    public WpdDcimMap(WpdDcim dcim, IReadOnlyCollection<string> phoneFiles, IReadOnlyCollection<WpdPhoneAsset>? phoneAssets = null,
        IReadOnlyCollection<string>? folderNames = null)
    {
        _dcim = dcim;
        _phoneAssets = phoneAssets;
        _phoneNames = phoneFiles
            .GroupBy(f => f[(f.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        ByFolder = ShowsPhoneFolders(folderNames ?? dcim.Folders.Select(f => f.Name).ToList(), phoneFiles);
    }

    /// <summary>Windows lists at least one of the phone's own DCIM folder names (100APPLE), so folders take part in the match.</summary>
    public static bool ShowsPhoneFolders(IReadOnlyCollection<string> windowsFolders, IReadOnlyCollection<string> phoneFiles)
    {
        var phoneFolders = phoneFiles.Select(FolderOf).Where(f => f is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return windowsFolders.Any(phoneFolders.Contains);
    }

    /// <summary>Windows shows the phone's own folder names, so folders take part in the match.</summary>
    public bool ByFolder { get; }

    public WpdDcim Dcim => _dcim;

    /// <param name="takenAt">When the library says it was taken; null (or no assets given) keeps to names.</param>
    public WpdMapResult Find(string path, long diskSize, DateTimeOffset? takenAt = null)
    {
        var parts = path.Split('/');
        if (parts.Length != 3 || !parts[0].Equals("DCIM", StringComparison.OrdinalIgnoreCase)
            || parts.Any(p => p is "" or "." or "..") || !PhotoIndex.IsMediaFile(parts[2]))
        {
            return Problem(WpdMapProblem.NotDcim, "It isn't a file in the camera roll's DCIM folders.");
        }

        var (folderName, name) = (parts[1], parts[2]);
        WpdObject candidate;
        if (ByFolder)
        {
            var folders = _dcim.Folders.Where(f => f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (folders.Count == 0)
            {
                return Problem(WpdMapProblem.NoFolder, $"Windows doesn't list the folder {folderName}.");
            }

            var named = folders.SelectMany(f => f.Files).Where(o => Named(o, name)).ToList();
            if (named.Count == 0)
            {
                return Renamed(name, diskSize, takenAt) ?? Problem(WpdMapProblem.Absent, $"Windows doesn't list {folderName}/{name}.");
            }

            if (named.Count > 1)
            {
                return Problem(WpdMapProblem.Ambiguous, $"Windows lists {named.Count} files called {folderName}/{name}.");
            }

            candidate = named[0];
            if (candidate.Size != diskSize)
            {
                return SizeDiffers(candidate, diskSize);
            }
        }
        else
        {
            if (_phoneNames.GetValueOrDefault(name) != 1)
            {
                return Problem(WpdMapProblem.Ambiguous,
                    $"The phone has {_phoneNames.GetValueOrDefault(name)} files called {name}, and Windows shows other folders than the phone, so it can't tell them apart.");
            }

            var named = _dcim.Folders.SelectMany(f => f.Files).Where(o => Named(o, name)).ToList();
            if (named.Count == 0)
            {
                return Renamed(name, diskSize, takenAt) ?? Problem(WpdMapProblem.Absent, $"Windows doesn't list {name}.");
            }

            var sized = named.Where(o => o.Size == diskSize).ToList();
            if (sized.Count == 0)
            {
                return SizeDiffers(named[0], diskSize);
            }

            if (sized.Count > 1 || named.Count > 1)
            {
                return Problem(WpdMapProblem.Ambiguous, $"Windows lists {named.Count} files called {name}.");
            }

            candidate = sized[0];
        }

        return Checked(candidate, name, "");
    }

    /// <summary>A file Windows may delete: a file, and not marked read-only (a phone that doesn't say is asked by the delete itself).</summary>
    private static WpdMapResult Checked(WpdObject candidate, string name, string note)
    {
        if (candidate.Kind != WpdObjectKind.File)
        {
            return Problem(WpdMapProblem.NotAFile, $"Windows lists {name} as something other than a file.");
        }

        if (candidate.CanDelete == false)
        {
            return new WpdMapResult(null, WpdMapProblem.ReadOnly, $"The iPhone marks {name} as read-only for this PC.");
        }

        return new WpdMapResult(candidate, WpdMapProblem.None, note);
    }

    /// <summary>
    /// The object Windows shows under a name of the phone's making: same extension, same size, created when the asset
    /// was taken, a name that is no file of the phone's, and the only one on both sides. Null when there is none.
    /// </summary>
    private WpdMapResult? Renamed(string name, long diskSize, DateTimeOffset? takenAt)
    {
        if (takenAt is not { } taken || _phoneAssets is null)
        {
            return null;
        }

        var ext = Path.GetExtension(name);
        bool Same(DateTimeOffset? t) => t is { } x && (x - taken).Duration() <= SameTime;
        var objects = _dcim.Folders.SelectMany(f => f.Files)
            .Where(o => o.Size == diskSize && Same(o.Created)
                        && string.Equals(Path.GetExtension(o.FileName), ext, StringComparison.OrdinalIgnoreCase)
                        && _phoneNames.GetValueOrDefault(o.FileName ?? "") == 0)
            .ToList();
        if (objects.Count == 0)
        {
            return null;
        }

        var twins = _phoneAssets.Count(a => a.Size == diskSize && Same(a.Taken)
                                            && string.Equals(Path.GetExtension(a.Path), ext, StringComparison.OrdinalIgnoreCase));
        if (objects.Count > 1 || twins != 1)
        {
            return Problem(WpdMapProblem.Ambiguous,
                $"Windows doesn't list {name} by name; {objects.Count} objects and {twins} phone files share its size, type and time.");
        }

        return Checked(objects[0], name, $"Windows calls it {objects[0].FileName}.");
    }

    /// <summary>The video Windows lists beside a still under the still's name ("PZHI6539.MOV" beside "PZHI6539.HEIC"), at that size.</summary>
    public WpdObject? CompanionOf(WpdObject still, string companionPath, long size)
    {
        var name = Path.GetFileNameWithoutExtension(still.FileName ?? "") + Path.GetExtension(companionPath);
        var found = FolderOf(still)?.Files.Where(o => Named(o, name) && o.Size == size).ToList();
        return found is { Count: 1 } && found[0].CanDelete != false ? found[0] : null;
    }

    /// <summary>
    /// What Windows lists beside an edited photo in its folder: the rendered edit ("IMG_E0001.JPG" beside "IMG_0001.HEIC")
    /// and the edit's sidecar ("IMG_0001.AAE", "IMG_O0001.AAE"). These aren't files in the phone's DCIM; the phone makes them for the PC.
    /// </summary>
    public IReadOnlyList<WpdObject> EditsOf(WpdObject still)
    {
        var stem = Path.GetFileNameWithoutExtension(still.FileName ?? "");
        var cut = stem.IndexOf('_');
        if (cut < 0 || FolderOf(still) is not { } folder)
        {
            return [];
        }

        string[] renders = [stem[..(cut + 1)] + "E" + stem[(cut + 1)..]];
        string[] sidecars = [stem, stem[..(cut + 1)] + "O" + stem[(cut + 1)..]];
        return folder.Files.Where(o => o.Id != still.Id && o.FileName is { } n && (
                renders.Contains(Path.GetFileNameWithoutExtension(n), StringComparer.OrdinalIgnoreCase)
                || (Path.GetExtension(n).Equals(".AAE", StringComparison.OrdinalIgnoreCase)
                    && sidecars.Contains(Path.GetFileNameWithoutExtension(n), StringComparer.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>The DCIM folder object a file is in, by the walk.</summary>
    public WpdFolder? FolderOf(WpdObject file) => _dcim.Folders.FirstOrDefault(f => f.Files.Any(o => o.Id == file.Id));

    private static bool Named(WpdObject o, string name) => string.Equals(o.FileName, name, StringComparison.OrdinalIgnoreCase);

    private static string? FolderOf(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 3 ? parts[1] : null;
    }

    private static WpdMapResult Problem(WpdMapProblem problem, string message) => new(null, problem, message);

    private static WpdMapResult SizeDiffers(WpdObject o, long diskSize) =>
        Problem(WpdMapProblem.SizeDiffers, $"Windows lists {o.FileName} at {o.Size?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "no"} bytes; the phone's file is {diskSize.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bytes.");
}
