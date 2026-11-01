using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class CanvasRectificationTests
{
    internal static void Run(Action<string, bool> check)
    {
        foreach (var offset in new[] { new PointD(), new PointD(-500, -700) })
        {
            var projection = new CanvasProjectionSettings { PlaneBounds = new RectD(0, 0, 210, 297), PinPlanePoint = new(0, 297) };
            PointD[] quad = [new(40, 50), new(300, 30), new(350, 450), new(15, 420)];
            quad = quad.Select(p => p + offset).ToArray();
            check("paper fixture has valid independent perspective", CanvasProjectionGeometry.TryCalibrate(projection, quad, out projection, out _));
            using var source = SKSurface.Create(new SKImageInfo(400, 500)); source.Canvas.Clear(SKColors.Transparent);
            source.Canvas.Translate(-(float)offset.X, -(float)offset.Y);
            var matrix = new SKMatrix((float)projection.Matrix.M11, (float)projection.Matrix.M12, (float)projection.Matrix.M13,
                (float)projection.Matrix.M21, (float)projection.Matrix.M22, (float)projection.Matrix.M23,
                (float)projection.Matrix.M31, (float)projection.Matrix.M32, (float)projection.Matrix.M33);
            source.Canvas.Concat(in matrix);
            using var paint = new SKPaint { Color = SKColors.Red };
            source.Canvas.DrawRect(0, 0, 105, 148.5f, paint); paint.Color = SKColors.Blue;
            source.Canvas.DrawRect(105, 0, 105, 148.5f, paint); paint.Color = SKColors.Green;
            source.Canvas.DrawRect(0, 148.5f, 105, 148.5f, paint); paint.Color = SKColors.Yellow;
            source.Canvas.DrawRect(105, 148.5f, 105, 148.5f, paint);
            using var image = source.Snapshot(); using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] original = png.ToArray(); var rasterBounds = new RectD(offset.X, offset.Y, 400, 500);
            using var rectified = SKBitmap.Decode(CanvasRectificationRenderer.Render(original, rasterBounds, projection));
            check("rectification produces the specified A4 source dimensions", rectified.Width == 210 && rectified.Height == 297);
            check("inverse perspective recovers all four paper quadrants without swapping or stretching axes",
                rectified.GetPixel(50, 70) == SKColors.Red && rectified.GetPixel(160, 70) == SKColors.Blue &&
                rectified.GetPixel(50, 220) == SKColors.Green && rectified.GetPixel(160, 220) == SKColors.Yellow);
            using var small = SKBitmap.Decode(CanvasRectificationRenderer.Render(original, rasterBounds, projection, 100));
            check("preview allocation is bounded while preserving target ratio to raster precision", Math.Max(small.Width, small.Height) == 100 &&
                Math.Abs((double)small.Width / small.Height - 210.0 / 297) < .01);
        }
        var viewport = new RectD(0, 0, 900, 700);
        var document = new SceneDocument { Viewport = viewport, Canvas = new() { Projection = CanvasCalibration.CreateInitial(viewport), Grid = new() { Visible = true, Color = 0xFFFF0000 } } };
        var quadLegacy = document.Canvas.Projection.Quad.ToArray(); quadLegacy[0] += new PointD(60, 30);
        CanvasCalibration.TryCalibrate(document.Canvas.Projection, quadLegacy, viewport, out var legacy, out _); document.Canvas.Projection = legacy;
        document.Elements.Add(new() { Kind = ElementKind.Rectangle, Width = 80, Height = 40, Transform = Affine2D.Translation(300, 300), Style = new() { Fill = 0xFF0055FF } });
        using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        using var before = SKBitmap.Decode(renderer.ExportPng(document, assets));
        CanvasGeometryMigration.UpgradeLegacy(document.Canvas);
        using var after = SKBitmap.Decode(renderer.ExportPng(document, assets));
        check("legacy geometry adoption reproduces the exact rendered grid and content pixels", before.Pixels.SequenceEqual(after.Pixels));
        var matrixBefore = document.Canvas.Projection.Matrix; document.Canvas.Grid.PlaneCellSize = 23; document.Canvas.Grid.PlaneOrigin = new(100, 200);
        using var changed = SKBitmap.Decode(renderer.ExportPng(document, assets));
        check("independent grid edits visibly alter the grid while retaining projection", document.Canvas.Projection.Matrix == matrixBefore && !changed.Pixels.SequenceEqual(after.Pixels));
    }
}
