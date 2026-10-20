using System.Globalization;

namespace DeviceDeck.Ios.Device;

/// <summary>
/// Carrier names by MCC/MNC, for SIMs whose carrier bundle the phone reports as "Unknown" (seen on iOS 27
/// with Iranian SIMs). Small on purpose: the networks DeviceDeck's users are likely to have; extend as needed.
/// </summary>
public static class Carriers
{
    private static readonly Dictionary<(int Mcc, int Mnc), string> Names = new()
    {
        [(432, 11)] = "MCI",        // Hamrah-e Avval
        [(432, 14)] = "TKC",        // Telecommunication Kish Company
        [(432, 19)] = "Espadan",    // MTCE
        [(432, 20)] = "RighTel",
        [(432, 32)] = "Taliya",
        [(432, 35)] = "Irancell",   // MTN Irancell
    };

    private static readonly Dictionary<int, string> Countries = new()
    {
        [432] = "IR",
    };

    /// <summary>"MCI", or null for an unknown network.</summary>
    public static string? Name(string? mcc, string? mnc) =>
        Parse(mcc) is { } c && Parse(mnc) is { } n && Names.TryGetValue((c, n), out var name) ? name : null;

    /// <summary>ISO country code for an MCC ("IR"), or null.</summary>
    public static string? Country(string? mcc) => Parse(mcc) is { } c && Countries.TryGetValue(c, out var iso) ? iso : null;

    private static int? Parse(string? s) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
}
