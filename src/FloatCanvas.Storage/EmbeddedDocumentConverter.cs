using System.Text.Json;
using System.Text.Json.Serialization;
using FloatCanvas.Core;

namespace FloatCanvas.Storage;

/// <summary>Use the project import boundary for an embedded document, such as editable clipboard content.</summary>
public sealed class EmbeddedDocumentConverter : JsonConverter<SceneDocument>
{
    public override SceneDocument Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var json = JsonDocument.ParseValue(ref reader);
        return PackageCodec.ReadEmbeddedDocument(json.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, SceneDocument value, JsonSerializerOptions options) =>
        PackageCodec.WriteEmbeddedDocument(writer, value);
}
