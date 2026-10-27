using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class DashedContourRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var rectangle = new CanvasElement
        {
            Kind = ElementKind.Rectangle, Width = 300, Height = 180, CornerRadius = 60,
            Transform = Affine2D.Translation(20, 20),
            Style = new() { StrokeWidth = 4, StrokePattern = StrokePattern.Dashed }
        };
        var document = new SceneDocument { Viewport = new(0, 0, 340, 220), Elements = [rectangle] };
        rectangle.Style.StrokePattern = StrokePattern.Solid;
        byte[] originalSolid = renderer.ExportPng(document, new(), true);
        foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.RoundedRectangle, ElementKind.Ellipse })
        foreach (var pattern in new[] { StrokePattern.Dashed, StrokePattern.Dotted })
        {
            rectangle.Kind = kind; rectangle.Style.StrokePattern = pattern;
            var solid = rectangle.Clone(); solid.Style.StrokePattern = StrokePattern.Solid;
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(document, new(), true));
            int ink = 0, missedInk = 0, clearGaps = 0;
            for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var point = new PointD(x + .5, y + .5);
                if (bitmap.GetPixel(x, y).Alpha > 220)
                {
                    ink++;
                    if (!ElementGeometry.HitTest(rectangle, point)) missedInk++;
                }
                // Sample gaps on a two-pixel-wide band around the undashed contour.
                // This ensures the fix does not simply make the whole outline clickable.
                else if ((x % 4 == 0 || y % 4 == 0) && bitmap.GetPixel(x, y).Alpha == 0 &&
                    ElementGeometry.HitTest(solid, point, 0) && !ElementGeometry.HitTest(rectangle, point, 0))
                {
                    clearGaps++;
                }
            }
            check($"{kind} {pattern}: rendered ink remains selectable with normal tolerance ({missedInk} misses)", ink > 100 && missedInk == 0);
            check($"{kind} {pattern}: actual dash gaps remain non-selectable", clearGaps > 40);
        }

        rectangle.Kind = ElementKind.Rectangle; rectangle.Style.StrokePattern = StrokePattern.Solid;
        byte[] returnedSolid = renderer.ExportPng(document, new(), true);
        check("switching from patterned to solid restores identical native curve pixels", originalSolid.AsSpan().SequenceEqual(returnedSolid));
        using var solidBitmap = SKBitmap.Decode(returnedSolid);
        check("solid rounded outline retains smooth native curve and hollow center", solidBitmap.GetPixel(97, 22).Alpha > 220 &&
            solidBitmap.GetPixel(20, 20).Alpha == 0 && solidBitmap.GetPixel(170, 110).Alpha == 0);
        rectangle.Style.Fill = 0xFF4466CC;
        using var filledBitmap = SKBitmap.Decode(renderer.ExportPng(document, new(), true));
        check("solid rounded fill remains unchanged", filledBitmap.GetPixel(170, 110).Alpha == 255 && filledBitmap.GetPixel(20, 20).Alpha == 0);
        rectangle.Style.Stroke = 0;
        byte[] solidFill = renderer.ExportPng(document, new(), true);
        rectangle.Style.StrokePattern = StrokePattern.Dotted;
        check("changing stroke pattern does not alter native fill pixels", solidFill.AsSpan().SequenceEqual(renderer.ExportPng(document, new(), true)));

        rectangle.Style.Stroke = 0xFFDD3366; rectangle.Style.Fill = 0; rectangle.Style.StrokeWidth = 8; rectangle.Style.StrokePattern = StrokePattern.Dashed;
        using var wideBitmap = SKBitmap.Decode(renderer.ExportPng(document, new(), true));
        int wideMisses = 0;
        for (int y = 0; y < wideBitmap.Height; y++)
        for (int x = 0; x < wideBitmap.Width; x++)
            if (wideBitmap.GetPixel(x, y).Alpha > 220 && !ElementGeometry.HitTest(rectangle, new(x + .5, y + .5))) wideMisses++;
        check($"wide rounded dashes remain selectable without increasing default tolerance ({wideMisses} misses)", wideMisses == 0);

        rectangle.Height = 120;
        // Keep the collapsed edge inside a real gap on the inward centerline.
        rectangle.Style.StrokeWidth = 16;
        using var capsule = SKBitmap.Decode(renderer.ExportPng(document, new(), true));
        check("a collapsed straight edge on a capsule does not invent ink in a dash gap", capsule.GetPixel(319, 79).Alpha == 0 &&
            !ElementGeometry.HitTest(rectangle, new(319.5, 79.5), 0));
    }
}
