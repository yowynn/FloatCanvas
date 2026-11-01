using System.Text.Json.Nodes;
using FloatCanvas.Core;

internal static class LegacyFixtureSchema
{
    internal static void RemovePrefabFields(JsonObject scene)
    {
        scene.Remove("ResourceId"); scene.Remove("Instances"); scene.Remove("Definitions");
        UseLegacyAppearance(scene);
    }
    internal static void UseLegacyAppearance(JsonObject graph)
    {
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
