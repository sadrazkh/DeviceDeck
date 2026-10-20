using System.Text.RegularExpressions;

namespace DeviceDeck.Android.Info;

/// <summary>
/// Best-effort body colour. There is no standard property: a few makers expose one (Pixels' ro.boot.hardware.color,
/// some ODMs' ro.product.color / ro.config.color / ro.boot.color). Values are colour words, three-letter codes or
/// hex. Unknown values give null rather than a guess. Unverified on real phones until captured with the probe.
/// </summary>
internal static partial class DeviceColor
{
    private static readonly string[] Keys =
    [
        "ro.boot.hardware.color",
        "ro.boot.color",
        "ro.boot.hw_color",
        "ro.boot.device_color",
        "ro.product.color",
        "ro.config.color",
        "ro.config.devicecolor",
        "ro.vendor.color",
        "persist.sys.device_color",
    ];

    private static readonly (string Word, string Hex)[] Words =
    [
        ("black", "#1C1C1E"), ("obsidian", "#202124"), ("graphite", "#3A3A3C"), ("phantom black", "#1C1C1E"),
        ("midnight", "#232A31"), ("charcoal", "#36454F"), ("gray", "#8E8E93"), ("grey", "#8E8E93"),
        ("titanium", "#8A8D8F"), ("silver", "#C7C8CA"), ("white", "#F2F2F2"), ("porcelain", "#EDE8E1"),
        ("snow", "#F7F7F7"), ("cream", "#F3E9D2"), ("beige", "#E8DCC4"), ("hazel", "#A3A58A"),
        ("sage", "#B2BFA0"), ("mint", "#B6E3C8"), ("green", "#4E8B5F"), ("emerald", "#2E8B57"),
        ("blue", "#2F6DB5"), ("navy", "#1F2A44"), ("sky", "#9CC7E6"), ("bay", "#7FB3D5"),
        ("purple", "#7B5EA7"), ("violet", "#8F6BB8"), ("lavender", "#C7B8E6"), ("lilac", "#C8A2C8"),
        ("pink", "#F2B8C6"), ("rose", "#E8B4B8"), ("coral", "#F08A75"), ("peach", "#F6C1A0"),
        ("red", "#C0392B"), ("orange", "#F28C28"), ("yellow", "#F4D35E"), ("lemongrass", "#E3E39A"),
        ("gold", "#D4AF37"), ("bronze", "#A97142"), ("copper", "#B87333"), ("brown", "#7B5B45"),
    ];

    private static readonly (string Code, string Word)[] Codes =
    [
        ("BLK", "black"), ("BK", "black"), ("WHT", "white"), ("WH", "white"), ("SLV", "silver"), ("GRY", "gray"),
        ("GLD", "gold"), ("BLU", "blue"), ("GRN", "green"), ("RED", "red"), ("PNK", "pink"), ("PUR", "purple"),
        ("YEL", "yellow"), ("ORG", "orange"),
    ];

    [GeneratedRegex(@"^#?(?<hex>[0-9A-Fa-f]{6})$")]
    private static partial Regex Hex();

    public static string? FromProps(IReadOnlyDictionary<string, string> props)
    {
        foreach (string key in Keys)
        {
            if (props.TryGetValue(key, out string? value) && FromValue(value) is { } hex)
            {
                return hex;
            }
        }
        return null;
    }

    public static string? FromValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim();
        var hex = Hex().Match(value);
        if (hex.Success)
        {
            return "#" + hex.Groups["hex"].Value.ToUpperInvariant();
        }
        string lower = value.ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        foreach (var (word, color) in Words.OrderByDescending(w => w.Word.Length))
        {
            if (Regex.IsMatch(lower, $@"\b{Regex.Escape(word)}\b"))
            {
                return color;
            }
        }
        string upper = value.ToUpperInvariant();
        foreach (var (code, word) in Codes.OrderByDescending(c => c.Code.Length))
        {
            if (upper.StartsWith(code, StringComparison.Ordinal))
            {
                return Words.First(w => w.Word == word).Hex;
            }
        }
        return null;
    }
}
