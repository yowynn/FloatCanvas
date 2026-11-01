using System.Security.Cryptography;
using System.Text.Json;

namespace FloatCanvas.Core;

/// <summary>Savepoint identity of authored content. Runtime frame fallback pixels are a package cache.</summary>
public static class AuthoredSceneContent
{
    private static readonly JsonSerializerOptions jsonOptions = new() { MaxDepth = 128 };

    /// <summary>Owns its graph containers. Only a real live-image fallback AssetId is normalized.</summary>
    public static SceneDocument CreateProjection(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PrefabClosure.RequireSourceGraphs(document);
        var result = document.Clone();
        foreach (var element in result.Elements.Concat(result.Definitions.SelectMany(definition => definition.Graph.Elements)))
            if (element.Kind == ElementKind.Image && element.WindowSource is not null) element.AssetId = null;
        return result;
    }

    public static bool Equal(SceneDocument? left, SceneDocument? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return SceneContent.Equal(CreateProjection(left), CreateProjection(right));
    }

    /// <summary>Stable SHA256 of canonical UTF-8 JSON; dictionary insertion order is immaterial.</summary>
    public static string Fingerprint(SceneDocument document)
    {
        using var json = JsonSerializer.SerializeToDocument(CreateProjection(document), jsonOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, json.RootElement);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray();
        }
        // Value equality treats the two IEEE zero signs as equal. Keep savepoint
        // hashes consistent with that same contract instead of emitting '-0'.
        else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && number == 0) writer.WriteNumberValue(0);
        else value.WriteTo(writer);
    }
}
