namespace DeviceDeck.Ios.Device;

/// <summary>
/// The phone's finish as a hex colour for the UI. Older phones report DeviceEnclosureColor as "#rrggbb";
/// newer ones report a numeric code (asked for by name: it is missing from GetValue's full dump), which only
/// means something per model, so it is looked up in a table of codes confirmed on real phones.
/// </summary>
public static class DeviceColors
{
    /// <summary>(ProductType, DeviceEnclosureColor code) → finish. Add rows as real phones confirm them.</summary>
    private static readonly Dictionary<(string ProductType, string Code), (string Name, string Hex)> Known = new();

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
