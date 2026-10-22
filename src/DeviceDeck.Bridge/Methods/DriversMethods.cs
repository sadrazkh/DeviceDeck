using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Bridge.Methods;

// docs/bridge-contract.md, "Drivers card (v0.6)": starting a driver (adb's server; Apple Mobile Device Service through
// Windows' administrator prompt), downloading Google's platform-tools, and the few web pages the card links to.

public sealed record DriverParams(string ProviderId);

public sealed record DriverLinkParams(string Link);

/// <summary>Opens a link outside the app (the default browser, the Microsoft Store). The Shell implements it.</summary>
public interface ILinkOpener
{
    /// <summary>Opens <paramref name="uri"/>; throws when Windows can't (no app for that kind of link).</summary>
    void Open(Uri uri);
}

/// <summary>
/// The only links drivers.openLink opens, by name: the page can't ask for any other address.
/// Apple Devices' Store id 9NP83LWLPZ9K was checked on apps.microsoft.com (2026-10-22: "Apple Devices", free).
/// </summary>
public static class DriverLinks
{
    public static IReadOnlyDictionary<string, Uri> All { get; } = new Dictionary<string, Uri>(StringComparer.Ordinal)
    {
        ["appleDevicesStore"] = new("ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K"),
        ["appleDevicesWeb"] = new("https://apps.microsoft.com/detail/9NP83LWLPZ9K"),
        ["itunes"] = new("https://www.apple.com/itunes/"),
        ["samsungUsbDriver"] = new("https://developer.samsung.com/android-usb-driver"),
        ["googleUsbDriver"] = new("https://developer.android.com/studio/run/win-usb"),
        ["oemUsbDrivers"] = new("https://developer.android.com/studio/run/oem-usb"),
        ["platformToolsLicense"] = new("https://developer.android.com/studio/terms"),
    };

    /// <summary>Where a link that can't open goes instead: the Store page on the web when the Store app isn't there.</summary>
    public static IReadOnlyDictionary<string, string> Fallback { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["appleDevicesStore"] = "appleDevicesWeb",
    };
}

public static class DriversMethods
{
    public static void Register(JsonRpcDispatcher rpc, BridgeServices s)
    {
        rpc.Register<DriverParams, IReadOnlyList<DriverStatus>>("drivers.start", (p, ct) => s.Hub.StartDriverAsync(p.ProviderId, ct));
        rpc.Register<DriverParams, DriverDownloadStatus>("drivers.download", (p, _) => Task.FromResult(Download(s, p.ProviderId).StartDownload()));
        rpc.Register<DriverParams, DriverDownloadStatus>("drivers.downloadStatus", (p, _) => Task.FromResult(Download(s, p.ProviderId).DownloadStatus));
        rpc.Register<DriverParams, Empty>("drivers.cancelDownload", (p, _) =>
        {
            Download(s, p.ProviderId).CancelDownload();
            return Task.FromResult(Empty.Value);
        });
        rpc.Register<DriverLinkParams, Empty>("drivers.openLink", (p, _) =>
        {
            if (!DriverLinks.All.TryGetValue(p.Link ?? "", out var uri))
            {
                throw new ArgumentException($"There is no link called \"{p.Link}\".");
            }

            var links = s.Links ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "Links can't be opened here.");
            try
            {
                links.Open(uri);
            }
            catch (Exception ex) when (ex is not DeviceException && DriverLinks.Fallback.TryGetValue(p.Link!, out var other))
            {
                // No Microsoft Store app (Windows LTSC, Store removed): its page in the browser instead.
                links.Open(DriverLinks.All[other]);
            }

            return Task.FromResult(Empty.Value);
        });
    }

    /// <summary>drivers.download: each download's progress, as it goes and when it ends.</summary>
    public static IDisposable PublishEvents(BridgeServices s, Action<string, object> publish)
    {
        var downloads = s.Hub.Providers.OfType<IDriverDownload>().ToList();
        void OnDownload(object? sender, DriverDownloadStatus status) => publish("drivers.download", status);
        foreach (var download in downloads)
        {
            download.DownloadChanged += OnDownload;
        }

        return new CleanupMethods.Unsubscribe(() =>
        {
            foreach (var download in downloads)
            {
                download.DownloadChanged -= OnDownload;
            }
        });
    }

    private static IDriverDownload Download(BridgeServices s, string providerId)
    {
        var provider = s.Hub.Providers.FirstOrDefault(p => p.Id == providerId)
            ?? throw new ArgumentException($"There is no driver called \"{providerId}\".");
        return provider as IDriverDownload
            ?? throw new DeviceException(DeviceErrorCodes.NotSupported, "DeviceDeck can't download this driver.");
    }
}
