using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;
using System.Text.Json;

internal static class CanvasPinRenderingTests
{
    internal static void Run(Action<string, bool> check)
    {
        var scene = Scene(); using var renderer = new SceneRenderer(); var assets = new AssetLibrary();
        using var grid = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        for (int i = 1; i < 9; i++)
        {
            check("900x600 9x9 renders every full column " + i, Near(grid, new(50 + 100 * i, 333), c => c.Green > 180));
            check("900x600 9x9 renders every full row " + i, Near(grid, new(377, 50 + 600.0 * i / 9), c => c.Green > 180));
        }
        scene.Canvas.Grid.Visible = false;
        using var surface = SKSurface.Create(new SKImageInfo(1000, 800)); surface.Canvas.Clear(SKColors.Transparent);
        renderer.Draw(surface.Canvas, scene, assets, calibrationReference: true);
        using var image = surface.Snapshot(); using var reference = SKBitmap.FromImage(image);
        check("reference grid uses the same pin phase", Near(reference, new(150, 333), c => c.Alpha > 40));
        var shape = new CanvasElement { Kind = ElementKind.Rectangle, Width = 100, Height = 100,
            Transform = Affine2D.Translation(50, 550), Style = new() { Fill = 0xFFFF4000, StrokeWidth = 0 } };
        scene.Elements.Add(shape); string original = JsonSerializer.Serialize(shape);
        using var before = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        var projection = scene.Canvas.Projection;
        check("pinned element bottom and pin share render coordinates", CanvasMapping.Create(scene).ElementToScreen(shape).TransformPoint(new(0, 100))
            .DistanceTo(CanvasMapping.Create(scene).ToScreen(projection.PinPoint)) < 1e-7);
        var draft = projection.Clone(); draft.Rows = 6;
        check("count change calibrates", CanvasCalibration.TryCalibrate(draft, projection.Quad, scene.Viewport, out var next, out _));
        scene.Canvas.Projection = next;
        using var after = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("same renderer invalidates projection caches for count changes", !before.Bytes.AsSpan().SequenceEqual(after.Bytes));
        check("rendering and count changes preserve logical layer values", JsonSerializer.Serialize(shape) == original);
        CanvasCalibration.SetEnabled(scene.Canvas, false);
        using var flat = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        var sample = CanvasMapping.Create(scene).ElementToScreen(shape).TransformPoint(new(2, 2));
        check("flat renderer uses the pin-aligned ordinary placement", Near(flat, sample, c => c.Red > 180));
        scene.Elements.Clear(); scene.Canvas.Grid.Visible = true;
        scene.Canvas.Grid.FlatOrigin = scene.Viewport.Center;
        foreach (var translation in new[] { new PointD(), new PointD(123, -57) })
        foreach (double size in new[] { 73.1, 91.3 })
        {
            scene.Canvas.Grid.FlatTranslation = translation; scene.Canvas.Grid.FlatCellSize = size;
            using var centered = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
            foreach (var point in new[] { new PointD(500, 333), new PointD(377, 400), new PointD(500 + size, 333), new PointD(377, 400 + size) })
                check("flat grid pixels stay centered while size or layer placement changes", Near(centered, point, c => c.Green > 180));
        }
    }
    internal static void WriteEvidence(string output)
    {
        Directory.CreateDirectory(output); using var renderer = new SceneRenderer();
        File.WriteAllBytes(Path.Combine(output, "pin-9x9.png"), renderer.ExportPng(Scene(), new AssetLibrary(), true));
    }
    private static SceneDocument Scene()
    {
        var scene = new SceneDocument { Viewport = new(0, 0, 1000, 800) };
        var p = CanvasCalibration.CreateInitial(scene.Viewport, 9, 9); p.ReferenceBounds = new(50, -100, 900, 600);
        CanvasCalibration.TryCalibrate(p, [new(50, 50), new(950, 50), new(950, 650), new(50, 650)], scene.Viewport, out var value, out _);
        scene.Canvas.Projection = value; scene.Canvas.Grid.Visible = true; scene.Canvas.Grid.Color = 0xFF00FF00; return scene;
    }
    private static bool Near(SKBitmap bitmap, PointD p, Func<SKColor, bool> predicate)
    {
        for (int x = (int)p.X - 2; x <= (int)p.X + 2; x++)
        for (int y = (int)p.Y - 2; y <= (int)p.Y + 2; y++)
            if (x >= 0 && y >= 0 && x < bitmap.Width && y < bitmap.Height && predicate(bitmap.GetPixel(x, y))) return true;
        return false;
    }
}
