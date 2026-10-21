using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Imaging;

namespace DeviceDeck.Core.Photos;

/// <summary>Edited iPhone photos: which edited versions a backup copies, and what they are called on the PC.</summary>
public static class PhotoBackupEdits
{
    public const string EditedSuffix = " (edited)";

    /// <summary>
    /// The asset's edited version is to be copied: it was edited, and the PC has no copy of this edit (a new photo, or one
    /// the ledger knows by its key with another edit or none). A photo the ledger knows only by path or name is left alone.
    /// </summary>
    public static bool Wanted(PhotoBackupAsset asset, PhotoBackupKnownSet known) =>
        asset.Edited is { } edit && (!known.Contains(asset) || (known.TryGetCopy(asset.Key, out var copy) && copy.EditStamp != edit.Stamp));

    /// <summary>"IMG_1234.HEIC" + "FullSizeRender.heic" → "IMG_1234 (edited).heic" next to it (".jpg" when HEIC becomes JPG).</summary>
    public static string EditedPath(string stillPath, string renderName, bool convertHeic)
    {
        var ext = Path.GetExtension(renderName);
        if (convertHeic && ImageFormats.IsHeic(renderName))
        {
            ext = ".jpg";
        }

        return Path.Combine(Path.GetDirectoryName(stillPath)!, Path.GetFileNameWithoutExtension(stillPath) + EditedSuffix + ext);
    }
}
