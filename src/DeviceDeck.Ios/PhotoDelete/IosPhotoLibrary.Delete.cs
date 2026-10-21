using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.Device.Photos;

// Round 4 (photodelete): what the delete through Windows reads from the library. Deleting itself goes through
// IosDevice.DeleteThroughWindowsAsync; nothing here touches DCIM.

public sealed partial class IosPhotoLibrary
{
    /// <summary>The index as last built, without building one.</summary>
    internal PhotoIndex? CachedIndex => _index;

    /// <summary>Every media file in /DCIM/*/ as "DCIM/&lt;folder&gt;/&lt;name&gt;", listed now.</summary>
    internal static async Task<IReadOnlyList<string>> ListDcimFilesAsync(AfcClient afc, CancellationToken ct) =>
        await ListDcimAsync(afc, ct).ConfigureAwait(false);
}
