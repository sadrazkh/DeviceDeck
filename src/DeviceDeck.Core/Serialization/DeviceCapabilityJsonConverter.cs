using System.Text.Json;
using System.Text.Json.Serialization;
using DeviceDeck.Core.Abstractions;

namespace DeviceDeck.Core.Serialization;

/// <summary>Writes <see cref="DeviceCapability"/> flags as <c>["info","storage","photosRead"]</c>, in bit order.</summary>
public sealed class DeviceCapabilityJsonConverter : JsonConverter<DeviceCapability>
{
    private static readonly (DeviceCapability Flag, string Name)[] Names = Enum.GetValues<DeviceCapability>()
        .Where(f => f != DeviceCapability.None && ((long)f & ((long)f - 1)) == 0)
        .OrderBy(f => (long)f)
        .Select(f => (f, JsonNamingPolicy.CamelCase.ConvertName(f.ToString())))
        .ToArray();

    public override DeviceCapability Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Capabilities must be an array of names.");
        }

        var result = DeviceCapability.None;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Capabilities must be an array of names.");
            }

            var name = reader.GetString();
            var match = Names.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
            {
                throw new JsonException($"Unknown capability '{name}'.");
            }

            result |= match.Flag;
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, DeviceCapability value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (flag, name) in Names)
        {
            if ((value & flag) == flag)
            {
                writer.WriteStringValue(name);
            }
        }

        writer.WriteEndArray();
    }
}
