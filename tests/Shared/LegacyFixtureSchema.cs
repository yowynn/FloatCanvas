using System.Text.Json.Nodes;
using FloatCanvas.Core;

internal static class LegacyFixtureSchema
{
    internal static void RemoveFutureRootFields(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            if (item["Version"] is JsonValue version && version.TryGetValue<int>(out int value))
            {
                if (value == 19) UseForegroundBackground(item);
                if (value < 16) item.Remove("RootCanvasAssignments");
                if (value < 17) item.Remove("RootPreviewCanvases");
            }
            foreach (var child in item.ToArray()) RemoveFutureRootFields(child.Value);
        }
        else if (node is JsonArray array) foreach (var child in array) RemoveFutureRootFields(child);
    }

    internal static void UseForegroundBackground(JsonNode? node)
    {
        if (node is JsonArray array) { foreach (var child in array) UseForegroundBackground(child); return; }
        if (node is not JsonObject item) return;
        if (item.ContainsKey("Kind") && item["Style"] is JsonObject style && style.Remove("PrimaryColor", out var primary))
        {
            style.Remove("SecondaryColor", out var secondary);
            style.Remove(nameof(ElementStyle.HasTextOutlineColor));
            style["Foreground"] = primary;
            style["Background"] = secondary;
            style["AdditionalColors"] ??= new JsonObject();
            if (style["Variant"]?.GetValue<string>() == ElementAppearance.NumberOutline)
                style["Variant"] = AppearanceMigration.LegacyNumberOutline;
        }
        foreach (var child in item.ToArray()) UseForegroundBackground(child.Value);
    }

    internal static void RemovePrefabFields(JsonObject scene)
    {
        scene.Remove("ResourceId"); scene.Remove("Instances"); scene.Remove("Definitions");
        UseLegacyAppearance(scene);
    }
    internal static void UseLegacyAppearance(JsonObject graph)
    {
        UseForegroundBackground(graph);
        graph.Remove("RootCanvasAssignments"); graph.Remove("RootPreviewCanvases");
        foreach (var element in (graph["Elements"]?.AsArray() ?? []).Append(graph["Background"]).Where(item => item is not null))
        {
            var item = element!.AsObject();
            if (item["Style"] is not JsonObject style || !style.ContainsKey("Foreground")) continue;
            var extras = style["AdditionalColors"]?.AsObject();
            style["Stroke"] = (extras?["legacy.stroke"] ?? style["Foreground"])!.DeepClone();
            style["Fill"] = (extras?["legacy.fill"] ?? style["Background"])!.DeepClone();
            string? variant = style["Variant"]?.GetValue<string>();
            style["LinkColors"] = variant == ElementAppearance.ShapeUniform;
            if (extras?.ContainsKey("legacy.stroke") == true || variant == ElementAppearance.Default) style.Remove("Variant");
            style.Remove("Foreground"); style.Remove("Background"); style.Remove("AdditionalColors");
            item["NumberUsesForeground"] = item["NumberTextSizeMode"]!.GetValue<int>() == (int)NumberTextSizeMode.FitBadge;
            item.Remove("NumberTextSizeMode");
        }
        foreach (var definition in graph["Definitions"]?.AsArray() ?? []) UseLegacyAppearance(definition!["Graph"]!.AsObject());
    }
}
