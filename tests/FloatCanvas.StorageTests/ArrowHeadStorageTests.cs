using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using FloatCanvas.Core;
using FloatCanvas.Storage;

internal static class ArrowHeadStorageTests
{
    public static async Task RunAsync(ProjectStore store, string root, Action<bool, string> check)
    {
        foreach (var style in Enum.GetValues<ArrowHeadStyle>())
        {
            var scene = new SceneDocument { Elements = [new() { Kind = ElementKind.DoubleArrow, Points = [new(5, 10), new(110, 40)],
                Style = new() { ArrowHeadStyle = style, ArrowHeadSize = 37 }, Transform = Affine2D.Translation(-20, 40).Compose(Affine2D.Rotation(28)) }] };
            string path = Path.Combine(root, "arrow-" + style + ".floatcanvas");
            await store.SaveAsync(path, scene, new());
            var loaded = await store.LoadAsync(path);
            check(JsonSerializer.Serialize(scene) == JsonSerializer.Serialize(loaded.Document), $"{style}: ZIP roundtrip preserves exact head style, size and transform");
        }
        var legacyScene = new SceneDocument { Elements = [new() { Kind = ElementKind.Arrow, Points = [new(0, 0), new(100, 0)] }] };
        var legacyPath = Path.Combine(root, "legacy-arrow.floatcanvas");
        await store.SaveAsync(legacyPath, legacyScene, new());
        using (var zip = ZipFile.Open(legacyPath, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("scene.json")!; JsonNode node;
            using (var reader = new StreamReader(entry.Open())) node = JsonNode.Parse(await reader.ReadToEndAsync())!;
            var style = node["Elements"]![0]!["Style"]!.AsObject(); style.Remove("ArrowHeadStyle"); style.Remove("ArrowHeadSize");
            entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("scene.json").Open()); await writer.WriteAsync(node.ToJsonString());
        }
        var legacy = await store.LoadAsync(legacyPath);
        check(legacy.Document.Elements[0].Style.ArrowHeadStyle == ArrowHeadStyle.FilledTriangle && legacy.Document.Elements[0].Style.ArrowHeadSize == 0,
            "old ZIP missing new fields loads with original filled automatic head");
        foreach (var field in new[] { "ArrowHeadStyle", "ArrowHeadSize" })
        {
            var invalidPath = Path.Combine(root, "invalid-read-" + field + ".floatcanvas");
            File.Copy(legacyPath, invalidPath);
            using (var zip = ZipFile.Open(invalidPath, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("scene.json")!; JsonNode node;
                using (var reader = new StreamReader(entry.Open())) node = JsonNode.Parse(await reader.ReadToEndAsync())!;
                node["Elements"]![0]!["Style"]![field] = field == "ArrowHeadStyle" ? 99 : 513;
                entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("scene.json").Open()); await writer.WriteAsync(node.ToJsonString());
            }
            bool rejected = false;
            try { await store.LoadAsync(invalidPath); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid serialized " + field + " is rejected on load");
        }
        foreach (var value in new[] { -1d, 512.1, double.NaN, double.PositiveInfinity })
        {
            var invalid = legacy.Document.Clone(); invalid.Elements[0].Style.ArrowHeadSize = value;
            bool rejected = false;
            try { await store.SaveAsync(Path.Combine(root, "invalid-head.floatcanvas"), invalid, new()); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid arrow head size is rejected before writing: " + value);
        }
        var undefined = legacy.Document.Clone(); undefined.Elements[0].Style.ArrowHeadStyle = (ArrowHeadStyle)99;
        bool undefinedRejected = false;
        try { await store.SaveAsync(Path.Combine(root, "invalid-head-style.floatcanvas"), undefined, new()); } catch (InvalidDataException) { undefinedRejected = true; }
        check(undefinedRejected, "undefined arrow head style is rejected before writing");
        legacy.Document.Elements[0].Style.ArrowHeadSize = 512;
        await store.SaveAsync(Path.Combine(root, "maximum-head.floatcanvas"), legacy.Document, legacy.Assets);
        check((await store.LoadAsync(Path.Combine(root, "maximum-head.floatcanvas"))).Document.Elements[0].Style.ArrowHeadSize == 512, "maximum explicit size 512 is accepted and retained");
    }
}
