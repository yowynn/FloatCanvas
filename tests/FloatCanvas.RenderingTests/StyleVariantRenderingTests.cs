using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class StyleVariantRenderingTests
{
    internal static void Run(Action<string, bool> check, string? output = null)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        var badge = new CanvasElement { Kind = ElementKind.Number, Number = 8, Width = 100, Height = 100, NumberUsesForeground = true,
            Transform = Affine2D.Translation(10, 10), Style = new() { Stroke = 0xFF245BC3, Fill = 0xFFFFCC33, StrokeWidth = 5, Variant = ElementAppearance.NumberFilled } };
        byte[] Render(CanvasElement element) => renderer.ExportPng(new() { Viewport = new(0, 0, 120, 120), Elements = [element] }, assets, true);
        TextStyleRenderingTests.Run(renderer, check, output);
        byte[] filledBytes = Render(badge);
        using var filled = SKBitmap.Decode(filledBytes);
        ElementAppearance.SetVariant(badge, ElementAppearance.NumberOutline);
        badge.Style.SecondaryColor = 0;
        byte[] outlineBytes = Render(badge);
        using var outline = SKBitmap.Decode(outlineBytes);
        check("filled badge paints its primary body", filled.GetPixel(28, 60).Alpha == 255);
        check("outline badge leaves its interior transparent", outline.GetPixel(28, 60).Alpha == 0);
        check("outline badge paints a primary-color ring", outline.GetPixel(12, 60).Blue > outline.GetPixel(12, 60).Red && outline.GetPixel(12, 60).Alpha > 200);
        check("outline badge keeps all paint inside its local bounds", outline.GetPixel(8, 60).Alpha == 0 && outline.GetPixel(60, 8).Alpha == 0);
        bool textInk = false, secondaryInk = false;
        for (int y = 30; y < 90; y++) for (int x = 40; x < 80; x++)
        {
            var pixel = outline.GetPixel(x, y);
            textInk |= pixel.Alpha > 200 && pixel.Blue > pixel.Red;
            secondaryInk |= pixel.Alpha > 200 && pixel.Red > 180;
        }
        check("outline label is visible in primary rather than hidden secondary", textInk && !secondaryInk);
        var oldBadge = badge.Clone(); oldBadge.Style.Variant = null; oldBadge.NumberUsesForeground = false; oldBadge.Style.FontSize = 8;
        byte[] oldBytes = Render(oldBadge);
        ElementAppearance.SetPrimaryColor(oldBadge, ElementAppearance.PrimaryColor(oldBadge));
        check("normalizing legacy color roles preserves exact rendered pixels and small font", oldBytes.SequenceEqual(Render(oldBadge)));
        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse, ElementKind.Symbol })
        {
            var shape = new CanvasElement { Kind = kind, Width = 100, Height = 100, Transform = Affine2D.Translation(10, 10),
                Style = new() { Variant = ElementAppearance.ShapeUniform, PrimaryColor = 0x80FF3311,
                    SecondaryColor = 0xFF1122EE, StrokeWidth = 18, StrokePattern = StrokePattern.DashDot } };
            var solid = Render(shape);
            using var pixels = SKBitmap.Decode(solid);
            check(kind + " solid paints primary fill once", pixels.GetPixel(60, 60) is { Alpha: 128, Red: > 245 });
            shape.Style.StrokeWidth = 0; shape.Style.StrokePattern = StrokePattern.Solid;
            check(kind + " hidden stroke settings do not affect solid ink", solid.SequenceEqual(Render(shape)));
            shape.Style.Variant = ElementAppearance.ShapeIndependent; shape.Style.StrokeWidth = 7;
            using var border = SKBitmap.Decode(Render(shape));
            check(kind + " outlined uses secondary fill and primary stroke", border.GetPixel(60, 60).Blue > 220 &&
                border.Pixels.Any(pixel => pixel.Red > 245 && pixel.Blue < 30 && pixel.Alpha == 128));
        }
        if (output is not null)
        {
            Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "number-filled.png"), filledBytes);
            File.WriteAllBytes(Path.Combine(output, "number-outline.png"), outlineBytes);
        }
    }
}
