using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.Core;

namespace FloatCanvas.Storage;

/// <summary>Discards unsupported typed records before their type-specific readers run.</summary>
internal static class SceneContentCompatibility
{
    internal static bool RequiresFiltering(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array) return node.EnumerateArray().Any(RequiresFiltering);
        if (node.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name is "Elements" or "Effects" && property.Value.ValueKind == JsonValueKind.Array)
            {
                if (property.Value.GetArrayLength() > (property.Name == "Elements" ? SceneLimits.MaximumElements : 10_000))
                    throw new InvalidDataException("Scene content count exceeds the limit.");
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Kind", out var kind) || kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out int value))
                        throw new InvalidDataException("Missing or invalid content type.");
                    if (!(property.Name == "Elements" ? Enum.IsDefined(typeof(ElementKind), value) : Enum.IsDefined(typeof(ImageEffectKind), value))) return true;
                }
            }
            if (property.Name == "Background" && property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("Kind", out var backgroundKind) &&
                backgroundKind.ValueKind == JsonValueKind.Number && backgroundKind.TryGetInt32(out int backgroundValue) && !Enum.IsDefined(typeof(ElementKind), backgroundValue)) return true;
            if (!GeometryProperty(property.Name) && RequiresFiltering(property.Value)) return true;
        }
        return false;
    }

    internal static bool Filter(JsonNode root)
    {
        bool skippedNodes = false;
        Visit(root);
        return skippedNodes;

        void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var item in array.ToArray()) Visit(item); return; }
            if (node is not JsonObject obj) return;
            if (obj["Elements"] is JsonArray elements)
            {
                // Reject duplicate source identities even when one record will be skipped.
                var identities = new HashSet<Guid>();
                foreach (string collection in new[] { "Elements", "Groups", "Instances" })
                    if (obj[collection] is JsonArray records)
                        foreach (var record in records)
                        {
                            if (record is not JsonObject item || item["Id"] is not JsonValue idValue ||
                                !idValue.TryGetValue<Guid>(out var id) || id == Guid.Empty || !identities.Add(id))
                                throw new InvalidDataException("Invalid or duplicate scene identity.");
                        }
                if (obj["Background"] is JsonObject sourceBackground && sourceBackground["Id"] is JsonValue backgroundIdentity &&
                    backgroundIdentity.TryGetValue<Guid>(out var backgroundId) && !identities.Add(backgroundId))
                    throw new InvalidDataException("Duplicate background identity.");
                var localRemoved = new HashSet<Guid>();
                foreach (var item in elements.ToArray())
                    if (!Known<ElementKind>(item))
                    {
                        var id = item!["Id"]!.GetValue<Guid>(); localRemoved.Add(id);
                        elements.Remove(item); skippedNodes = true;
                    }
                RemoveIds(obj["RootOrder"], localRemoved);
                if (obj["Groups"] is JsonArray groups) foreach (var group in groups) RemoveIds(group?["ChildOrder"], localRemoved);
                if (obj["RootCanvasAssignments"] is JsonObject assignments)
                    foreach (string key in assignments.Select(pair => pair.Key).ToArray())
                        if (Guid.TryParse(key, out var id) && localRemoved.Contains(id)) assignments.Remove(key);
            }
            if (obj["Background"] is JsonObject background && !Known<ElementKind>(background))
            {
                if (background["Id"] is not JsonValue identity || !identity.TryGetValue<Guid>(out var id) || id == Guid.Empty)
                    throw new InvalidDataException("Invalid background identity.");
                obj["Background"] = null; skippedNodes = true;
            }
            if (obj["Effects"] is JsonArray effects)
            {
                var ids = new HashSet<Guid>();
                foreach (var item in effects.ToArray())
                {
                    if (item is not JsonObject effect || effect["Id"] is not JsonValue identity || !identity.TryGetValue<Guid>(out var id) || id == Guid.Empty || !ids.Add(id))
                        throw new InvalidDataException("Invalid or duplicate effect identity.");
                    if (!Known<ImageEffectKind>(item)) effects.Remove(item);
                }
            }
            foreach (var (_, child) in obj.ToArray()) Visit(child);
        }

    }

    private static bool GeometryProperty(string name) => name is "Points" or "ImageFrame" or "Transform" or "ProjectivePlacement" or "Style" or "Canvas" or "DisplayLayout" or "Presentation";

    private static void RemoveIds(JsonNode? node, HashSet<Guid> removed)
    {
        if (node is not JsonArray ids) return;
        foreach (var item in ids.ToArray())
            if (item is JsonValue value && value.TryGetValue<Guid>(out var id) && removed.Contains(id)) ids.Remove(item);
    }

    private static bool Known<T>(JsonNode? node) where T : struct, Enum
    {
        if (node is not JsonObject obj || obj["Kind"] is not JsonValue value || !value.TryGetValue<int>(out int kind))
            throw new InvalidDataException("Missing or invalid content type.");
        return Enum.IsDefined(typeof(T), kind);
    }
}
