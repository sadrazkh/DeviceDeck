using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Services;

/// <summary>springboardservices. Note the lower-case "command" key this service uses.</summary>
public static class SpringBoardClient
{
    /// <summary>The app's home-screen icon as PNG, or null when SpringBoard has none.</summary>
    public static async Task<byte[]?> GetIconPngDataAsync(PlistConnection connection, string bundleId, CancellationToken ct)
    {
        var reply = await connection.RequestAsync(new PlistDict
        {
            ["command"] = "getIconPNGData",
            ["bundleId"] = bundleId,
        }, ct).ConfigureAwait(false);
        var png = reply.GetData("pngData");
        return png is { Length: > 0 } ? png : null;
    }
}
