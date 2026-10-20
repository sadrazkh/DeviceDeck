using DeviceDeck.Core.Abstractions;
using DeviceDeck.Ios.Plist;

namespace DeviceDeck.Ios.Device;

/// <summary>Pure mappings from lockdown / diagnostics_relay values to the shared models. Tested against fixtures.</summary>
public static class LockdownMapping
{
    public const DeviceCapability ReadyCapabilities =
        DeviceCapability.Info | DeviceCapability.Storage | DeviceCapability.Battery |
        DeviceCapability.PhotosRead | DeviceCapability.Files | DeviceCapability.AppDocuments |
        DeviceCapability.AppsList | DeviceCapability.AppsInstall | DeviceCapability.AppsUninstall |
        DeviceCapability.Syslog | DeviceCapability.CrashReports |
        DeviceCapability.Reboot | DeviceCapability.Shutdown;

    public static DeviceCapability CapabilitiesFor(DeviceState state) => state switch
    {
        DeviceState.Ready => ReadyCapabilities,
        DeviceState.Locked => DeviceCapability.Info,
        _ => DeviceCapability.None,
    };

    public static DeviceSummary Summary(string udid, PlistDict values, ConnectionKind connection, DeviceState state)
    {
        var productType = values.GetString("ProductType");
        var enclosure = values.GetString("DeviceEnclosureColor") ?? values.GetLong("DeviceEnclosureColor")?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new DeviceSummary(
            Id: udid,
            Name: values.GetString("DeviceName") ?? DeviceModels.MarketingName(productType) ?? "iPhone",
            Platform: DevicePlatform.Ios,
            Model: productType ?? "iPhone",
            MarketingName: DeviceModels.MarketingName(productType),
            OsVersion: values.GetString("ProductVersion") ?? "",
            Connection: connection,
            State: state,
            Mode: DeviceMode.Full,
            Capabilities: CapabilitiesFor(state),
            ColorHex: DeviceColors.Hex(productType, enclosure));
    }

    /// <summary>
    /// From the com.apple.disk_usage domain (merged with com.apple.disk_usage.factory where the first lacks a key).
    /// Categories: photos, videos, apps, media, system; "other" is what is used but unaccounted for.
    /// </summary>
    /// <param name="videoBytes">Video files in the camera roll, split out of the photo usage; null when unknown.</param>
    /// <remarks>
    /// Free space is AmountDataAvailable: what can be written right now. AFC's FSFreeBytes is that plus
    /// AmountDataReserved. Settings → iPhone Storage shows more free (an iPhone 16 on iOS 27: 6.86 GB in Settings
    /// against 4.2 GB here) because it also counts purgeable data, which no lockdown or AFC value reports.
    /// </remarks>
    public static StorageInfo Storage(PlistDict diskUsage, long? videoBytes = null)
    {
        var total = diskUsage.GetLong("TotalDiskCapacity") ?? 0;
        var dataCapacity = diskUsage.GetLong("TotalDataCapacity");
        var free = diskUsage.GetLong("AmountDataAvailable") ?? diskUsage.GetLong("TotalDataAvailable") ?? 0;

        long? system = null;
        if (diskUsage.GetLong("TotalSystemCapacity") is { } sysCap)
            system = sysCap - (diskUsage.GetLong("TotalSystemAvailable") ?? 0);
        else if (dataCapacity is { } dc && total > dc)
            system = total - dc;

        // Older iOS reports the camera roll and the synced photo library separately; newer iOS (seen on 27)
        // puts the same camera-roll figure in both keys.
        var camera = diskUsage.GetLong("CameraUsage");
        var library = diskUsage.GetLong("PhotoUsage");
        var photos = camera is not null && camera == library ? camera : Sum(camera, library);
        long? videos = null;
        if (videoBytes is > 0 && photos is > 0)
        {
            videos = Math.Min(videoBytes.Value, photos.Value);
            photos -= videos;
        }
        var media = Sum(diskUsage.GetLong("MediaCacheUsage"), diskUsage.GetLong("VoicemailUsage"));
        var apps = Sum(diskUsage.GetLong("MobileApplicationUsage"), diskUsage.GetLong("WebAppCacheUsage"));

        var categories = new List<StorageCategory>();
        void Add(string key, long? bytes)
        {
            if (bytes is > 0)
                categories.Add(new StorageCategory(key, bytes.Value));
        }
        Add("photos", photos);
        Add("videos", videos);
        Add("apps", apps);
        Add("media", media);
        Add("system", system);

        var used = Math.Max(0, total - free);
        var known = categories.Sum(c => c.Bytes);
        Add("other", used - known); // skipped when the parts already exceed the used space (estimates overlap)

        return new StorageInfo([new StorageVolume("internal", "iPhone storage", total, free, categories)]);
    }

