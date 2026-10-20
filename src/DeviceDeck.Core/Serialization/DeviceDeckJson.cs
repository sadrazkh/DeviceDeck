using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceDeck.Core.Serialization;

/// <summary>
/// The one set of JSON options used on the wire and on disk: camelCase properties, enums as camelCase strings,
/// <see cref="Abstractions.DeviceCapability"/> as an array of camelCase names (docs/bridge-contract.md).
/// </summary>
public static class DeviceDeckJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.Strict,
            WriteIndented = false,
        };
        // Order matters: the flags converter must win over the generic enum converter.
        options.Converters.Add(new DeviceCapabilityJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
