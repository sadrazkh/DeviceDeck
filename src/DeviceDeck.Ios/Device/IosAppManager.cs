using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;
using DeviceDeck.Ios.Protocol;
using DeviceDeck.Ios.Protocol.Afc;
using DeviceDeck.Ios.Protocol.Services;

namespace DeviceDeck.Ios.Device;

/// <summary>Apps through installation_proxy; icons through springboardservices; .ipa upload through AFC PublicStaging.</summary>
internal sealed class IosAppManager(IosDevice device) : IAppManager, IAsyncDisposable
{
    private const string StagingFolder = "/PublicStaging";

    // One springboardservices connection for all icons; requests on it are strictly one at a time.
    private readonly SharedConnection<PlistConnection> _springboard = new(
        ct => device.Connector().StartServiceAsync(ServiceNames.SpringBoard, ct), c => c.IsBroken, serialize: true);

    public Task<IReadOnlyList<AppInfo>> ListAsync(bool includeSystem, CancellationToken ct) => device.UseAsync<IReadOnlyList<AppInfo>>(async c =>
    {
        await using var proxy = await c.StartServiceAsync(ServiceNames.InstallationProxy, ct).ConfigureAwait(false);
        var apps = await InstallationProxyClient.BrowseAsync(proxy, includeSystem ? "Any" : "User", ct).ConfigureAwait(false);
        return apps
            .Where(a => a.GetString("CFBundleIdentifier") is not null)
            .Where(a => a.GetString("ApplicationType") is null or "User" or "System")
            .Where(a => a.GetBool("IsAppClip") != true)
            .Select(ToAppInfo)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    });

    public Task<byte[]?> GetIconAsync(string appId, CancellationToken ct) =>
        device.UseAsync(_ => _springboard.UseAsync(sb => SpringBoardClient.GetIconPngDataAsync(sb, appId, ct), ct));

    public ValueTask DisposeAsync() => _springboard.DisposeAsync();

    public Task InstallAsync(IReadOnlyList<string> localPaths, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        if (localPaths.Count != 1 || !localPaths[0].EndsWith(".ipa", StringComparison.OrdinalIgnoreCase))
            return Task.FromException(new DeviceException(DeviceErrorCodes.InstallFailed, "An iPhone app is installed from exactly one .ipa file."));
        var local = localPaths[0];
        if (!File.Exists(local))
            return Task.FromException(new DeviceException(DeviceErrorCodes.PathNotFound, $"'{local}' does not exist."));

        return device.UseAsync(async c =>
        {
            var staged = $"{StagingFolder}/devicedeck-{Guid.NewGuid():N}.ipa";
            await using (var afc = await c.StartAfcAsync(ct).ConfigureAwait(false))
            {
                if (await afc.GetFileInfoAsync(StagingFolder, ct).ConfigureAwait(false) is null)
                    await afc.MakeDirectoryAsync(StagingFolder, ct).ConfigureAwait(false);
                await using var file = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                var length = file.Length;
                progress?.Report(new InstallProgress("upload", 0));
                await afc.WriteFileAsync(staged, file, length,
                    new SyncProgress<long>(done => progress?.Report(new InstallProgress("upload", length == 0 ? 100 : Math.Round(100.0 * done / length, 1)))),
                    ct).ConfigureAwait(false);
            }

            try
            {
                await using var proxy = await c.StartServiceAsync(ServiceNames.InstallationProxy, ct).ConfigureAwait(false);
                await InstallationProxyClient.InstallAsync(proxy, staged.TrimStart('/'),
                    new SyncProgress<(string Status, int? Percent)>(p => progress?.Report(new InstallProgress(StageName(p.Status), p.Percent))),
                    ct).ConfigureAwait(false);
                progress?.Report(new InstallProgress("complete", 100));
            }
            finally
            {
                await RemoveStagedQuietlyAsync(c, staged).ConfigureAwait(false);
            }
        });
    }

    public Task UninstallAsync(string appId, CancellationToken ct) => device.UseAsync(async c =>
    {
        await using var proxy = await c.StartServiceAsync(ServiceNames.InstallationProxy, ct).ConfigureAwait(false);
        await InstallationProxyClient.UninstallAsync(proxy, appId, null, ct).ConfigureAwait(false);
    });

    public Task<IReadOnlyList<string>> ExportAsync(string appId, string destinationFolder, CancellationToken ct) =>
        Task.FromException<IReadOnlyList<string>>(IosErrors.NotSupported("iPhone apps cannot be copied off the phone."));

    internal static AppInfo ToAppInfo(PlistDict a)
    {
        var type = a.GetString("ApplicationType");
        var signer = a.GetString("SignerIdentity") ?? "";
        var source = type == "System" ? AppSource.System
            : signer.Contains("Developer", StringComparison.OrdinalIgnoreCase)
              || signer.Contains("Development", StringComparison.OrdinalIgnoreCase)
              || signer.Contains("Distribution", StringComparison.OrdinalIgnoreCase)
              || a.GetBool("ProfileValidated") == true
                ? AppSource.Sideloaded
                : AppSource.Store;
        return new AppInfo(
            Id: a.GetString("CFBundleIdentifier")!,
            Name: DisplayName(a),
            Version: a.GetString("CFBundleShortVersionString") ?? a.GetString("CFBundleVersion") ?? "",
            AppBytes: a.GetLong("StaticDiskUsage") ?? 0,
            DataBytes: a.GetLong("DynamicDiskUsage") ?? 0,
            Source: source,
            FileSharing: a.GetBool("UIFileSharingEnabled") == true,
            InstalledAt: null);
    }

    internal static string DisplayName(PlistDict a) =>
        a.GetString("CFBundleDisplayName") is { Length: > 0 } d ? d
        : a.GetString("CFBundleName") is { Length: > 0 } n ? n
        : a.GetString("CFBundleIdentifier") ?? "?";

    /// <summary>"InstallingApplication" → "installingApplication", matching the bridge's camelCase.</summary>
    private static string StageName(string status) =>
        status.Length == 0 ? status : char.ToLowerInvariant(status[0]) + status[1..];

    private static async Task RemoveStagedQuietlyAsync(DeviceConnector c, string staged)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var afc = await c.StartAfcAsync(cts.Token).ConfigureAwait(false);
            if (await afc.GetFileInfoAsync(staged, cts.Token).ConfigureAwait(false) is not null)
                await afc.RemovePathAsync(staged, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DeviceException or IOException or OperationCanceledException)
        {
            // installation_proxy usually consumes the file; a leftover is harmless.
        }
    }

    /// <summary>Reports on the caller's thread, so stages arrive in order.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
