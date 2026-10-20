namespace DeviceDeck.Ios.Device;

/// <summary>
/// The phone's finish as a hex colour for the UI. Older phones report DeviceEnclosureColor as "#rrggbb";
/// newer ones report a numeric code (asked for by name: it is missing from GetValue's full dump), which only
/// means something per model, so it is looked up in a table; each row says whether a real phone confirmed it.
/// </summary>
public static class DeviceColors
{
    /// <summary>
    /// (ProductType, DeviceEnclosureColor code) → finish. Hex values approximate Apple's colour swatches.
    /// Verified = seen on a real phone; the others are inferred and need a phone to confirm them.
    /// </summary>
    private static readonly Dictionary<(string ProductType, string Code), (string Name, string Hex, bool Verified)> Known = new()
    {
        [("iPhone17,3", "18")] = ("Teal", "#b0d4d2", true),   // iPhone 16, confirmed by its owner
        [("iPhone17,4", "18")] = ("Teal", "#b0d4d2", false),  // iPhone 16 Plus: same finishes; unverified
    };

    /// <summary>Whether the table's entry for this phone was confirmed on a real phone.</summary>
    public static bool IsVerified(string? productType, string? enclosureColor) =>
        productType is not null && enclosureColor is not null && Known.TryGetValue((productType, enclosureColor), out var f) && f.Verified;

    public static string? Hex(string? productType, string? enclosureColor)
    {
        if (enclosureColor is { Length: 7 } && enclosureColor[0] == '#' && enclosureColor[1..].All(Uri.IsHexDigit))
            return enclosureColor.ToLowerInvariant();
        if (productType is null || enclosureColor is null)
            return null;
        return Known.TryGetValue((productType, enclosureColor), out var finish) ? finish.Hex : null;
    }

    public static string? Name(string? productType, string? enclosureColor) =>
        productType is not null && enclosureColor is not null && Known.TryGetValue((productType, enclosureColor), out var finish)
            ? finish.Name
            : null;
}