    /// <param name="powerSource">diagnostics_relay IORegistry for IOPMPowerSource; may be null.</param>
    /// <param name="batteryDomain">lockdown's com.apple.mobile.battery domain; may be empty.</param>
    public static BatteryInfo Battery(PlistDict? powerSource, PlistDict batteryDomain)
    {
        var io = powerSource ?? new PlistDict();
        var data = io.GetDict("BatteryData") ?? new PlistDict();

        int? level = batteryDomain.GetInt("BatteryCurrentCapacity");
        if (level is null && io.GetLong("CurrentCapacity") is { } cur)
        {
            var max = io.GetLong("MaxCapacity");
            level = max is > 0 and not 100 ? (int)Math.Round(100.0 * cur / max.Value) : (int)cur;
        }

        var charging = batteryDomain.GetBool("BatteryIsCharging") ?? io.GetBool("IsCharging") ?? false;
        var design = (int?)(io.GetLong("DesignCapacity") ?? data.GetLong("DesignCapacity"));
        // NominalChargeCapacity is steady between readings; FullChargeCapacity drifts by tens of mAh. Neither
        // reproduces Settings' "Maximum Capacity" on iOS 27 (an iPhone 16 showing 88% reports nominal 92%,
        // full-charge 94%), and no percentage key is exposed, so health is nominal / design.
        var full = (int?)(io.GetLong("AppleRawMaxCapacity") ?? io.GetLong("NominalChargeCapacity")
                          ?? data.GetLong("NominalChargeCapacity") ?? data.GetLong("FullChargeCapacity"));
        if (full is null && io.GetLong("MaxCapacity") is > 100 and var mc)
            full = (int)mc;

        int? health = design is > 0 && full is > 0 ? Math.Min(100, (int)Math.Round(100.0 * full.Value / design.Value)) : null;
        var cycles = (int?)(io.GetLong("CycleCount") ?? data.GetLong("CycleCount"));
        double? temp = io.GetLong("Temperature") is { } t ? Math.Round(t / 100.0, 1) : null;

        return new BatteryInfo(level, charging, health, cycles, temp, design, full);
    }

    public static DeviceDetails Details(string udid, PlistDict values)
    {
        var rows = new List<DetailRow>();
        void Add(string key, string label, string? value, bool sensitive = false)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(new DetailRow(key, label, value, sensitive));
        }

        var productType = values.GetString("ProductType");
        Add("name", "Name", values.GetString("DeviceName"));
        Add("model", "Model", JoinNonEmpty(" · ", DeviceModels.MarketingName(productType), productType,
            Concat(values.GetString("ModelNumber"), values.GetString("RegionInfo"))));
        Add("ios", "iOS", JoinNonEmpty(" ", values.GetString("ProductVersion"),
            values.GetString("BuildVersion") is { } b ? $"({b})" : null));
        Add("serial", "Serial number", values.GetString("SerialNumber"), sensitive: true);
        Add("imei", "IMEI", values.GetString("InternationalMobileEquipmentIdentity"), sensitive: true);
        Add("imei2", "IMEI 2", values.GetString("InternationalMobileEquipmentIdentity2"), sensitive: true);
        Add("udid", "UDID", values.GetString("UniqueDeviceID") ?? udid);
        Add("wifi", "Wi-Fi address", values.GetString("WiFiAddress"));
        Add("bluetooth", "Bluetooth address", values.GetString("BluetoothAddress"));
        Add("carrier", "Carrier", Carrier(values));
        Add("phone", "Phone number", values.GetString("PhoneNumber"), sensitive: true);
        Add("baseband", "Modem firmware", values.GetString("BasebandVersion"));
        Add("cpu", "CPU architecture", values.GetString("CPUArchitecture"));
        Add("hardware", "Hardware model", values.GetString("HardwareModel"));
        Add("activation", "Activation", values.GetString("ActivationState"));
        return new DeviceDetails(rows);
    }

    /// <summary>
    /// "Irancell (IR) · 432/35" from CarrierBundleInfoArray, one entry per SIM ("… (2 SIMs)" when both are the same).
    /// The name comes from the carrier bundle id, or from the MCC/MNC table when the bundle is "Unknown".
    /// </summary>
    public static string? Carrier(PlistDict values)
    {
        var entries = values.GetArray("CarrierBundleInfoArray")?.OfType<PlistDict>().ToList();
        if (entries is null or { Count: 0 })
            return null;
        var parts = new List<string>();
        foreach (var e in entries)
        {
            var mcc = Text(e, "MCC");
            var mnc = Text(e, "MNC");
            var name = BundleCarrierName(e.GetString("CFBundleIdentifier"));
            if (name is null && Carriers.Name(mcc, mnc) is { } known)
                name = Carriers.Country(mcc) is { } iso ? $"{known} ({iso})" : known;
            var mccMnc = mcc is not null && mnc is not null ? $"{mcc}/{mnc}" : null;
            if (JoinNonEmpty(" · ", name, mccMnc) is { } text)
                parts.Add(text);
        }
        var distinct = parts.Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count switch
        {
            0 => null,
            1 when parts.Count > 1 => $"{distinct[0]} ({parts.Count} SIMs)",
            _ => string.Join(", ", distinct),
        };
    }

    /// <summary>"com.apple.Irancell_ir" → "Irancell (IR)"; null for a missing or "Unknown" bundle.</summary>
    private static string? BundleCarrierName(string? id)
    {
        if (id is null)
            return null;
        var bare = id.StartsWith("com.apple.", StringComparison.Ordinal) ? id["com.apple.".Length..] : id;
        if (bare.Length == 0 || bare.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        var us = bare.LastIndexOf('_');
        return us > 0 && bare.Length - us - 1 is 2 or 3
            ? $"{bare[..us].Replace('_', ' ')} ({bare[(us + 1)..].ToUpperInvariant()})"
            : bare.Replace('_', ' ');
    }

    private static string? Text(PlistDict d, string key) =>
        d.GetString(key) ?? d.GetLong(key)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static string? Concat(string? a, string? b) => a is null ? null : a + b;

    private static string? JoinNonEmpty(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}
