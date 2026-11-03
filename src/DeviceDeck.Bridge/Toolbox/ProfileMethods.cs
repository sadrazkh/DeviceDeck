using System.Text;
using DeviceDeck.Bridge.JsonRpc;
using DeviceDeck.Bridge.Methods;
using DeviceDeck.Core.Abstractions;
using DeviceDeck.Core.Hub;
using DeviceDeck.Core.Toolbox;

namespace DeviceDeck.Bridge.Toolbox;

/// <summary>The IPv6 profile tool of docs/bridge-contract.md ("IPv6 profile"): build it, show it, save it, hand it to the iPhone.</summary>
public static class ProfileMethods
{
    public static void Register(JsonRpcDispatcher rpc, DeviceHub hub, ToolboxServices t)
    {
        rpc.Register<DeviceParams, ProfileListResult>("profile.list", async (p, ct) =>
            new ProfileListResult(await Profiles(hub, p.DeviceId).ListProfilesAsync(ct).ConfigureAwait(false)));
        rpc.Register<ProfileOptionsParams, ProfilePreviewResult>("profile.preview", (p, _) =>
        {
            var options = p.ToOptions();
            return Task.FromResult(new ProfilePreviewResult(Encoding.UTF8.GetString(CellularProfile.Build(options)), CellularProfile.FileName(options)));
        });
        rpc.Register<ProfileSendParams, Empty>("profile.send", async (p, ct) =>
        {
            var profile = CellularProfile.Build(new CellularProfileOptions(p.Apn, p.Mode, p.AttachApn, p.Xlat464));
            await Profiles(hub, p.DeviceId).SendProfileAsync(profile, ct).ConfigureAwait(false);
            return Empty.Value;
        });
        rpc.Register<ProfileSaveParams, SavedFileResult>("profile.save", async (p, ct) =>
        {
            var options = new CellularProfileOptions(p.Apn, p.Mode, p.AttachApn, p.Xlat464);
            var profile = CellularProfile.Build(options);
            if (!string.IsNullOrWhiteSpace(p.Destination) && !Path.IsPathFullyQualified(p.Destination))
            {
                throw new ArgumentException("destination must be a full path on this PC.");
            }

            var folder = string.IsNullOrWhiteSpace(p.Destination) ? Path.Combine(t.OutputFolder, "Profiles") : p.Destination;
            var name = CellularProfile.FileName(options);
            var path = ToolboxMethods.FreePath(folder, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
            await File.WriteAllBytesAsync(path, profile, ct).ConfigureAwait(false);
            return new SavedFileResult(path, 1);
        });
    }

    private static IConfigurationProfiles Profiles(DeviceHub hub, string deviceId)
    {
        var device = hub.GetUsableDevice(deviceId);
        return device.Summary.Platform == DevicePlatform.Ios && device is IConfigurationProfiles profiles
            ? profiles
            : throw new DeviceException(DeviceErrorCodes.NotSupported, "Only an iPhone takes configuration profiles.");
    }
}
