using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatCanvas.Core;

/// <summary>Bounded role input and stable canonical ordering for all serializers and cache keys.</summary>
public sealed class AppearanceColorRolesConverter : JsonConverter<Dictionary<string, uint>>
{
    public override Dictionary<string, uint> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Color roles must be an object.");
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return result;
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid color role.");
            string key = reader.GetString()!;
            if (result.Count >= AppearanceRegistry.MaximumAdditionalColors || !AppearanceRegistry.IsValidColorRoleId(key) || result.ContainsKey(key) ||
                !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetUInt32(out uint value)) throw new JsonException("Invalid color role.");
            result.Add(key, value);
        }
        throw new JsonException("Incomplete color roles.");
    }
    public override void Write(Utf8JsonWriter writer, Dictionary<string, uint> value, JsonSerializerOptions options)
    {
        if (!AppearanceRegistry.IsValidColorRoles(value)) throw new JsonException("Invalid color roles.");
        writer.WriteStartObject();
        foreach (var pair in value.OrderBy(pair => pair.Key, StringComparer.Ordinal)) writer.WriteNumber(pair.Key, pair.Value);
        writer.WriteEndObject();
    }
}
