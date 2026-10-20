using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Transfers;

namespace DeviceDeck.Bridge.Methods;

// Params and results of docs/bridge-contract.md, "Methods". A member without a default is required.

public sealed record DeviceParams(string DeviceId);

public sealed record PhotosListParams(string DeviceId, int Offset, int Limit, string? AlbumId = null);

public sealed record PhotosExportParams(string DeviceId, IReadOnlyList<string> AssetIds, string Destination, bool ConvertHeic, string FolderPattern);

public sealed record PhotosDeleteParams(string DeviceId, IReadOnlyList<string> AssetIds);

public sealed record DeviceFilesParams(string DeviceId, IReadOnlyList<string> Files);

public sealed record FilesListParams(string DeviceId, string Root, string Path);

public sealed record FilesRenameParams(string DeviceId, string Root, string From, string To);

public sealed record FilesDeleteParams(string DeviceId, string Root, IReadOnlyList<string> Paths, bool Recursive);

public sealed record FilesCopyToPcParams(string DeviceId, string Root, IReadOnlyList<string> Paths, string Destination);

public sealed record FilesCopyToDeviceParams(string DeviceId, string Root, IReadOnlyList<string> Files, string Path);

public sealed record AppsListParams(string DeviceId, bool IncludeSystem = false);

public sealed record AppParams(string DeviceId, string AppId);

public sealed record AppsExportParams(string DeviceId, string AppId, string Destination);

public sealed record RebootParams(string DeviceId, RebootTarget Target = RebootTarget.System);

public sealed record JobParams(string JobId);

public sealed record ResolveConflictParams(string JobId, ConflictChoice Choice, bool ApplyToAll = false);

public sealed record PickFolderParams(string? Title = null);

public sealed record PathParams(string Path);

public sealed record DevStateParams(string DeviceId, DeviceState State);

public sealed record HelloResult(string Version, bool IsWebView);

public sealed record JobIdResult(string JobId);

public sealed record DeletedResult(int Deleted);

public sealed record UrlResult(string Url);

public sealed record PathResult(string? Path);

public sealed record DeviceRemovedEvent(string DeviceId);

public sealed record IndexProgressEvent(string DeviceId, int Done, int Total);

/// <summary>The contract's <c>{}</c> result.</summary>
public sealed record Empty
{
    public static readonly Empty Value = new();
}
