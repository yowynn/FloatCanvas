using FloatCanvas.Core;
using FloatCanvas.Storage;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class StyleVariantStorageTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string root = Path.Combine(AppContext.BaseDirectory, "style-variant-storage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ProjectStore("1.2.3-test"); var assets = new AssetLibrary();
        var document = new SceneDocument
        {
            Elements =
            [
                new() { Kind = ElementKind.Rectangle, Width = 80, Height = 60,
                    Style = new() { Variant = ElementAppearance.ShapeUniform, Stroke = 0xFF112233, Fill = 0x7F445566 } },
                new() { Kind = ElementKind.Ellipse, Width = 80, Height = 60,
                    Style = new() { Variant = ElementAppearance.ShapeIndependent, Stroke = 0xFF112233, Fill = 0x7F445566 } },
                new() { Kind = ElementKind.Number, Width = 60, Height = 60, Number = 12, NumberUsesForeground = false,
                    Style = new() { Variant = ElementAppearance.NumberOutline, Stroke = 0xFF123456, Fill = 0xFFEEDDCC, FontSize = 13 } },
                new() { Kind = ElementKind.Number, Width = 60, Height = 60, Number = 13, NumberUsesForeground = false,
                    Style = new() { Variant = ElementAppearance.NumberFilled, Stroke = 0xFF123456, Fill = 0xFFEEDDCC, FontSize = 17 } }
            ]
        };
        string expected = JsonSerializer.Serialize(document); string path = Path.Combine(root, "variants.floatcanvas");
        await store.SaveAsync(path, document, assets);
        var loaded = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(loaded.Document) == expected, "project roundtrip retains all stable style IDs, inactive colors and historical compatibility flags");
        var uniform = loaded.Document.Elements[0]; var independent = loaded.Document.Elements[1];
        check(ElementAppearance.ShapeFill(uniform.Style) == 0xFF112233 && uniform.Style.LinkColors && uniform.Style.Fill == 0x7F445566,
            "canonical uniform style derives its compatibility flag without discarding the inactive fill color");
        check(ElementAppearance.ShapeFill(independent.Style) == 0x7F445566 && !independent.Style.LinkColors,
            "canonical independent style derives its compatibility flag after storage roundtrip");
        var outline = loaded.Document.Elements[2]; var filled = loaded.Document.Elements[3];
        check(ElementAppearance.IsOutlineNumber(outline) && ElementAppearance.PrimaryColor(outline) == 0xFF123456 &&
            ElementAppearance.NumberTextColor(outline) == 0xFF123456 && outline.Style.FontSize == 13 && !outline.NumberUsesForeground,
            "outline badge keeps its primary outline/text role and historical sizing flag after reopening");
        check(ElementAppearance.PrimaryColor(filled) == 0xFF123456 && ElementAppearance.NumberTextColor(filled) == 0xFFEEDDCC &&
            filled.Style.FontSize == 17 && !filled.NumberUsesForeground, "filled badge variant takes precedence over the old color convention while preserving font data");

        var recovery = new RecoveryStore(Path.Combine(root, "recovery"), "1.2.3-test");
        var capture = document.Clone(); capture.Kind = DocumentKind.FrozenCapture;
        var workspace = new WorkspaceState { LiveDocument = document, CaptureDocument = capture, CaptureOrigin = new(11, 22) };
        await recovery.SaveAsync(workspace.Clone(), assets);
        check(JsonSerializer.Serialize((await recovery.LoadAsync())!.State) == JsonSerializer.Serialize(workspace),
            "recovery retains variants and inactive appearance data in both live and captured documents");

        byte[] before = await File.ReadAllBytesAsync(path);
        foreach (string unsupported in new[] { "shape.unknown", ElementAppearance.NumberOutline, "" })
        {
            var invalid = document.Clone(); invalid.Elements[0].Style.Variant = unsupported;
            bool rejected = false;
            try { await store.SaveAsync(path, invalid, assets); } catch (InvalidDataException) { rejected = true; }
            check(rejected && (await File.ReadAllBytesAsync(path)).AsSpan().SequenceEqual(before),
                "unknown or type-incompatible style ID is rejected before overwriting a valid package: " + unsupported);
        }
        foreach (string unsupported in new[] { "number.future", ElementAppearance.ShapeUniform })
        {
            string invalidPath = Path.Combine(root, unsupported + ".floatcanvas"); File.Copy(path, invalidPath);
            using (var zip = ZipFile.Open(invalidPath, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("scene.json")!; JsonObject json;
                using (var reader = new StreamReader(entry.Open())) json = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                json["Elements"]![2]!["Style"]!["Variant"] = unsupported;
                entry.Delete(); using var writer = new StreamWriter(zip.CreateEntry("scene.json").Open()); await writer.WriteAsync(json.ToJsonString());
            }
            bool rejected = false;
            try { await store.LoadAsync(invalidPath); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "unknown or type-incompatible stored badge style is rejected instead of silently falling back: " + unsupported);
        }
    }
}
