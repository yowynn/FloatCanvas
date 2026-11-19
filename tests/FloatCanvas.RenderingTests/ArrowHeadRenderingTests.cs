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
        var diamonds = new CanvasElement
        {
            Kind = ElementKind.Line, Points = [new(24, 40), new(168, 40)],
            Style = new() { Foreground = 0x80FF0000, StrokeWidth = 12, StartArrowHead = ArrowHeadStyle.Diamond, EndArrowHead = ArrowHeadStyle.Diamond }
        };
        using (var diamondInk = SKBitmap.Decode(renderer.ExportPng(
            new() { Viewport = new(0, 0, 200, 80), Elements = [diamonds] }, assets, true)))
        {
            check("automatic diamond heads meet the full-width translucent shaft without a round-cap notch",
                Enumerable.Range(66, 60).All(x => diamondInk.GetPixel(x, 35).Alpha == 128 && diamondInk.GetPixel(x, 44).Alpha == 128));
            check("diamond joints retain single coverage and exact picking",
                diamondInk.Pixels.All(pixel => pixel.Alpha <= 128) &&
                ElementGeometry.HitTest(diamonds, new(66.5, 35.5), 0) && ElementGeometry.HitTest(diamonds, new(125.5, 44.5), 0));
        }
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
        // A standalone legacy style needs its owning kind before color migration.
        AppearanceMigration.MigrateElement(arrow, new(0xFFFF0000, 0, false, null), numberUsesForeground: false);
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

        var arc = new CanvasElement
        {
            Kind = ElementKind.Line, Points = [new(30, 90), new(230, 90)],
            Style = new() { Foreground = 0x80FF0000, StrokeWidth = 8, LineArcBulge = .2,
                StartArrowHead = ArrowHeadStyle.Diamond, EndArrowHead = ArrowHeadStyle.FilledTriangle }
        };
        var arcScene = new SceneDocument { Viewport = new(0, 0, 260, 180), Elements = [arc] };
        using var lower = SKBitmap.Decode(renderer.ExportPng(arcScene, assets, true));
        check("a translucent arc renders its midpoint and retains single ink coverage at its heads",
            lower.GetPixel(130, 130).Alpha == 128 && lower.GetPixel(130, 90).Alpha == 0 &&
            lower.Pixels.Max(pixel => pixel.Alpha) == 128);
        arc.Style.LineArcBulge = -.2;
        arc.Style.StrokePattern = StrokePattern.DashDot;
        using var upper = SKBitmap.Decode(renderer.ExportPng(arcScene, assets, true));
        check("changing an existing arc flips its cached outline and follows the new arc with the patterned stroke",
            Enumerable.Range(100, 61).Any(x => upper.GetPixel(x, 52).Alpha > 0) &&
            Enumerable.Range(100, 61).All(x => upper.GetPixel(x, 130).Alpha == 0));

        arc.Style.StrokePattern = StrokePattern.Solid;
        using var centered = SKBitmap.Decode(renderer.ExportPng(arcScene, assets, true));
        arc.Style.LineArcOffset = .25;
        using var offset = SKBitmap.Decode(renderer.ExportPng(arcScene, assets, true));
        check("sideways handle movement invalidates cached arc ink even with the same bulge",
            centered.GetPixel(180, 50).Alpha == 0 && offset.GetPixel(180, 50).Alpha == 128 &&
            offset.Pixels.Max(pixel => pixel.Alpha) == 128);

        arc.Points = [new(35, 140), new(245, 140)];
        arc.Style.LineArcOffset = 0;
        arc.Style.LineArcBulge = -.5;
        arc.Style.StrokeWidth = 20;
        arc.Style.EndArrowHead = ArrowHeadStyle.Diamond;
        arc.Style.StrokePattern = StrokePattern.Solid;
        var center = new PointD(140, 140);
        var shaft = ElementGeometry.LineShaftPoints(arc);
        foreach (bool atStart in new[] { true, false })
        {
            var head = ElementGeometry.ArrowHead(arc, atStart);
            var end = atStart ? shaft[0] : shaft[^1];
            check($"a tight arc meets the rear diamond vertex on its centerline, start={atStart}",
                Math.Abs(head[2].DistanceTo(center) - 105) < .05);
            check($"the curved shaft's last round cap is entirely inside the diamond, start={atStart}",
                Enumerable.Range(0, 32).All(i =>
                {
                    var (sin, cos) = Math.SinCos(i * Math.Tau / 32);
                    return ElementGeometry.PointInPolygon(end + new PointD(cos, sin) * 9.8, head);
                }));
        }

        arc.Style.LineArcOffset = .32;
        arc.Style.LineArcBulge = -.57;
        shaft = ElementGeometry.LineShaftPoints(arc);
        check("both diamond heads cover the shaft ends of a tilted half ellipse",
            ElementGeometry.PointInPolygon(shaft[0], ElementGeometry.ArrowHead(arc, true)) &&
            ElementGeometry.PointInPolygon(shaft[^1], ElementGeometry.ArrowHead(arc)));
        using var ellipse = SKBitmap.Decode(renderer.ExportPng(arcScene, assets, true));
        check("a translucent half ellipse reaches the free handle and composites its heads only once",
            ellipse.GetPixel(207, 20).Alpha == 128 && ellipse.Pixels.Max(pixel => pixel.Alpha) == 128);

        arcScene.Viewport = new(0, 0, 450, 180);
        arc.Points = [new(43, 68), new(316, 68)];
        (arc.Style.LineArcOffset, arc.Style.LineArcBulge) = ElementGeometry.LineArcCoordinatesAt(arc, new(386, 123), false);
        arc.Style.StrokeWidth = 4;
        using var returning = SKBitmap.Decode(renderer.ExportPng(arcScene, assets, true));
        check("the visible stroke passes C and turns back eleven pixels beyond it",
            returning.GetPixel(386, 123).Alpha == 128 && returning.GetPixel(397, 114).Alpha == 128 &&
            returning.GetPixel(401, 114).Alpha == 0);
        DashedArcWithOtherLines(renderer, assets, check);

        var patterned = new CanvasElement
        {
            Kind = ElementKind.Line, Points = [new(30, 80), new(230, 80)],
            Style = new() { Foreground = 0x80E03E3E, StrokeWidth = 4,
                StartArrowHead = ArrowHeadStyle.Open, EndArrowHead = ArrowHeadStyle.Diamond }
        };
        var patternedScene = new SceneDocument { Viewport = new(0, 0, 260, 160), Elements = [patterned] };
        foreach (var pattern in new[] { StrokePattern.Dashed, StrokePattern.Dotted, StrokePattern.DashDot })
        {
            patterned.Style.StrokePattern = pattern;
            using var pixels = SKBitmap.Decode(renderer.ExportPng(patternedScene, assets, true));
            check($"{pattern} shaft and both arrow types keep their silhouettes with single translucent coverage",
                pixels.GetPixel(30, 80).Alpha == 128 && pixels.GetPixel(43, 85).Alpha == 128 &&
                pixels.GetPixel(222, 80).Alpha == 128 && pixels.Pixels.Max(pixel => pixel.Alpha) == 128);
        }
    }

    private static void DashedArcWithOtherLines(SceneRenderer renderer, AssetLibrary assets, Action<string, bool> check)
    {
        var arc = new CanvasElement
        {
            Kind = ElementKind.Line, Points = [default, new(653, -255)], Transform = Affine2D.Translation(50, 572),
            Style = new() { Foreground = 0xFFE03E3E, StrokeWidth = 4, StrokePattern = StrokePattern.Dashed,
                LineArcOffset = -.03162581343578176, LineArcBulge = -.2641111522605275 }
        };
        var crossing = new CanvasElement
        {
            Kind = ElementKind.Line, Points = [new(10, 380), new(780, 300)],
            Style = new() { Foreground = 0xFF2378E2, StrokeWidth = 4, EndArrowHead = ArrowHeadStyle.FilledTriangle }
        };
        var scene = new SceneDocument { Viewport = new(0, 0, 800, 650), Elements = [crossing, arc] };
        using var bitmap = new SKBitmap(800, 650);
        using var canvas = new SKCanvas(bitmap);
        int lostDashes = 0, filledGaps = 0, checkedDashes = 0;
        for (int frame = 0; frame < 25; frame++)
        {
            arc.Style.LineArcOffset = -.03162581343578176 - frame * .004;
            arc.Style.LineArcBulge = -.2641111522605275 - frame * .0015;
            canvas.Clear(SKColors.Transparent);
            renderer.Draw(canvas, scene, assets, includeBackground: false, includeCanvasBackground: false);
            canvas.Flush();
            var center = ElementGeometry.LineShaftPoints(arc);
            double next = 4, offset = 0;
            bool expectInk = true;
            for (int i = 1; i < center.Count; i++)
            {
                var a = center[i - 1]; var b = center[i];
                double length = a.DistanceTo(b);
                while (next < offset + length)
                {
                    var p = arc.Transform.TransformPoint(a + (b - a) * ((next - offset) / length));
                    var pixel = bitmap.GetPixel((int)p.X, (int)p.Y);
                    bool redInk = pixel.Alpha >= 128 && pixel.Red > pixel.Blue;
                    if (expectInk && !redInk) lostDashes++;
                    if (!expectInk && redInk) filledGaps++;
                    if (expectInk) checkedDashes++;
                    next += 8;
                    expectInk = !expectInk;
                }
                offset += length;
            }
        }
        check($"dragging a dashed arc across other lines retains its {checkedDashes} dashes ({lostDashes} missing, {filledGaps} filled gaps)",
            checkedDashes > 1000 && lostDashes == 0 && filledGaps == 0);
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
