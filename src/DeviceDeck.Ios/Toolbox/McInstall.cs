using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;

namespace DeviceDeck.Ios.Toolbox;

/// <summary>
/// com.apple.mobile.MCInstall, the service Apple Configurator uses for profiles. On a phone that isn't supervised, InstallProfile
/// only hands the profile over: the user still has to review and install it in Settings.
/// </summary>
public static class McInstall
{
    public const string ServiceName = "com.apple.mobile.MCInstall";

    /// <summary>The service answers at once; a silent phone shouldn't hold the tool's spinner for ever.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<InstalledProfile>> ListAsync(PlistConnection connection, CancellationToken ct)
    {
        var reply = await RequestAsync(connection, new PlistDict { ["RequestType"] = "GetProfileList" }, ct).ConfigureAwait(false);
        var metadata = reply.GetDict("ProfileMetadata");
        if (metadata is null)
        {
            return [];
        }

        // OrderedIdentifiers is the order Settings shows them in.
        var order = reply.GetArray("OrderedIdentifiers")?.OfType<string>().Where(metadata.ContainsKey).ToList() ?? [];
        return order.Concat(metadata.Keys.Except(order, StringComparer.Ordinal))
            .Select(id =>
            {
                var profile = metadata.GetDict(id);
                return new InstalledProfile(id, profile?.GetString("PayloadDisplayName") ?? id, profile?.GetString("PayloadOrganization"));
            })
            .ToList();
    }

    public static Task SendAsync(PlistConnection connection, byte[] profile, CancellationToken ct) =>
        RequestAsync(connection, new PlistDict { ["RequestType"] = "InstallProfile", ["Payload"] = profile }, ct);

    private static async Task<PlistDict> RequestAsync(PlistConnection connection, PlistDict request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        PlistDict reply;
        try
        {
            reply = await connection.RequestAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DeviceException(DeviceErrorCodes.Protocol, "The iPhone didn't answer about its profiles.");
        }

        if (reply.GetString("Status") == "Acknowledged")
        {
            return reply;
        }

        var reason = reply.GetArray("ErrorChain")?.OfType<PlistDict>().Select(e => e.GetString("LocalizedDescription")).FirstOrDefault(d => d is not null);
        throw new DeviceException(DeviceErrorCodes.InstallFailed, reason ?? "The iPhone refused the request.");
    }
}
