using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol.Afc;

namespace DeviceDeck.Ios.Protocol.Services;

/// <summary>
/// house_arrest: one plist command, then the same socket speaks AFC rooted in the app's container.
/// VendDocuments only works for apps with UIFileSharingEnabled and exposes /Documents.
/// </summary>
public static class HouseArrestClient
{
    public static async Task<AfcClient> VendDocumentsAsync(PlistConnection connection, string bundleId, CancellationToken ct)
    {
        var reply = await connection.RequestAsync(new PlistDict
        {
            ["Command"] = "VendDocuments",
            ["Identifier"] = bundleId,
        }, ct).ConfigureAwait(false);

        if (reply.GetString("Error") is { } error)
        {
            throw error switch
            {
                "ApplicationLookupFailed" or "InstallationLookupFailed" =>
                    new DeviceException(DeviceErrorCodes.NotFound, $"No app '{bundleId}' on the phone (house_arrest: {error})."),
                "NoFileSharing" or "VendDocumentsFailed" =>
                    new DeviceException(DeviceErrorCodes.PermissionDenied, $"'{bundleId}' does not share its documents (house_arrest: {error})."),
                _ => new DeviceException(DeviceErrorCodes.Protocol, $"house_arrest refused '{bundleId}': {error}."),
            };
        }
        if (reply.GetString("Status") != "Complete")
            throw new FormatException($"house_arrest answered without Status=Complete for '{bundleId}'.");
        return new AfcClient(connection.Detach());
    }
}
