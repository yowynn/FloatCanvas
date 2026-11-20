using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ToolColorPartitionTests
{
    internal static void Run(SceneRenderer renderer, Action<string, bool> check)
    {
        var keys = new[] { new PresentationKeyCap(0x11, "Ctrl", true), new PresentationKeyCap(0x41, "A") };
        renderer.PresentationFrame = new(1, 1, TimeSpan.FromSeconds(1), pressedKeys: keys,
            recentChords: [new(keys, TimeSpan.FromSeconds(1), true)]);
        var backdrop = new SceneDocument { Viewport = new(0, 0, 160, 130), Elements =
            [Tile(0, 0xFF316286), Tile(80, 0xFF53935E)] };
        var assets = new AssetLibrary();
        using var behind = SKBitmap.Decode(renderer.ExportPng(backdrop, assets, true));
        foreach (string variant in new[] { ElementAppearance.NumberFilled, ElementAppearance.NumberOutline, "log", ElementAppearance.TextOutline })
        {
            var item = new CanvasElement { Kind = ElementKind.Number, Number = 87, Width = 108, Height = 108, NumberTextSizeMode = NumberTextSizeMode.FitBadge,
                Transform = Affine2D.Translation(26, 10), Style = new() { Variant = variant, FontFamily = "Segoe UI", FontSize = 30 } };
            if (variant == "log")
            {
                item.Kind = ElementKind.Presentation; item.Style.Variant = ElementAppearance.Default;
                item.Presentation = new() { Modules = PresentationModules.Keys, DisplayStyle = PresentationDisplayStyle.Log,
                    KeyPanelAlignment = PresentationPanelAlignment.TopLeft };
                item.Style.FontSize = 42;
                var natural = PresentationLayout.NaturalSize(item.Presentation, item.Style, renderer);
                item.Width = natural.X * item.Height / natural.Y;
                item.Transform = Affine2D.Translation(4, 6);
            }
            if (variant == ElementAppearance.TextOutline)
            {
                item.Kind = ElementKind.Text; item.Text = "87"; item.Style.FontSize = 66;
            }
            bool filledBadge = variant == ElementAppearance.NumberFilled;
            void Colors(uint ink, uint body)
            {
                item.Style.PrimaryColor = filledBadge ? body : ink;
                item.Style.SecondaryColor = filledBadge ? ink : body;
            }
            SKBitmap Render() => SKBitmap.Decode(renderer.ExportPng(new SceneDocument
                { Viewport = backdrop.Viewport, Elements = [..backdrop.Elements, item] }, assets, true));
            Colors(0xFFFFFFFF, 0xFFE02030);
            using var guide = Render();
            var white = SKColors.White; var red = new SKColor(0xFFE02030);
            var reference = guide.Pixels; var ground = behind.Pixels;
            // Probe the interiors of material regions. The antialias fringe can differ
            // by a subpixel between an opaque canvas and a transparent intermediate.
            var inkPixels = InteriorPixels(guide, reference, white);
            var fillPixels = InteriorPixels(guide, reference, red);
            check($"{variant} reference contains independent ink and body regions ({inkPixels.Length}, {fillPixels.Length})", inkPixels.Length > 10 && fillPixels.Length > 10);
            if (item.Kind == ElementKind.Number)
                check(variant + " digit spans both background colors", inkPixels.Where(i => Math.Abs(i % guide.Width - 80) < 22 &&
                    Math.Abs(i / guide.Width - 64) < 22).Select(i => ground[i]).Distinct().Count() == 2);
            Colors(0, 0xFFE02030);
            using var cut = Render(); var holes = cut.Pixels;
            check(variant + " clear ink reveals the lower layer while body keeps its color",
                inkPixels.All(i => holes[i] == ground[i]) && fillPixels.All(i => holes[i] == red));
            Colors(0x80000000, 0xFFE02030);
            using var half = Render(); var translucent = half.Pixels;
            check(variant + " half-transparent ink mixes only with the lower layer", inkPixels.All(i =>
                Math.Abs(translucent[i].Red * 2 - ground[i].Red) <= 2 && Math.Abs(translucent[i].Green * 2 - ground[i].Green) <= 2 &&
                Math.Abs(translucent[i].Blue * 2 - ground[i].Blue) <= 2));
        }
        renderer.PresentationFrame = null;
        var rounded = new CanvasElement { Kind = ElementKind.Rectangle, Width = 108, Height = 94, CornerRadius = 15,
            Style = new() { Variant = ElementAppearance.ShapeIndependent, PrimaryColor = 0, SecondaryColor = 0xFF356A76,
                StrokeWidth = 10, StrokePattern = StrokePattern.Dashed } };
        using (var corner = SKBitmap.Decode(renderer.ExportPng(new() { Viewport = new(0, 0, 108, 94), Elements = [rounded] }, assets, true)))
            check("a clear dash removes the entire rounded outer edge without a hairline of fill",
                new[] { (11, 0), (8, 1), (6, 2), (4, 4), (1, 8) }.All(p => corner.GetPixel(p.Item1, p.Item2).Alpha <= 2));

        foreach (var pattern in new[] { StrokePattern.Dotted, StrokePattern.DashDot })
        {
            // A 4K-sized ellipse brings hundreds of thin marks into near tangency.
            // Sample its top edge without needing a full-size test image.
            var ellipse = new CanvasElement { Kind = ElementKind.Ellipse, Width = 2400, Height = 1100,
                Transform = Affine2D.Translation(-980, 6), Style = new() { Variant = ElementAppearance.ShapeIndependent,
                    PrimaryColor = 0xFFFFFFFF, SecondaryColor = 0, StrokePattern = pattern, StrokeWidth = 2 } };
            var strip = new SceneDocument { Viewport = new(0, 0, 440, 36), Elements = [ellipse] };
            using var marks = SKBitmap.Decode(renderer.ExportPng(strip, assets, true));
            ellipse.Style.PrimaryColor = 0;
            ellipse.Style.SecondaryColor = 0xFF0000FF;
            using var fill = SKBitmap.Decode(renderer.ExportPng(strip, assets, true));
            check($"large ellipse with thin {pattern} marks renders both material regions",
                fill.GetPixel(220, 30).Blue == 255 && marks.Pixels.Zip(fill.Pixels).Count(p => p.First.Alpha > 224 && p.Second.Alpha < 40) > 25);
        }
        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse, ElementKind.Triangle, ElementKind.Symbol })
        foreach (var pattern in Enum.GetValues<StrokePattern>())
        {
            var shape = new CanvasElement { Kind = kind, Width = 122, Height = 100, Transform = Affine2D.Translation(20, 14),
                CornerRadius = kind == ElementKind.RoundedRectangle ? 18 : null,
                Style = new() { Variant = ElementAppearance.ShapeIndependent, PrimaryColor = 0xFFFFFFFF, SecondaryColor = 0xFFE02030,
                    StrokeWidth = 10, StrokePattern = pattern } };
            var scene = new SceneDocument { Viewport = backdrop.Viewport, Elements = [..backdrop.Elements, shape] };
            SKBitmap RenderShape() => SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
            using var painted = RenderShape();
            var band = InteriorPixels(painted, painted.Pixels, SKColors.White);
            var body = InteriorPixels(painted, painted.Pixels, new SKColor(shape.Style.SecondaryColor));
            shape.Style.PrimaryColor = 0x00FFFFFF;
            using var cut = RenderShape();
            var holes = cut.Pixels; var ground = behind.Pixels;
            check($"{kind} {pattern} reserves every clear stroke mark and retains all fill and gaps", band.Length > 0 && body.Length > 0 &&
                band.All(i => holes[i] == ground[i]) && body.All(i => holes[i] == new SKColor(shape.Style.SecondaryColor)));
            shape.Style.StrokeWidth = 0;
            using var noBand = RenderShape();
            var filled = noBand.Pixels;
            check($"{kind} {pattern} zero width restores fill in the reserved band", band.All(i => filled[i] == new SKColor(shape.Style.SecondaryColor)));
        }
    }

    private static int[] InteriorPixels(SKBitmap bitmap, SKColor[] pixels, SKColor material) =>
        Enumerable.Range(bitmap.Width, pixels.Length - 2 * bitmap.Width).Where(i =>
            i % bitmap.Width > 0 && i % bitmap.Width < bitmap.Width - 1 && pixels[i] == material &&
            pixels[i - 1] == material && pixels[i + 1] == material && pixels[i - bitmap.Width] == material && pixels[i + bitmap.Width] == material).ToArray();

    private static CanvasElement Tile(double x, uint color) => new() { Kind = ElementKind.Rectangle, Width = 80, Height = 130,
        Transform = Affine2D.Translation(x, 0), Style = new() { Variant = ElementAppearance.ShapeUniform, PrimaryColor = color } };
}
