using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Protocol.Services;

/// <summary>
/// installation_proxy: Browse lists apps in batches until Status=Complete; Install and Uninstall
/// stream progress messages {Status, PercentComplete} until Complete or an Error.
/// </summary>
public static class InstallationProxyClient
{
    /// <summary>The attributes Browse returns per app. Sizes are only computed when asked for.</summary>
    public static readonly string[] BrowseAttributes =
    [
        "CFBundleIdentifier",
        "CFBundleDisplayName",
        "CFBundleName",
        "CFBundleShortVersionString",
        "CFBundleVersion",
        "ApplicationType",
        "SignerIdentity",
        "UIFileSharingEnabled",
        "StaticDiskUsage",
        "DynamicDiskUsage",
        "IsAppClip",
        "ProfileValidated",
    ];

    /// <summary>Enough to name apps and find the file-sharing ones, without the slow size calculation.</summary>
    public static readonly string[] LightAttributes =
    [
        "CFBundleIdentifier",
        "CFBundleDisplayName",
        "CFBundleName",
        "ApplicationType",
        "UIFileSharingEnabled",
    ];

    /// <param name="applicationType">"User", "System" or "Any".</param>
    /// <param name="attributes">Defaults to <see cref="BrowseAttributes"/>.</param>
    public static async Task<IReadOnlyList<PlistDict>> BrowseAsync(PlistConnection connection, string applicationType, CancellationToken ct,
        IReadOnlyList<string>? attributes = null)
    {
        await connection.SendAsync(new PlistDict
        {
            ["Command"] = "Browse",
            ["ClientOptions"] = new PlistDict
            {
                ["ApplicationType"] = applicationType,
                ["ReturnAttributes"] = (attributes ?? BrowseAttributes).Cast<object>().ToList(),
            },
        }, ct).ConfigureAwait(false);

        var apps = new List<PlistDict>();
        while (true)
        {
            var reply = await connection.ReceiveAsync(ct).ConfigureAwait(false);
            ThrowIfError(reply, "Browse");
            if (reply.GetArray("CurrentList") is { } batch)
                apps.AddRange(batch.OfType<PlistDict>());
            if (reply.GetString("Status") == "Complete")
                return apps;
        }
    }

    /// <summary>Installs a package already uploaded through AFC (e.g. "PublicStaging/app.ipa").</summary>
    public static Task InstallAsync(PlistConnection connection, string devicePackagePath, IProgress<(string Status, int? Percent)>? progress, CancellationToken ct) =>
        RunAsync(connection, new PlistDict
        {
            ["Command"] = "Install",
            ["PackagePath"] = devicePackagePath,
            ["ClientOptions"] = new PlistDict(),
        }, "Install", progress, ct);

    public static Task UninstallAsync(PlistConnection connection, string bundleId, IProgress<(string Status, int? Percent)>? progress, CancellationToken ct) =>
        RunAsync(connection, new PlistDict
        {
            ["Command"] = "Uninstall",
            ["ApplicationIdentifier"] = bundleId,
            ["ClientOptions"] = new PlistDict(),
        }, "Uninstall", progress, ct);

    private static async Task RunAsync(PlistConnection connection, PlistDict command, string name,
        IProgress<(string Status, int? Percent)>? progress, CancellationToken ct)
    {
        await connection.SendAsync(command, ct).ConfigureAwait(false);
        while (true)
        {
            var reply = await connection.ReceiveAsync(ct).ConfigureAwait(false);
            ThrowIfError(reply, name);
            var status = reply.GetString("Status");
            if (status is not null)
                progress?.Report((status, reply.GetInt("PercentComplete")));
            if (status == "Complete")
                return;
        }
    }

    private static void ThrowIfError(PlistDict reply, string command)
    {
        if (reply.GetString("Error") is not { } error)
            return;
        var detail = reply.GetString("ErrorDescription");
        var text = detail is null ? error : $"{error}: {detail}";
        throw command switch
        {
            "Install" => new DeviceException(DeviceErrorCodes.InstallFailed, $"The iPhone did not install the app ({text})."),
            "Uninstall" when error is "APIInternalError" or "ApplicationNotFound"
                => new DeviceException(DeviceErrorCodes.NotFound, $"The app is not installed ({text})."),
            _ => new DeviceException(DeviceErrorCodes.Protocol, $"installation_proxy {command} failed ({text})."),
        };
    }
}
