using System.Text.Json;
using FloatCanvas.Core;
using FloatCanvas.Rendering;
using SkiaSharp;

internal static class ArrowHeadRenderingTests
{
    public static void Run(Action<string, bool> check)
    {
        using var renderer = new SceneRenderer();
        var assets = new AssetLibrary();
        foreach (double degrees in new[] { 0d, 35d, 135d, 225d, 315d })
        {
            var thick = new CanvasElement { Kind = ElementKind.Arrow, Points = [new(-100, 0), new(0, 0)],
                Transform = Affine2D.Translation(150, 150).Compose(Affine2D.Rotation(degrees)),
                Style = new() { Stroke = 0xFFFF0000, StrokeWidth = 20, ArrowHeadSize = 80 } };
            var viewport = new SceneDocument { Viewport = new(0, 0, 300, 300), Elements = [thick] };
            using var result = SKBitmap.Decode(renderer.ExportPng(viewport, assets, true));
            var beyondTip = thick.Transform.TransformPoint(new(5, 0));
            check(degrees + " degree filled arrow has no shaft cap protruding beyond its pointed tip", !InkNear(result, beyondTip) && !ElementGeometry.HitTest(thick, beyondTip, 0));
            var joint = thick.Transform.TransformPoint(new(-70, 0));
            check(degrees + " degree thick arrow connects shaft and filled head", InkNear(result, joint) && ElementGeometry.HitTest(thick, joint, 0));
        }
        var shortArrow = new CanvasElement { Kind = ElementKind.DoubleArrow, Points = [new(80, 80), new(85, 80)], Style = new() { StrokeWidth = 20, ArrowHeadSize = 30 } };
        check("overlapping arrow heads do not create a reversed shaft outside their endpoints", ElementGeometry.LineShaftPoints(shortArrow).Count == 0);
        var arrow = new CanvasElement { Kind = ElementKind.Arrow, Points = [new(20, 70), new(140, 70)],
            Style = new() { Stroke = 0xFFFF0000, StrokeWidth = 2, ArrowHeadSize = 40 } };
        var scene = new SceneDocument { Viewport = new(0, 0, 180, 140), Elements = [arrow] };
        foreach (var style in Enum.GetValues<ArrowHeadStyle>().Where(s => s != ArrowHeadStyle.None))
        {
            arrow.Style.ArrowHeadStyle = style;
            using var bitmap = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
            check(style + " head draws real pixels", Count(bitmap) > 100);
            check(style + " head fill matches expected silhouette", (bitmap.GetPixel(110, 78).Alpha > 80) == (style != ArrowHeadStyle.Open));
            check(style + " diamond side pixel is distinct", (bitmap.GetPixel(120, 85).Alpha > 80) == (style == ArrowHeadStyle.Diamond));
            if (style == ArrowHeadStyle.Open)
            {
                check("open head renders its V wing", bitmap.GetPixel(120, 79).Alpha > 80);
                check("open head never closes the base", bitmap.GetPixel(100, 78).Alpha == 0);
            }
            arrow.Kind = ElementKind.DoubleArrow;
            using var doubled = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
            var leftPoint = style switch { ArrowHeadStyle.Open => new SKPointI(40, 79), ArrowHeadStyle.Diamond => new SKPointI(40, 85), _ => new SKPointI(50, 78) };
            check(style + " double arrow actually renders both ends", doubled.GetPixel(leftPoint.X, leftPoint.Y).Alpha > 80 && doubled.GetPixel(120, 70).Alpha > 80);
            arrow.Kind = ElementKind.Arrow;
        }
        long builds = renderer.PathBuildCount;
        arrow.Style.ArrowHeadStyle = ArrowHeadStyle.Open; renderer.ExportPng(scene, assets, true);
        check("arrow style change invalidates head path cache", renderer.PathBuildCount == builds + 1);
        builds = renderer.PathBuildCount;
        arrow.Style.ArrowHeadSize = 25; renderer.ExportPng(scene, assets, true);
        check("arrow size change invalidates head path cache", renderer.PathBuildCount == builds + 1);
        builds = renderer.PathBuildCount;
        arrow.Transform = Affine2D.Translation(5, 0); renderer.ExportPng(scene, assets, true);
        check("arrow transform still reuses local head paths", renderer.PathBuildCount == builds);
        arrow.Style.ArrowHeadSize = 0; arrow.Style.StrokeWidth = 1; arrow.Transform = Affine2D.Identity;
        using var small = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        builds = renderer.PathBuildCount; arrow.Style.StrokeWidth = 8;
        using var big = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("automatic head reacts to stroke width and rebuilds", renderer.PathBuildCount == builds + 1 && Count(big) > Count(small) * 3);

        arrow.Style = JsonSerializer.Deserialize<ElementStyle>("{\"Stroke\":4294901760,\"StrokeWidth\":4}")!;
        byte[] legacy = renderer.ExportPng(scene, assets, true);
        arrow.Style = new() { Stroke = 0xFFFF0000, StrokeWidth = 4, ArrowHeadStyle = ArrowHeadStyle.FilledTriangle, ArrowHeadSize = 0 };
        check("old JSON retains byte-identical default arrow rendering", legacy.AsSpan().SequenceEqual(renderer.ExportPng(scene, assets, true)));

        arrow.Points = [new(0, 0), new(90, 0)]; arrow.Style.ArrowHeadStyle = ArrowHeadStyle.Open; arrow.Style.ArrowHeadSize = 30; arrow.Style.StrokeWidth = 2;
        arrow.Transform = Affine2D.Translation(30, 20).Compose(Affine2D.Rotation(35)).Compose(Affine2D.Scale(1.1, .6));
        using var transformed = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        var wing = arrow.Transform.TransformPoint(new(75, 6.75));
        var hole = arrow.Transform.TransformPoint(new(60, 5));
        check("transformed open wing raster and exact hit agree", InkNear(transformed, wing) && ElementGeometry.HitTest(arrow, wing, 0));
        check("transformed open hole raster and exact hit agree", !InkNear(transformed, hole) && !ElementGeometry.HitTest(arrow, hole, 0));
        arrow.Style.StrokeWidth = 0;
        using var invisible = SKBitmap.Decode(renderer.ExportPng(scene, assets, true));
        check("zero-width arrow has no raster or ghost head hit", Count(invisible) == 0 && !ElementGeometry.HitTest(arrow, wing, 0));
    }
    private static int Count(SKBitmap bitmap)
    { int total = 0; for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) if (bitmap.GetPixel(x, y).Alpha > 80) total++; return total; }
    private static bool InkNear(SKBitmap bitmap, PointD point)
    {
        for (var y = (int)Math.Floor(point.Y) - 1; y <= (int)Math.Floor(point.Y) + 1; y++)
            for (var x = (int)Math.Floor(point.X) - 1; x <= (int)Math.Floor(point.X) + 1; x++)
                if (x >= 0 && x < bitmap.Width && y >= 0 && y < bitmap.Height && bitmap.GetPixel(x, y).Alpha > 80) return true;
        return false;
    }
}
