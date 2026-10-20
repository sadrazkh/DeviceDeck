using System.Globalization;
using DeviceDeck.Android.Parsing;

namespace DeviceDeck.Android.Info;

/// <summary>What getprop and a couple of settings tell us about a phone, read once when it becomes ready.</summary>
internal sealed class DeviceProfile(IReadOnlyDictionary<string, string> props, string? deviceName, TimeSpan utcOffset)
{
    public IReadOnlyDictionary<string, string> Props { get; } = props;

    /// <summary>The name the owner gave the phone (Settings > About phone > Device name), when set.</summary>
    public string? DeviceName { get; } = deviceName;

    /// <summary>The phone's offset from UTC (`date +%z`), for the local times dumpsys and ls print.</summary>
    public TimeSpan UtcOffset { get; } = utcOffset;

    public int Sdk => int.TryParse(Props.First("ro.build.version.sdk"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sdk) ? sdk : 0;

    /// <summary>"14", "15", "16" — or the codename on a preview build.</summary>
    public string AndroidVersion => Props.First("ro.build.version.release_or_codename", "ro.build.version.release") ?? "";

    public string Model => Props.First("ro.product.model", "ro.product.vendor.model", "ro.product.system.model") ?? "Android phone";

    public string? Manufacturer => Props.First("ro.product.manufacturer", "ro.product.vendor.manufacturer", "ro.product.brand");

    /// <summary>
    /// Retail name where the maker publishes one in a property (Xiaomi, OPPO/OnePlus/realme, vivo, Huawei, Motorola).
    /// Samsung has none; Pixels put the retail name in ro.product.model already.
    /// </summary>
    public string? MarketingName
    {
        get
        {
            string? name = Props.First(
                "ro.product.marketname",
                "ro.product.vendor.marketname",
                "ro.product.odm.marketname",
                "ro.product.product.marketname",
                "ro.vendor.oplus.market.name",
                "ro.oppo.market.name",
                "ro.vivo.market.name",
                "ro.config.marketing_name",
                "ro.vendor.product.display",
                "ro.product.display");
            return name != null && !name.Equals(Model, StringComparison.OrdinalIgnoreCase) ? name : null;
        }
    }

    public string? Serial => Props.First("ro.serialno", "ro.boot.serialno");

    public string? SecurityPatch => Props.First("ro.build.version.security_patch");

    public string? BuildId => Props.First("ro.build.display.id", "ro.build.id");

    public string? Fingerprint => Props.First("ro.build.fingerprint", "ro.system.build.fingerprint");

    public string? Chipset
    {
        get
        {
            string? manufacturer = Props.First("ro.soc.manufacturer");
            string? model = Props.First("ro.soc.model");
            if (model != null)
            {
                return manufacturer != null && !model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase) ? $"{manufacturer} {model}" : model;
            }
            return Props.First("ro.board.platform", "ro.hardware");
        }
    }

    public string? Baseband => Props.First("gsm.version.baseband")?.Split(',')[0];

    public string? CpuAbi => Props.First("ro.product.cpu.abi");

    public IReadOnlyList<string> SupportedAbis =>
        (Props.First("ro.product.cpu.abilist", "ro.product.cpu.abi") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public string? ColorHex => DeviceColor.FromProps(Props);

    /// <summary>The name to show: owner's device name, else retail name, else model.</summary>
    public string DisplayName => DeviceName ?? MarketingName ?? Model;
}
