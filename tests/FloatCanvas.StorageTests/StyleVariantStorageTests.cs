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
                    Style = new() { Variant = ElementAppearance.NumberFilled, Stroke = 0xFF123456, Fill = 0xFFEEDDCC, FontSize = 17 } },
                new() { Kind = ElementKind.Highlighter, Points = [new(20, 30), new(100, 30)],
                    Style = new() { StrokeWidth = 24, HighlighterTip = HighlighterTip.Flat, HighlighterTipAngle = -35,
                        HighlighterInkContrast = .55, StrokePattern = StrokePattern.DashDot } },
                new() { Kind = ElementKind.Highlighter, Points = [new(20, 60), new(100, 60)], Style = new() { StrokeWidth = 24 } },
                new() { Kind = ElementKind.Line, Points = [new(20, 90), new(100, 90)],
                    Style = new() { LineArcBulge = -.3, EndArrowHead = ArrowHeadStyle.Open, StrokePattern = StrokePattern.DashDot } },
                new() { Kind = ElementKind.Line, Points = [new(20, 90), new(100, 90)],
                    Style = new() { LineArcBulge = -.75, LineArcOffset = .25 } }
            ]
        };
        string expected = JsonSerializer.Serialize(document); string path = Path.Combine(root, "variants.floatcanvas");
        await store.SaveAsync(path, document, assets);
        var loaded = await store.LoadAsync(path);
        check(JsonSerializer.Serialize(loaded.Document) == expected, "project roundtrip retains all stable style IDs, inactive colors and historical compatibility flags");
        check(loaded.Document.Elements[6].Style.LineArcBulge == -.3 && loaded.Document.Elements[6].Points.Count == 2,
            "project roundtrip retains the signed bend and both editable endpoints");
        check(ElementGeometry.LineArcHandle(loaded.Document.Elements[6]) == new PointD(60, 66) &&
            ElementGeometry.LineArcHandle(loaded.Document.Elements[7]) == new PointD(80, 30),
            "project roundtrip preserves the position of a handle past the semicircle boundary");
        var uniform = loaded.Document.Elements[0]; var independent = loaded.Document.Elements[1];
        check(ElementAppearance.ShapeFill(uniform.Style) == 0xFF112233 && uniform.Style.LinkColors && uniform.Style.Fill == 0x7F445566,
            "canonical uniform style derives its compatibility flag without discarding the inactive fill color");
        check(ElementAppearance.ShapeFill(independent.Style) == 0x7F445566 && !independent.Style.LinkColors,
            "canonical independent style derives its compatibility flag after storage roundtrip");
        var outline = loaded.Document.Elements[2]; var filled = loaded.Document.Elements[3];
        var flat = loaded.Document.Elements[4]; var round = loaded.Document.Elements[5];
        check(flat.Style.HighlighterTip == HighlighterTip.Flat && flat.Style.HighlighterTipAngle == -35 && flat.Style.HighlighterInkContrast == .55 &&
            flat.Style.StrokePattern == StrokePattern.DashDot &&
            round.Style.HighlighterTip == HighlighterTip.Round && round.Style.HighlighterTipAngle == 0 &&
            !JsonSerializer.Serialize(round).Contains("HighlighterTip", StringComparison.Ordinal),
            "flat highlighter tip, angle and dash-dot pattern survive saving while existing strokes retain the round default");
        var olderStyle = JsonNode.Parse(JsonSerializer.Serialize(flat.Style))!.AsObject();
        olderStyle.Remove(nameof(ElementStyle.HighlighterInkContrast));
        check(olderStyle.Deserialize<ElementStyle>()!.HighlighterInkContrast == 1,
            "a highlighter style saved before ink contrast opens with the standard density profile");
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
