using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class EditablePresetRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        DashedContourRenderingTests.Run(check);
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var line = new CanvasElement { Kind = ElementKind.Line, Points = [new(30, 40), new(170, 40)],
            Style = new() { Stroke = 0xFFDD3366, StrokeWidth = 2, ArrowHeadSize = 30, StartArrowHead = ArrowHeadStyle.Open, EndArrowHead = ArrowHeadStyle.Diamond } };
        var document = new SceneDocument { Viewport = new(0, 0, 220, 150), Elements = [line] };
        using (var image = SKBitmap.Decode(renderer.ExportPng(document, assets, true)))
        {
            check("unified line independently renders open start and filled diamond end", image.GetPixel(45, 47).Alpha > 80 && image.GetPixel(155, 50).Alpha > 80 && image.GetPixel(55, 48).Alpha == 0);
        }
        line.Style.StartArrowHead = line.Style.EndArrowHead = ArrowHeadStyle.None;
        using (var image = SKBitmap.Decode(renderer.ExportPng(document, assets, true)))
            check("explicit endpoint none clears both cached heads", image.GetPixel(45, 47).Alpha == 0 && image.GetPixel(155, 50).Alpha == 0);
        line.Points = [new(20, 40), new(190, 40)]; line.Style.StrokeWidth = 4;
        foreach (var pattern in Enum.GetValues<StrokePattern>())
        {
            line.Style.StrokePattern = pattern;
            using var image = SKBitmap.Decode(renderer.ExportPng(document, assets, true));
            check(pattern + " has real stroke ink", image.GetPixel(20, 40).Alpha > 80);
            check(pattern + " raster gap matches exact hit", (image.GetPixel(pattern == StrokePattern.Dotted ? 25 : 42, 40).Alpha > 80) == (pattern == StrokePattern.Solid));
            check(pattern + " never leaks a path effect into text/fill rendering", image.GetPixel(25, 80).Alpha == 0);
        }
        var rectangle = new CanvasElement { Kind = ElementKind.Rectangle, Width = 80, Height = 60, Transform = Affine2D.Translation(10, 10),
            Style = new() { Fill = 0xFF2299CC, StrokeWidth = 0 } };
        document.Elements = [rectangle];
        using var square = SKBitmap.Decode(renderer.ExportPng(document, assets, true));
        long builds = renderer.PathBuildCount; rectangle.CornerRadius = 25;
        using var round = SKBitmap.Decode(renderer.ExportPng(document, assets, true));
        check("rectangle radius change rebuilds real path and removes square corner", renderer.PathBuildCount == builds + 1 && square.GetPixel(12, 12).Alpha == 255 && round.GetPixel(12, 12).Alpha == 0 && round.GetPixel(50, 40).Alpha == 255);
        rectangle.Kind = ElementKind.RoundedRectangle; rectangle.CornerRadius = null;
        byte[] oldDefault = renderer.ExportPng(document, assets, true);
        rectangle.CornerRadius = 12;
        check("legacy rounded rectangle pixels equal explicit twenty percent radius", oldDefault.AsSpan().SequenceEqual(renderer.ExportPng(document, assets, true)));

        foreach (var symbol in Enum.GetValues<SymbolKind>())
        {
            var item = new CanvasElement { Kind = ElementKind.Symbol, Symbol = symbol, Width = 100, Height = 100,
                Transform = Affine2D.Translation(10, 10), Style = new() { Stroke = 0xFFCC2266, StrokeWidth = 2 } };
            document.Elements = [item];
            using var outline = SKBitmap.Decode(renderer.ExportPng(document, assets, true));
            builds = renderer.PathBuildCount; item.Style.Fill = 0xFF2266CC;
            using var fill = SKBitmap.Decode(renderer.ExportPng(document, assets, true));
            check(symbol + " supports distinct real outline and fill", Count(outline) > 100 && Count(fill) > Count(outline) && renderer.PathBuildCount == builds);
            item.Style.Stroke = 0xFF22CC66; item.Style.Fill = 0xFF22CC66;
            using var recolored = SKBitmap.Decode(renderer.ExportPng(document, assets, true));
            check(symbol + " recolors the editable vector without rebuilding geometry", Pixels(recolored).Any(p => p.Alpha == 255 && p.Green > 180 && p.Red < 70) && renderer.PathBuildCount == builds);
        }
        var label = new CanvasElement { Kind = ElementKind.Text, Text = "Float 矢量", Width = 100, Height = 30, Transform = Affine2D.Translation(20, 20), Style = new() { FontSize = 18 } };
        var badge = new CanvasElement { Kind = ElementKind.Symbol, Symbol = SymbolKind.Heart, Width = 100, Height = 100, Style = new() { Fill = 0x442299CC } };
        var session = new EditorSession(new() { Viewport = new(0, 0, 130, 120), Elements = [badge, label] });
        session.SetSelection([badge.Id, label.Id]); session.GroupSelection("vector stamp");
        byte[] grouped = renderer.ExportPng(session.Document, assets, true);
        var ungrouped = session.Document.Clone(); ungrouped.Groups.Clear(); foreach (var element in ungrouped.Elements) element.GroupId = null;
        check("logical grouping retains exact compositing and vector text pixels", grouped.AsSpan().SequenceEqual(renderer.ExportPng(ungrouped, assets, true)));
        var stamp = session.CreateSelectionSnapshot();
        var receiving = new EditorSession(new() { Viewport = new(0, 0, 400, 350) });
        receiving.InsertDocument(stamp, assets, Affine2D.Scale(3, 3));
        using var scaled = SKBitmap.Decode(renderer.ExportPng(receiving.Document, receiving.Assets, true));
        check("scaled stamp renders editable text and vector artwork with no raster asset", receiving.Assets.Count == 0 && receiving.Document.Elements.Any(e => e.Kind == ElementKind.Text && e.Text == label.Text) && Count(scaled) > 1500);
    }

    private static int Count(SKBitmap bitmap) => Pixels(bitmap).Count(p => p.Alpha > 80);
    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap)
    {
        for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++) yield return bitmap.GetPixel(x, y);
    }
}
